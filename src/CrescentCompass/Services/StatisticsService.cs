using CrescentCompass.Configuration;
using CrescentCompass.Core;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace CrescentCompass.Services;

public sealed unsafe class StatisticsService : IDisposable
{
    private const uint KnowledgeLogMessageId = 10_952;
    private const uint SupportJobExperienceLogMessageId = 10_953;
    private const uint SouthCurrencyItemId = 47_750;
    private const uint NorthCurrencyItemId = 50_977;
    private static readonly uint[] TrackedItemIds =
    [
        SouthCurrencyItemId,
        47_744, 47_745, 47_746, 47_747, 47_748, 47_749,
        50_974, 50_975, 50_976,
    ];

    private readonly PluginConfiguration configuration;
    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;
    private readonly IFateTable fateTable;
    private readonly OccultEventTracker eventTracker;
    private readonly Action save;
    private readonly Dictionary<uint, int> itemCounts = [];
    private readonly List<StatisticsRecentEntry> recentEntries = [];
    private Participation? fateParticipation;
    private Participation? ceParticipation;
    private long nextUpdate;
    private long nextInventoryScan;
    private long lastElapsedUpdate;
    private long southElapsedMilliseconds;
    private long northElapsedMilliseconds;
    private uint lastSupportedTerritory;
    private bool disposed;

    public StatisticsService(PluginConfiguration configuration, IChatGui chatGui,
        IClientState clientState, IPlayerState playerState, IFramework framework,
        IFateTable fateTable, OccultEventTracker eventTracker, Action save)
    {
        this.configuration = configuration;
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.playerState = playerState;
        this.framework = framework;
        this.fateTable = fateTable;
        this.eventTracker = eventTracker;
        this.save = save;
        lastElapsedUpdate = Environment.TickCount64;
        chatGui.LogMessage += OnLogMessage;
        framework.Update += OnFrameworkUpdate;
    }

    public OccultStatisticsTotals Session { get; private set; } = new();
    public OccultStatisticsTotals Cumulative => configuration.StatisticsCumulative;
    public IReadOnlyList<StatisticsRecentEntry> RecentEntries => recentEntries;
    public int CurrentSouthCurrency => CurrentItemCount(SouthCurrencyItemId);
    public int CurrentNorthCurrency => CurrentItemCount(NorthCurrencyItemId);
    public int CurrentAzureDemiatma => CurrentItemCount(47_744);
    public int CurrentVerdigrisDemiatma => CurrentItemCount(47_745);
    public int CurrentMalachiteDemiatma => CurrentItemCount(47_746);
    public int CurrentRealgarDemiatma => CurrentItemCount(47_747);
    public int CurrentPurpleDemiatma => CurrentItemCount(47_748);
    public int CurrentYellowDemiatma => CurrentItemCount(47_749);
    public int CurrentAlphaDispeller => CurrentItemCount(50_974);
    public int CurrentBetaDispeller => CurrentItemCount(50_975);
    public int CurrentGammaDispeller => CurrentItemCount(50_976);

    public TimeSpan SessionElapsedForTerritory(uint territory) => TimeSpan.FromMilliseconds(
        territory == PotCandidateCatalog.NorthHornTerritoryId
            ? northElapsedMilliseconds
            : southElapsedMilliseconds);

    public int CurrentItemCount(uint itemId) => itemCounts.GetValueOrDefault(itemId);

    public void ResetSession()
    {
        Session = new();
        southElapsedMilliseconds = 0;
        northElapsedMilliseconds = 0;
        lastElapsedUpdate = Environment.TickCount64;
        recentEntries.Clear();
        RefreshInventoryBaseline();
    }

    public void ResetCumulative()
    {
        configuration.StatisticsCumulative = new();
        save();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        chatGui.LogMessage -= OnLogMessage;
        framework.Update -= OnFrameworkUpdate;
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (message.LogMessageId is not KnowledgeLogMessageId and not SupportJobExperienceLogMessageId)
            return;
        var territory = ResolveRewardTerritory();
        if (!IsSupportedTerritory(territory) || !IsLocalPlayerMessage(message) ||
            !TryReadAmount(message, out var amount) || amount == 0)
            return;

        var sessionArea = Area(Session, territory);
        var cumulativeArea = Area(Cumulative, territory);
        if (message.LogMessageId == KnowledgeLogMessageId)
        {
            sessionArea.KnowledgeExperience += amount;
            cumulativeArea.KnowledgeExperience += amount;
            AddRecent(territory, $"知见 +{amount:N0}");
        }
        else
        {
            sessionArea.SupportJobExperience += amount;
            cumulativeArea.SupportJobExperience += amount;
            AddRecent(territory, $"辅助职业经验 +{amount:N0}");
        }
        save();
    }

    private bool IsLocalPlayerMessage(ILogMessage message)
    {
        var source = message.SourceEntity;
        if (source == null || !source.IsPlayer) return true;
        var sourceName = source.Name.ToString();
        return string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(playerState.CharacterName) ||
               string.Equals(sourceName, playerState.CharacterName, StringComparison.Ordinal);
    }

