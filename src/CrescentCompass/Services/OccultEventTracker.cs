using System.Numerics;
using CrescentCompass.Core;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using LuminaDynamicEvent = Lumina.Excel.Sheets.DynamicEvent;

namespace CrescentCompass.Services;

public sealed unsafe class OccultEventTracker : IDisposable
{
    private const long CrowdSourceRefreshIntervalMilliseconds = 30_000;
    private static readonly HashSet<uint> MagicPotIds = [1976, 1977, 2072, 2073];
    private static readonly HashSet<uint> ForkTowerIds = [48, 64, 65];
    private readonly IFateTable fateTable;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly ZoneServerIdReader zoneServerIdReader;
    private readonly List<OccultEventSnapshot> activeEvents = [];
    private MagicPotObservation? magicPotObservation;
    private IReadOnlyDictionary<uint, CeSpawnObservation> crowdSourceCeSpawnObservations =
        new Dictionary<uint, CeSpawnObservation>();
    private readonly Dictionary<uint, CeSpawnObservation> localCeSpawnObservations = [];
    private uint observationTerritory;
    private uint queriedZoneServerId;
    private int queryGeneration;
    private bool crowdSourceLookupStarted;
    private bool hasLocalMagicPotObservation;
    private CancellationTokenSource? crowdSourceCancellation;
    private PendingCrowdSourceResult? pendingCrowdSourceResult;
    private long nextRefresh;
    private long nextCrowdSourceRefresh;
    private bool disposed;

    public OccultEventTracker(
        IFateTable fateTable,
        IClientState clientState,
        IDataManager dataManager,
        IFramework framework,
        IPlayerState playerState,
        ZoneServerIdReader zoneServerIdReader)
    {
        this.fateTable = fateTable;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.framework = framework;
        this.playerState = playerState;
        this.zoneServerIdReader = zoneServerIdReader;
        framework.Update += OnFrameworkUpdate;
        Refresh();
    }

    public IReadOnlyList<OccultEventSnapshot> ActiveEvents => activeEvents;
    public uint ZoneServerId => zoneServerIdReader.Value;
    public string ForecastStatus { get; private set; } = "众包：等待当前实例编号";

