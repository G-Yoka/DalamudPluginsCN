using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace CrescentCompass.Core;

public static class MagicPotForecastSchedule
{
    public const int IntervalSeconds = 30 * 60;
    public const int PredictionToleranceSeconds = 35 * 60;
    public const int AdvanceNoticeSeconds = 5 * 60;

    private static readonly MagicPotDefinition[] SouthHornDefinitions =
    [
        new(1976, "幸福的魔法罐（上）", new(26.03f, 17.09f), 60958),
        new(1977, "瑟瑟发抖的魔法罐（下）", new(11.88f, 32.08f), 60958)
    ];

    // CrowdSource 的轮换顺序为 2073 -> 2072。
    private static readonly MagicPotDefinition[] NorthHornDefinitions =
    [
        new(2073, "被吹飞的魔法罐（下）", new(11.3f, 26.2f), 60958),
        new(2072, "被欺负的魔法罐（上）", new(26.1f, 12.1f), 60958)
    ];

    public static IReadOnlyList<MagicPotDefinition> GetDefinitions(uint territoryId) => territoryId switch
    {
        PotCandidateCatalog.SouthHornTerritoryId => SouthHornDefinitions,
        PotCandidateCatalog.NorthHornTerritoryId => NorthHornDefinitions,
        _                                        => []
    };

    public static bool TryCreate(
        uint territoryId,
        MagicPotObservation observation,
        long now,
        out MagicPotForecast forecast)
    {
        forecast = default;
        var definitions = GetDefinitions(territoryId);
        var currentIndex = -1;
        for (var index = 0; index < definitions.Count; index++)
            if (definitions[index].FateId == observation.FateId)
            {
                currentIndex = index;
                break;
            }

        if (currentIndex < 0 || observation.SpawnedAt <= 0 ||
            now > observation.SpawnedAt + PredictionToleranceSeconds)
            return false;

        forecast = new(
            definitions[(currentIndex + 1) % definitions.Count],
            observation.SpawnedAt + IntervalSeconds,
            observation.Source);
        return true;
    }

    public static bool ShouldShow(MagicPotForecast forecast, long now) =>
        now >= forecast.PredictedAt - AdvanceNoticeSeconds &&
        now <= forecast.PredictedAt + (PredictionToleranceSeconds - IntervalSeconds);
}

public readonly record struct MagicPotDefinition(
    uint FateId,
    string Name,
    Vector2 MapPosition,
    uint IconId);

public readonly record struct MagicPotObservation(uint FateId, long SpawnedAt, string Source);

public readonly record struct ForkTowerObservation(uint EventId, string Name, long SpawnedAt, string Source);

public readonly record struct CeSpawnObservation(uint EventId, long SpawnedAt, string Source);

public readonly record struct ForkTowerDefinition(uint EventId, string Name, Vector2 MapPosition, uint IconId);

public static class ForkTowerCatalog
{
    public static bool TryGetDefinition(uint territoryId, out ForkTowerDefinition definition)
    {
        definition = territoryId switch
        {
            PotCandidateCatalog.SouthHornTerritoryId => new(48, "两歧塔 力之塔", new(22.7f, 21.6f), 63978),
            PotCandidateCatalog.NorthHornTerritoryId => new(64, "两歧塔 魔之塔", new(15f, 30f), 63978),
            _                                        => default
        };
        return definition.EventId != 0;
    }
}

public readonly record struct MagicPotForecast(
    MagicPotDefinition Definition,
    long PredictedAt,
    string Source);

public sealed record CrowdSourceLookupResult(
    MagicPotObservation? Observation,
    ForkTowerObservation? ForkTowerObservation,
    string Status,
    IReadOnlyDictionary<uint, CeSpawnObservation>? CeObservations = null);

public static class CrowdSourceMagicPotClient
{
    private const string Endpoint = "wss://ce-crowdsource.atmoomen.top/v1/realtime/";
    private const int MaximumSnapshotBytes = 8 * 1024 * 1024;