    private static bool TryReadAmount(ILogMessage message, out uint amount)
    {
        amount = 0;
        if (message.Parameters.Count > 1 && !message.Parameters[1].IsString)
        {
            amount = message.Parameters[1].UIntValue;
            return amount > 0;
        }
        for (var index = message.Parameters.Count - 1; index >= 0; index--)
        {
            var parameter = message.Parameters[index];
            if (parameter.IsString || parameter.UIntValue == 0) continue;
            amount = parameter.UIntValue;
            return true;
        }
        return false;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - lastElapsedUpdate, 0, 5_000);
        lastElapsedUpdate = now;
        if (clientState.TerritoryType == PotCandidateCatalog.SouthHornTerritoryId)
            southElapsedMilliseconds += elapsed;
        else if (clientState.TerritoryType == PotCandidateCatalog.NorthHornTerritoryId)
            northElapsedMilliseconds += elapsed;
        if (now < nextUpdate) return;
        nextUpdate = now + 250;

        if (IsSupportedTerritory(clientState.TerritoryType))
            lastSupportedTerritory = clientState.TerritoryType;
        TrackParticipation(now);
        if (now < nextInventoryScan) return;
        nextInventoryScan = now + 500;
        ScanInventory();
    }

    private void TrackParticipation(long now)
    {
        var territory = clientState.TerritoryType;
        var currentFateId = CurrentFateId();
        UpdateParticipation(ref fateParticipation, currentFateId, territory, now,
            OccultEventKind.Fate, OccultEventKind.MagicPot);
        var currentCeId = CurrentCriticalEngagementId();
        UpdateParticipation(ref ceParticipation, currentCeId, territory, now,
            OccultEventKind.CriticalEngagement);
    }

    private void UpdateParticipation(ref Participation? participation, uint currentId,
        uint territory, long now, params OccultEventKind[] allowedKinds)
    {
        if (participation is { } active &&
            (currentId != active.EventId || territory != active.TerritoryId))
        {
            if (active.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot &&
                FateCompleted(active.EventId))
                active.MaxProgress = 100;
            CompleteParticipation(active, territory, now);
            participation = null;
        }

        if (currentId == 0 || !IsSupportedTerritory(territory)) return;
        var snapshot = eventTracker.ActiveEvents.FirstOrDefault(item =>
            item.DataId == currentId && allowedKinds.Contains(item.Kind));
        if (snapshot.DataId == 0) return;
        if (participation == null)
            participation = new Participation(territory, currentId, snapshot.Kind, snapshot.Name, now, snapshot.Progress);
        else if (snapshot.Progress > participation.MaxProgress)
            participation.MaxProgress = snapshot.Progress;
    }

    private void CompleteParticipation(Participation participation, uint currentTerritory, long now)
    {
        if (currentTerritory != participation.TerritoryId) return;
        var elapsed = now - participation.StartedAt;
        var completed = participation.Kind == OccultEventKind.CriticalEngagement
            ? elapsed >= 10_000
            : participation.MaxProgress >= 100;
        if (!completed) return;

        var sessionArea = Area(Session, participation.TerritoryId);
        var cumulativeArea = Area(Cumulative, participation.TerritoryId);
        if (participation.Kind == OccultEventKind.CriticalEngagement)
        {
            sessionArea.CriticalEngagementCount++;
            cumulativeArea.CriticalEngagementCount++;
            AddRecent(participation.TerritoryId, $"CE 参与 · {participation.Name}");
        }
        else
        {
            sessionArea.FateCount++;
            cumulativeArea.FateCount++;
            if (participation.Kind == OccultEventKind.MagicPot)
            {
                sessionArea.MagicPotCount++;
                cumulativeArea.MagicPotCount++;
            }
            AddRecent(participation.TerritoryId,
                $"FATE 完成 · {participation.Name}" +
                (participation.Kind == OccultEventKind.MagicPot ? "（魔法罐）" : string.Empty));
        }
        save();
    }

    private void ScanInventory()
    {
        if (!TryReadTrackedItems(out var counts)) return;
        if (itemCounts.Count == 0)
        {
            foreach (var (itemId, count) in counts) itemCounts[itemId] = count;
            return;
        }

        var changed = false;
        var countGains = IsSupportedTerritory(clientState.TerritoryType);
        foreach (var (itemId, current) in counts)
        {
            var previous = itemCounts.GetValueOrDefault(itemId, current);
            itemCounts[itemId] = current;
            var gained = current - previous;
            if (gained <= 0 || !countGains) continue;
            RecordItemGain(itemId, gained);
            changed = true;
        }
        if (changed) save();
    }

    private void RefreshInventoryBaseline()
    {
        itemCounts.Clear();
        if (!TryReadTrackedItems(out var counts)) return;
        foreach (var (itemId, count) in counts) itemCounts[itemId] = count;
    }

    private static bool TryReadTrackedItems(out Dictionary<uint, int> counts)
    {
        counts = TrackedItemIds.ToDictionary(itemId => itemId, _ => 0);
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;
        foreach (var inventoryType in new[]
                 {
                     InventoryType.Inventory1, InventoryType.Inventory2,
                     InventoryType.Inventory3, InventoryType.Inventory4
                 })
        {
            var container = inventory->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded) return false;
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null || slot->Quantity <= 0 || !counts.ContainsKey(slot->ItemId)) continue;
                counts[slot->ItemId] += slot->Quantity;
            }
        }
        return true;
    }

    private void RecordItemGain(uint itemId, int gained)
    {
        var territory = itemId >= 50_974 ? PotCandidateCatalog.NorthHornTerritoryId :
            PotCandidateCatalog.SouthHornTerritoryId;
        var sessionArea = Area(Session, territory);
        var cumulativeArea = Area(Cumulative, territory);
        string label;
        switch (itemId)
        {
            case SouthCurrencyItemId:
            case NorthCurrencyItemId:
                sessionArea.SpecialCurrency += gained;
                cumulativeArea.SpecialCurrency += gained;
                label = itemId == SouthCurrencyItemId ? "新月矿石" : "朔月矿石";
                break;
            case 47_744: sessionArea.AzureDemiatma += gained; cumulativeArea.AzureDemiatma += gained; label = "青色半魂晶"; break;
            case 47_745: sessionArea.VerdigrisDemiatma += gained; cumulativeArea.VerdigrisDemiatma += gained; label = "碧色半魂晶"; break;
            case 47_746: sessionArea.MalachiteDemiatma += gained; cumulativeArea.MalachiteDemiatma += gained; label = "绿色半魂晶"; break;
            case 47_747: sessionArea.RealgarDemiatma += gained; cumulativeArea.RealgarDemiatma += gained; label = "橙色半魂晶"; break;
            case 47_748: sessionArea.PurpleDemiatma += gained; cumulativeArea.PurpleDemiatma += gained; label = "紫色半魂晶"; break;
            case 47_749: sessionArea.YellowDemiatma += gained; cumulativeArea.YellowDemiatma += gained; label = "黄色半魂晶"; break;
            case 50_974: sessionArea.AlphaDispeller += gained; cumulativeArea.AlphaDispeller += gained; label = "[α] 消幻晶"; break;
            case 50_975: sessionArea.BetaDispeller += gained; cumulativeArea.BetaDispeller += gained; label = "[β] 消幻晶"; break;
            case 50_976: sessionArea.GammaDispeller += gained; cumulativeArea.GammaDispeller += gained; label = "[γ] 消幻晶"; break;
            default: return;
        }
        if (!string.IsNullOrEmpty(label)) AddRecent(territory, $"{label} +{gained:N0}");
    }

    private void AddRecent(uint territory, string text)
    {
        recentEntries.Insert(0, new(DateTimeOffset.Now, territory, text));
        if (recentEntries.Count > 20) recentEntries.RemoveRange(20, recentEntries.Count - 20);
    }

    private uint ResolveRewardTerritory() => IsSupportedTerritory(clientState.TerritoryType)
        ? clientState.TerritoryType
        : lastSupportedTerritory;

    private static OccultStatisticsArea Area(OccultStatisticsTotals totals, uint territory) =>
        territory == PotCandidateCatalog.NorthHornTerritoryId ? totals.North : totals.South;

    private static bool IsSupportedTerritory(uint territory) =>
        territory is PotCandidateCatalog.SouthHornTerritoryId or PotCandidateCatalog.NorthHornTerritoryId;

    private bool FateCompleted(uint fateId)
    {
        var fate = fateTable.FirstOrDefault(item => item.FateId == fateId);
        return fate != null &&
               (fate.State is Dalamud.Game.ClientState.Fates.FateState.Ending or
                   Dalamud.Game.ClientState.Fates.FateState.Ended) && fate.Progress >= 100;
    }

    private static uint CurrentFateId()
    {
        var manager = FateManager.Instance();
        return manager == null || manager->CurrentFate == null ? 0u : manager->CurrentFate->FateId;
    }

    private static uint CurrentCriticalEngagementId()
    {
        var container = DynamicEventContainer.GetInstance();
        return container == null ? 0u : container->CurrentEventId;
    }

    private sealed class Participation(uint territoryId, uint eventId, OccultEventKind kind,
        string name, long startedAt, int maxProgress)
    {
        public uint TerritoryId { get; } = territoryId;
        public uint EventId { get; } = eventId;
        public OccultEventKind Kind { get; } = kind;
        public string Name { get; } = name;
        public long StartedAt { get; } = startedAt;
        public int MaxProgress { get; set; } = maxProgress;
    }
}

public readonly record struct StatisticsRecentEntry(DateTimeOffset At, uint TerritoryId, string Text);