    public bool TryGetCeSpawnObservation(uint eventId, out CeSpawnObservation observation)
    {
        var hasLocal = localCeSpawnObservations.TryGetValue(eventId, out var local);
        var hasCrowdSource = crowdSourceCeSpawnObservations.TryGetValue(eventId, out var crowdSource);
        observation = (hasLocal, hasCrowdSource) switch
        {
            (true, true) => local.SpawnedAt >= crowdSource.SpawnedAt ? local : crowdSource,
            (true, false) => local,
            (false, true) => crowdSource,
            _ => default
        };
        return hasLocal || hasCrowdSource;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        queryGeneration++;
        crowdSourceCancellation?.Cancel();
        crowdSourceCancellation?.Dispose();
        activeEvents.Clear();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now < nextRefresh) return;
        nextRefresh = now + 500;
        Refresh();
    }

    private void Refresh()
    {
        ConsumeCrowdSourceResult();
        activeEvents.Clear();
        var territoryId = clientState.TerritoryType;
        if (territoryId is not PotCandidateCatalog.SouthHornTerritoryId and not PotCandidateCatalog.NorthHornTerritoryId)
        {
            magicPotObservation = null;
            observationTerritory = 0;
            ResetForecastState();
            return;
        }
        if (observationTerritory != territoryId)
        {
            ResetForecastState();
            observationTerritory = territoryId;
        }

        var zoneServerId = ZoneServerId;
        if (queriedZoneServerId != 0 && zoneServerId != 0 && queriedZoneServerId != zoneServerId)
            ResetForecastState();
        TryStartCrowdSourceLookup(territoryId, zoneServerId);

        foreach (var fate in fateTable)
        {
            if (fate.MapIconId == 0 ||
                fate.State is FateState.Ended or FateState.Ending or FateState.Failed ||
                fate.Position == default || !PotPredictionSession.IsFinite(fate.Position))
                continue;

            var kind = MagicPotIds.Contains(fate.FateId) ? OccultEventKind.MagicPot : OccultEventKind.Fate;
            if (kind == OccultEventKind.MagicPot)
            {
                magicPotObservation = new(fate.FateId, fate.StartTimeEpoch, "本机观察");
                hasLocalMagicPotObservation = true;
            }
            activeEvents.Add(new(
                fate.FateId,
                fate.Name.ToString(),
                fate.Position,
                fate.MapIconId,
                kind,
                $"进度 {fate.Progress}% · 剩余 {FormatDuration(fate.TimeRemaining)}",
                OccultEventRewardCatalog.FateTag(territoryId, fate.FateId),
                true));
        }

        AddMagicPotForecast(territoryId);

        var publicContent = PublicContentOccultCrescent.GetInstance();
        if (publicContent == null) return;
        foreach (ref var dynamicEvent in publicContent->DynamicEventContainer.Events)
        {
            if (dynamicEvent.State == DynamicEventState.Inactive) continue;
            var id = dynamicEvent.DynamicEventId;
            var kind = ForkTowerIds.Contains(id) ? OccultEventKind.ForkTower : OccultEventKind.CriticalEngagement;
            if (kind == OccultEventKind.CriticalEngagement)
                localCeSpawnObservations[id] = new(
                    id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "本机观察");
            var position = dynamicEvent.MapMarker.Position;
            if ((position == default || !PotPredictionSession.IsFinite(position)) &&
                kind == OccultEventKind.ForkTower && ForkTowerCatalog.TryGetDefinition(territoryId, out var tower))
                position = MapToWorld(tower.MapPosition);
            if (position == default || !PotPredictionSession.IsFinite(position)) continue;
            var iconId = 0u;
            var eventRow = dataManager.GetExcelSheet<LuminaDynamicEvent>().GetRowOrDefault(id);
            if (eventRow is { } row)
                iconId = row.EventType.Value.IconObjective0;

            var stateText = dynamicEvent.State switch
            {
                DynamicEventState.Register =>
                    $"报名中 · 剩余 {FormatDuration(Math.Max(0, dynamicEvent.StartTimestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds()))}",
                DynamicEventState.Warmup => "准备中",
                DynamicEventState.Battle => $"战斗中 · 进度 {dynamicEvent.Progress}%",
                _ => dynamicEvent.State.ToString()
            };
            activeEvents.Add(new(
                id,
                dynamicEvent.Name.ToString(),
                position,
                iconId,
                kind,
                stateText,
                kind == OccultEventKind.CriticalEngagement
                    ? OccultEventRewardCatalog.CriticalEncounterTag(territoryId, id)
                    : string.Empty,
                dynamicEvent.State is DynamicEventState.Register or DynamicEventState.Warmup));
        }
    }

    private void AddMagicPotForecast(uint territoryId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (magicPotObservation is not { } observation ||
            !MagicPotForecastSchedule.TryCreate(territoryId, observation, now, out var forecast))
        {
            if (magicPotObservation != null)
            {
                ForecastStatus = $"实例 {ZoneServerId} · {AreaName(territoryId)} · 基准记录已过期，正在刷新众包";
                if (!hasLocalMagicPotObservation && Environment.TickCount64 >= nextCrowdSourceRefresh)
                {
                    nextCrowdSourceRefresh = Environment.TickCount64 + CrowdSourceRefreshIntervalMilliseconds;
                    crowdSourceLookupStarted = false;
                }
            }
            return;
        }
        var seconds = forecast.PredictedAt - now;
        var predictedLocal = DateTimeOffset.FromUnixTimeSeconds(forecast.PredictedAt).ToLocalTime();
        ForecastStatus = $"实例 {ZoneServerId} · {AreaName(territoryId)}\n预测 {forecast.Definition.Name} {predictedLocal:HH:mm:ss} · {forecast.Source}";
        if (!MagicPotForecastSchedule.ShouldShow(forecast, now)) return;
        activeEvents.Add(new(
            forecast.Definition.FateId,
            forecast.Definition.Name,
            MapToWorld(forecast.Definition.MapPosition),
            forecast.Definition.IconId,
            OccultEventKind.MagicPotForecast,
            $"预计 {predictedLocal:HH:mm:ss} 出现 · {(seconds > 0 ? $"还有 {FormatDuration(seconds)}" : "预计时间已到")} · 基准：{forecast.Source}"));
    }

    private void TryStartCrowdSourceLookup(uint territoryId, uint zoneServerId)
    {
        if (crowdSourceLookupStarted || zoneServerId == 0 ||
            Environment.TickCount64 < nextCrowdSourceRefresh) return;
        var dataCenterId = playerState.CurrentWorld.Value.DataCenter.RowId;
        if (dataCenterId == 0) return;
        crowdSourceLookupStarted = true;
        nextCrowdSourceRefresh = Environment.TickCount64 + CrowdSourceRefreshIntervalMilliseconds;
        queriedZoneServerId = zoneServerId;
        ForecastStatus = $"众包：正在查询实例 {zoneServerId}…";
        var generation = queryGeneration;
        var cancellation = new CancellationTokenSource();
        crowdSourceCancellation = cancellation;
        _ = CrowdSourceMagicPotClient.QueryAsync(dataCenterId, zoneServerId, territoryId, cancellation.Token)
            .ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) return;
                Interlocked.Exchange(ref pendingCrowdSourceResult,
                    new PendingCrowdSourceResult(generation, task.Result));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ConsumeCrowdSourceResult()
    {
        var pending = Interlocked.Exchange(ref pendingCrowdSourceResult, null);
        if (pending == null || pending.Generation != queryGeneration) return;
        crowdSourceCancellation?.Dispose();
        crowdSourceCancellation = null;
        crowdSourceLookupStarted = false;
        ForecastStatus = pending.Result.Status;
        crowdSourceCeSpawnObservations = pending.Result.CeObservations ??
                                         new Dictionary<uint, CeSpawnObservation>();
        if (!hasLocalMagicPotObservation && pending.Result.Observation is { } observation)
            magicPotObservation = observation;
    }

    private void ResetForecastState()
    {
        magicPotObservation = null;
        crowdSourceCeSpawnObservations = new Dictionary<uint, CeSpawnObservation>();
        localCeSpawnObservations.Clear();
        hasLocalMagicPotObservation = false;
        crowdSourceLookupStarted = false;
        queriedZoneServerId = 0;
        nextCrowdSourceRefresh = 0;
        queryGeneration++;
        crowdSourceCancellation?.Cancel();
        crowdSourceCancellation?.Dispose();
        crowdSourceCancellation = null;
        Interlocked.Exchange(ref pendingCrowdSourceResult, null);
        ForecastStatus = "众包：等待当前实例编号";
    }

    private static string AreaName(uint territoryId) => territoryId switch
    {
        PotCandidateCatalog.SouthHornTerritoryId => "新月岛南部",
        PotCandidateCatalog.NorthHornTerritoryId => "新月岛北部",
        _ => "区域外"
    };

    private Vector3 MapToWorld(Vector2 mapPosition)
    {
        var map = dataManager.GetExcelSheet<Map>().GetRowOrDefault(clientState.MapId);
        if (map is not { } row || row.SizeFactor == 0) return default;
        var scale = row.SizeFactor / 100f;
        var texture = (mapPosition - Vector2.One) * scale / 40.96f * 2048f;
        var world = (texture - new Vector2(1024f)) / scale - new Vector2(row.OffsetX, row.OffsetY);
        return new(world.X, 0f, world.Y);
    }

    private static string FormatDuration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private sealed record PendingCrowdSourceResult(int Generation, CrowdSourceLookupResult Result);
}

public enum OccultEventKind
{
    Fate,
    CriticalEngagement,
    ForkTower,
    MagicPot,
    MagicPotForecast
}

public readonly record struct OccultEventSnapshot(
    uint DataId,
    string Name,
    Vector3 Position,
    uint IconId,
    OccultEventKind Kind,
    string StateText,
    string RewardTag = "",
    bool IsJoinable = false);