    public static async Task<CrowdSourceLookupResult> QueryAsync(
        uint dataCenterId,
        uint zoneServerId,
        uint territoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"{Endpoint}{dataCenterId}"), timeout.Token);

            using var payload = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return new(null, null, "众包：连接在返回快照前关闭");
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                payload.Write(buffer, 0, result.Count);
                if (payload.Length > MaximumSnapshotBytes)
                    return new(null, null, "众包：快照超过大小限制");
                if (result.EndOfMessage) break;
            }

            var json = Encoding.UTF8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length));
            return ParseSnapshot(json, zoneServerId, territoryId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, null, "众包：查询超时，将等待本机首次观察");
        }
        catch (OperationCanceledException)
        {
            return new(null, null, "众包：查询已取消");
        }
        catch (Exception exception)
        {
            return new(null, null, $"众包：查询失败（{exception.GetType().Name}）");
        }
    }

    internal static CrowdSourceLookupResult ParseSnapshot(string json, uint zoneServerId, uint territoryId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "snapshot" ||
                !root.TryGetProperty("instances", out var instances))
                return new(null, null, "众包：返回内容不是实例快照");

            JsonElement? matchedInstance = null;
            foreach (var instance in instances.EnumerateArray())
                if (instance.TryGetProperty("zoneServerID", out var id) && id.GetUInt32() == zoneServerId)
                {
                    matchedInstance = instance;
                    break;
                }

            if (matchedInstance is not { } currentInstance)
                return new(null, null, $"众包：实例 {zoneServerId} 暂无记录");

            var serverTime = root.TryGetProperty("serverTime", out var serverTimeElement)
                                 ? serverTimeElement.GetInt64()
                                 : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var areaCode = territoryId == PotCandidateCatalog.SouthHornTerritoryId ? "SouthHorn" : "NorthHorn";
            var eventLastSeenSets = SelectEventLastSeenSets(currentInstance, areaCode, serverTime);
            if (eventLastSeenSets.Count == 0)
                return new(null, null, $"众包：实例 {zoneServerId} 的{AreaName(territoryId)}暂无轨道数据");

            MagicPotObservation? latest = null;
            foreach (var events in eventLastSeenSets)
            {
                foreach (var definition in MagicPotForecastSchedule.GetDefinitions(territoryId))
                {
                    var key = $"{territoryId}:FATE:{definition.FateId}";
                    if (!events.TryGetProperty(key, out var eventState) ||
                        !eventState.TryGetProperty("lastSpawnedAt", out var spawnedAtElement))
                        continue;

                    var observation = new MagicPotObservation(definition.FateId, spawnedAtElement.GetInt64(), "众包");
                    if (latest == null || observation.SpawnedAt > latest.Value.SpawnedAt)
                        latest = observation;
                }
            }

            ForkTowerObservation? forkTower = null;
            if (ForkTowerCatalog.TryGetDefinition(territoryId, out var towerDefinition))
                foreach (var events in eventLastSeenSets)
                    if (events.TryGetProperty($"{territoryId}:CE:{towerDefinition.EventId}", out var towerState) &&
                        towerState.TryGetProperty("lastSpawnedAt", out var towerSpawnedAt))
                    {
                        var observation = new ForkTowerObservation(
                            towerDefinition.EventId,
                            towerDefinition.Name,
                            towerSpawnedAt.GetInt64(),
                            "众包");
                        if (forkTower == null || observation.SpawnedAt > forkTower.Value.SpawnedAt)
                            forkTower = observation;
                    }

            var ceObservations = new Dictionary<uint, CeSpawnObservation>();
            foreach (var definition in CeSpawnCatalog.All.Where(item => item.TerritoryId == territoryId))
                foreach (var events in eventLastSeenSets)
                    if (events.TryGetProperty($"{territoryId}:CE:{definition.Id}", out var ceState) &&
                        ceState.TryGetProperty("lastSpawnedAt", out var ceSpawnedAt) &&
                        ceSpawnedAt.TryGetInt64(out var spawnedAt) && spawnedAt > 0 &&
                        (!ceObservations.TryGetValue(definition.Id, out var previous) || spawnedAt > previous.SpawnedAt))
                        ceObservations[definition.Id] = new(definition.Id, spawnedAt, "众包");

            var status = (latest != null, forkTower != null) switch
            {
                (true, true)  => $"众包：已读取实例 {zoneServerId} 的魔法罐与两歧塔记录",
                (true, false) => $"众包：已读取实例 {zoneServerId} 的魔法罐记录",
                (false, true) => $"众包：已读取实例 {zoneServerId} 的两歧塔记录；暂无魔法罐记录",
                _             => $"众包：实例 {zoneServerId} 尚无魔法罐或两歧塔记录"
            };
            return new(latest, forkTower, status, ceObservations);
        }
        catch (JsonException)
        {
            return new(null, null, "众包：快照解析失败");
        }
    }

    private static IReadOnlyList<JsonElement> SelectEventLastSeenSets(
        JsonElement instance,
        string areaCode,
        long serverTime)
    {
        if (instance.TryGetProperty("areaTracks", out var areaTracks) &&
            areaTracks.TryGetProperty(areaCode, out var tracks) &&
            tracks.ValueKind == JsonValueKind.Array)
        {
            var candidates = new List<(JsonElement Events, bool Active, long LastReceivedAt, int Ordinal)>();
            foreach (var track in tracks.EnumerateArray())
            {
                if (!track.TryGetProperty("eventLastSeen", out var trackEvents)) continue;
                var expiresAt = track.TryGetProperty("expiresAt", out var expiry) ? expiry.GetInt64() : 0;
                var isActive = expiresAt > serverTime;
                var lastReceivedAt = track.TryGetProperty("lastReceivedAt", out var received) ? received.GetInt64() : 0;
                var ordinal = track.TryGetProperty("ordinal", out var order) ? order.GetInt32() : int.MaxValue;
                candidates.Add((trackEvents, isActive, lastReceivedAt, ordinal));
            }

            if (candidates.Count > 0)
            {
                var hasActive = candidates.Any(candidate => candidate.Active);
                return candidates
                    .Where(candidate => !hasActive || candidate.Active)
                    .OrderByDescending(candidate => candidate.LastReceivedAt)
                    .ThenBy(candidate => candidate.Ordinal)
                    .Select(candidate => candidate.Events)
                    .ToArray();
            }
        }

        return instance.TryGetProperty("eventLastSeen", out var legacyEvents)
                   ? [legacyEvents]
                   : [];
    }

    private static string AreaName(uint territoryId) =>
        territoryId == PotCandidateCatalog.SouthHornTerritoryId ? "新月岛南部" : "新月岛北部";
}
