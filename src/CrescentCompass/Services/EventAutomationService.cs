using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Integrations;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;
using EventItemRow = Lumina.Excel.Sheets.EventItem;
using StatusRow = Lumina.Excel.Sheets.Status;

namespace CrescentCompass.Services;

public enum EventAutomationStage
{
    Disabled,
    Waiting,
    Traveling,
    AwaitingStart,
    Participating,
    Settling,
    Returning,
    TravelingToMagicPot,
    WaitingForMagicPot,
    AwaitingMagicPotReward,
    TreasureHunting,
    Suspended
}

public sealed unsafe class EventAutomationService : IDisposable
{
    private const long MissingEventGraceMilliseconds = 3_000;
    private const long CombatApproachRetryMilliseconds = 1_000;
    private const long MagicPotRewardWaitMilliseconds = 15_000;
    private const long MagicPotForecastWaitMilliseconds = 11 * 60_000;
    private const long TreasureActionRetryMilliseconds = 8_000;
    private const long TreasureProbeConfirmationMilliseconds = 5_000;
    private const long TreasureDiscoveryGraceMilliseconds = 10_000;
    private const long TreasureInteractionConfirmationMilliseconds = 8_000;
    private const float TreasureArrivalRadius = 3f;
    private const float CriticalEngagementWaitingRadius = 12f;
    private const float CriticalEngagementCombatRadius = 30f;
    private const uint MagicElixirEventItemId = 2_003_296;
    private const float MeleeEngagementRange = 3f;
    private const float RangedEngagementRange = 20f;
    private readonly PluginConfiguration configuration;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly ITargetManager targetManager;
    private readonly IFramework framework;
    private readonly OccultEventTracker eventTracker;
    private readonly TreasureTracker treasureTracker;
    private readonly NavigationService navigationService;
    private readonly CombatAutomationIntegrations combatIntegrations;
    private readonly IDataManager dataManager;
    private readonly Action save;
    private readonly IPluginLog log;
    private OccultEventSnapshot? target;
    private long targetLastSeenAt;
    private long awaitingStartedAt;
    private long settleStartedAt;
    private long nextUpdate;
    private long nextCombatApproach;
    private long magicPotDeadline;
    private long nextTreasureAction;
    private long treasureGuidanceLostAt;
    private long nextIdentifierResolve;
    private uint guidanceStatusId;
    private uint elixirItemId = MagicElixirEventItemId;
    private uint treasureCandidateId;
    private TreasureSnapshot? activeTreasure;
    private readonly HashSet<uint> unreachableTreasureCandidateIds = [];
    private readonly HashSet<uint> attemptedTreasureCandidateIds = [];
    private int treasureCandidateRound;
    private int treasureHintCount;
    private uint pendingTreasureProbeCandidateId;
    private long pendingTreasureProbeDeadline;
    private bool treasureCandidatesExhausted;
    private string treasureActionFailure = string.Empty;
    private bool treasureInteractionIssued;
    private long treasureInteractionStartedAt;
    private readonly HashSet<ulong> treasureObjectsAtHuntStart = [];
    private bool awaitingSpawnedMagicPotTreasure;
    private bool navigationIssued;
    private bool routeRecordingWasActive;
    private bool disposed;

    public EventAutomationService(
        PluginConfiguration configuration,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        ITargetManager targetManager,
        IFramework framework,
        OccultEventTracker eventTracker,
        TreasureTracker treasureTracker,
        NavigationService navigationService,
        CombatAutomationIntegrations combatIntegrations,
        IDataManager dataManager,
        Action save,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.targetManager = targetManager;
        this.framework = framework;
        this.eventTracker = eventTracker;
        this.treasureTracker = treasureTracker;
        this.navigationService = navigationService;
        this.combatIntegrations = combatIntegrations;
        this.dataManager = dataManager;
        this.save = save;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public EventAutomationStage Stage { get; private set; } = EventAutomationStage.Disabled;
    public string Status { get; private set; } = "自动事件尚未启用";
    public string TargetLabel => target is { } current ? $"{KindLabel(current.Kind)}：{current.Name} #{current.DataId}" : "—";
    public CombatAutomationIntegrations CombatIntegrations => combatIntegrations;
    public float CurrentEngagementRange => EngagementRangeForJob(
        objectTable.LocalPlayer?.ClassJob.RowId ?? 0);
    public bool UsesDefaultWaitingPoint => configuration.EventAutomationWaitingPoints.All(item =>
        item.TerritoryId != clientState.TerritoryType);

    public string WaitingPointLabel
    {
        get
        {
            var point = CurrentWaitingPoint();
            return point is null ? "当前区域尚未记录" : $"X:{point.X:F1} Y:{point.Y:F1} Z:{point.Z:F1}";
        }
    }

    public void RecordCurrentWaitingPoint()
    {
        if (!SupportedTerritory(clientState.TerritoryType) || objectTable.LocalPlayer is not { } player)
        {
            Status = "请先进入新月岛南部或北部再记录等待点";
            return;
        }

        configuration.EventAutomationWaitingPoints.RemoveAll(item =>
            item.TerritoryId == clientState.TerritoryType);
        configuration.EventAutomationWaitingPoints.Add(new EventAutomationWaitingPoint
        {
            TerritoryId = clientState.TerritoryType,
            X = player.Position.X,
            Y = player.Position.Y,
            Z = player.Position.Z
        });
        save();
        Status = $"已记录{AreaName(clientState.TerritoryType)}等待点";
    }

    public void ClearCurrentWaitingPoint()
    {
        if (!SupportedTerritory(clientState.TerritoryType)) return;
        configuration.EventAutomationWaitingPoints.RemoveAll(item =>
            item.TerritoryId == clientState.TerritoryType);
        save();
        Status = $"已恢复{AreaName(clientState.TerritoryType)}总部大水晶等待点";
    }

    public void Stop()
    {
        configuration.EnableEventAutomation = false;
        save();
        Reset(EventAutomationStage.Disabled, "自动事件已停止", true);
    }

    public void Resume()
    {
        if (!configuration.EnableEventAutomation) return;
        Reset(EventAutomationStage.Waiting, "自动事件已恢复，正在等待目标", true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        Reset(EventAutomationStage.Disabled, "自动事件已卸载", true);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now < nextUpdate) return;
        nextUpdate = now + 250;

        if (!configuration.EnableEventAutomation)
        {
            if (Stage != EventAutomationStage.Disabled)
                Reset(EventAutomationStage.Disabled, "自动事件尚未启用", true);
            return;
        }

        if (!SupportedTerritory(clientState.TerritoryType))
        {
            if (Stage is not EventAutomationStage.Waiting and not EventAutomationStage.Disabled)
                Reset(EventAutomationStage.Waiting, "当前不在新月岛，自动事件正在等待", true);
            else
            {
                Stage = EventAutomationStage.Waiting;
                Status = "当前不在新月岛，自动事件正在等待";
            }
            return;
        }

        if (objectTable.LocalPlayer is not { } player)
        {
            Status = "正在等待玩家状态";
            return;
        }

        if (condition[ConditionFlag.Unconscious])
        {
            Suspend("角色无法行动，自动事件已暂停");
            return;
        }

        if (navigationService.IsRecordingCustomRoute)
        {
            routeRecordingWasActive = true;
            navigationIssued = false;
            combatIntegrations.Disarm();
            Status = navigationService.RouteRecordingStatus;
            return;
        }
        if (routeRecordingWasActive)
        {
            routeRecordingWasActive = false;
            Reset(EventAutomationStage.Waiting, "路线录制已结束，正在重新选择自动事件", true);
            return;
        }

        if (Stage == EventAutomationStage.Disabled)
        {
            Stage = EventAutomationStage.Waiting;
            Status = "自动事件已启用，正在等待目标";
        }
        if (Stage == EventAutomationStage.Suspended) return;

        ResolveTreasureIdentifiers();
        var hasGuidance = HasTreasureGuidance(player);
        if (hasGuidance && Stage != EventAutomationStage.TreasureHunting)
        {
            BeginTreasureHunt();
            UpdateTreasureHunt(player.Position, now, true);
            return;
        }
        if (Stage == EventAutomationStage.TreasureHunting)
        {
            UpdateTreasureHunt(player.Position, now, hasGuidance);
            return;
        }

        if (configuration.EventAutomationPriority == EventAutomationPriority.MagicPotFirst &&
            TryGetMagicPotForecast(out var forecast))
        {
            if (!IsCommittedToCurrentEvent() &&
                Stage is not EventAutomationStage.TravelingToMagicPot and
                     not EventAutomationStage.WaitingForMagicPot and
                     not EventAutomationStage.AwaitingMagicPotReward)
            {
                BeginMagicPotForecast(forecast, now);
                return;
            }
        }

        switch (Stage)
        {
            case EventAutomationStage.Waiting:
                UpdateWaiting(player.Position, now);
                break;
            case EventAutomationStage.Traveling:
                UpdateTraveling(player.Position, now);
                break;
            case EventAutomationStage.AwaitingStart:
                UpdateAwaitingStart(player.Position, now);
                break;
            case EventAutomationStage.Participating:
                UpdateParticipating(player.Position, now);
                break;
            case EventAutomationStage.Settling:
                UpdateSettling(player.Position, now);
                break;
            case EventAutomationStage.Returning:
                UpdateReturning(player.Position, now);
                break;
            case EventAutomationStage.TravelingToMagicPot:
                UpdateTravelingToMagicPot(player.Position, now);
                break;
            case EventAutomationStage.WaitingForMagicPot:
                UpdateWaitingForMagicPot(player.Position, now);
                break;
            case EventAutomationStage.AwaitingMagicPotReward:
                UpdateAwaitingMagicPotReward(now);
                break;
        }
    }

    private void UpdateWaiting(Vector3 playerPosition, long now)
    {
        if (CurrentWaitingPoint() is null)
        {
            Status = $"{AreaName(clientState.TerritoryType)}尚未记录等待点";
            return;
        }
        if (navigationService.IsNavigating)
        {
            Status = "正在等待现有导航结束";
            return;
        }
        if (!combatIntegrations.CanArm(
                configuration.EventMechanicProvider,
                configuration.CombatRotationProvider,
                configuration.BossModAutomationPreset,
                out var dependencyReason))
        {
            Status = $"战斗接管尚未就绪：{dependencyReason}";
            return;
        }

        var selected = SelectTarget(playerPosition);
        if (selected is not { } next)
        {
            Status = "没有符合自动参与规则的活动事件";
            return;
        }

        target = next;
        targetLastSeenAt = now;
        navigationIssued = navigationService.NavigateToEvent(next.Position, $"自动事件：{next.Name}",
            next.DataId, RouteKind(next.Kind), true);
        if (!navigationIssued)
        {
            Suspend($"无法开始前往 {next.Name}：{navigationService.Status}");
            return;
        }

        Stage = EventAutomationStage.Traveling;
        Status = $"正在前往 {next.Name}";
    }

    private void BeginMagicPotForecast(OccultEventSnapshot forecast, long now)
    {
        navigationService.Cancel(null);
        combatIntegrations.Disarm();
        target = forecast;
        targetLastSeenAt = now;
        magicPotDeadline = now + MagicPotForecastWaitMilliseconds;
        navigationIssued = navigationService.NavigateToEvent(forecast.Position, $"魔法罐预告：{forecast.Name}",
            forecast.DataId, CustomNavigationRouteKind.Fate, true);
        if (!navigationIssued)
        {
            Suspend($"无法前往魔法罐预告点：{navigationService.Status}");
            return;
        }
        Stage = EventAutomationStage.TravelingToMagicPot;
        Status = $"正在前往 {forecast.Name} 的预告位置";
    }

    private void UpdateTravelingToMagicPot(Vector3 playerPosition, long now)
    {
        if (TryGetActiveMagicPot(out var active))
        {
            BeginMagicPotEvent(active, now);
            return;
        }
        if (now >= magicPotDeadline)
        {
            BeginSettling("魔法罐在等待期限内没有出现");
            return;
        }
        if (target is not { } forecast)
        {
            BeginSettling("魔法罐预告状态不可用");
            return;
        }

        var distance = HorizontalDistance(playerPosition, forecast.Position);
        var usingRecordedRoute = navigationService.CurrentCustomRouteEventId == forecast.DataId;
        var completedRecordedRoute = !usingRecordedRoute ||
                                     navigationService.IsFollowingCustomRoute &&
                                     HorizontalDistance(playerPosition, navigationService.ResolvedDestination) <= 8f;
        if (distance <= configuration.EventAutomationArrivalRadius && completedRecordedRoute ||
            navigationIssued && !navigationService.IsNavigating && distance <= configuration.EventAutomationArrivalRadius * 1.5f)
        {
            navigationService.Cancel(null);
            navigationIssued = false;
            Stage = EventAutomationStage.WaitingForMagicPot;
            Status = $"已到达预告位置，等待 {forecast.Name} 出现";
            return;
        }
        if (navigationIssued && !navigationService.IsNavigating)
        {
            Suspend($"前往魔法罐预告点的导航提前结束：{navigationService.Status}");
            return;
        }
        Status = $"正在前往 {forecast.Name} 的预告位置 · 距离 {distance:F0}m";
    }

    private void UpdateWaitingForMagicPot(Vector3 playerPosition, long now)
    {
        if (TryGetActiveMagicPot(out var active))
        {
            BeginMagicPotEvent(active, now);
            return;
        }
        if (now >= magicPotDeadline)
        {
            BeginSettling("魔法罐在等待期限内没有出现");
            return;
        }
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting])
        {
            navigationService.RequestDismount();
            Status = "已到达预告位置，正在下坐骑等待魔法罐出现";
            return;
        }
        var remaining = Math.Max(0, (magicPotDeadline - now) / 1000);
        Status = $"正在预告位置等待魔法罐出现 · 最长等待 {remaining / 60:D2}:{remaining % 60:D2}";
    }

    private void BeginMagicPotEvent(OccultEventSnapshot active, long now)
    {
        target = active;
        targetLastSeenAt = now;
        if (navigationService.CurrentCustomRouteEventId == active.DataId)
        {
            navigationIssued = true;
            Stage = EventAutomationStage.Traveling;
            Status = $"魔法罐已出现，继续沿录制路线前往 {active.Name}";
            return;
        }
        navigationService.Cancel(null);
        navigationIssued = navigationService.NavigateToEvent(active.Position, $"自动事件：{active.Name}",
            active.DataId, RouteKind(active.Kind), true);
        if (!navigationIssued)
        {
            Suspend($"无法开始前往 {active.Name}：{navigationService.Status}");
            return;
        }
        Stage = EventAutomationStage.Traveling;
        Status = $"魔法罐已出现，正在前往 {active.Name}";
    }

    private void BeginAwaitingMagicPotReward()
    {
        navigationService.Cancel(null);
        navigationIssued = false;
        combatIntegrations.Disarm();
        magicPotDeadline = Environment.TickCount64 + MagicPotRewardWaitMilliseconds;
        Stage = EventAutomationStage.AwaitingMagicPotReward;
        Status = "魔法罐事件已结束，正在确认是否获得指引财宝";
    }

    private void UpdateAwaitingMagicPotReward(long now)
    {
        if (now < magicPotDeadline)
        {
            Status = "正在等待指引财宝状态生效";
            return;
        }
        BeginSettling("未获得指引财宝，恢复普通事件循环");
    }

    private void UpdateTraveling(Vector3 playerPosition, long now)
    {
        var active = RefreshTarget(now);
        if (active is null && now - targetLastSeenAt > MissingEventGraceMilliseconds)
        {
            BeginSettling("目标在抵达前结束");
            return;
        }

        var current = active ?? target;
        if (current is null)
        {
            BeginSettling("目标状态不可用");
            return;
        }

        var reachedEvent = current.Value.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot
            ? IsCurrentFate(current.Value.DataId) &&
              (!navigationService.IsFollowingCustomRoute ||
               HorizontalDistance(playerPosition, navigationService.ResolvedDestination) <= 8f)
            : HorizontalDistance(playerPosition, current.Value.Position) <= CriticalEngagementWaitingRadius;
        if (reachedEvent ||
            condition[ConditionFlag.InCombat])
        {
            navigationService.Cancel(null);
            navigationIssued = false;
            awaitingStartedAt = now;
            Stage = EventAutomationStage.AwaitingStart;
            Status = current.Value.Phase == OccultEventPhase.Battle
                ? $"已抵达 {current.Value.Name}，正在接管战斗"
                : $"已抵达 {current.Value.Name}，正在等待开始";
            UpdateAwaitingStart(playerPosition, now);
            return;
        }

        if (navigationIssued && !navigationService.IsNavigating)
            Suspend($"前往 {current.Value.Name} 的导航提前结束：{navigationService.Status}");
        else
            Status = $"正在前往 {current.Value.Name} · {current.Value.StateText}";
    }

    private void UpdateAwaitingStart(Vector3 playerPosition, long now)
    {
        var active = RefreshTarget(now);
        if (active is null)
        {
            if (now - targetLastSeenAt > MissingEventGraceMilliseconds)
                BeginSettling("等待期间事件结束");
            return;
        }

        if (active.Value.Kind == OccultEventKind.CriticalEngagement &&
            HorizontalDistance(playerPosition, active.Value.Position) > CriticalEngagementWaitingRadius)
        {
            if (EnsureNavigation(active.Value.Position, $"CE 等待区域：{active.Value.Name}"))
                Status = $"正在进入 {active.Value.Name} 的等待区域";
            else
                Suspend($"无法进入 {active.Value.Name} 的等待区域：{navigationService.Status}");
            return;
        }

        if (active.Value.Kind == OccultEventKind.CriticalEngagement && navigationService.IsNavigating)
        {
            navigationService.Cancel(null);
            navigationIssued = false;
        }

        if (active.Value.Phase != OccultEventPhase.Battle &&
            active.Value.Kind == OccultEventKind.CriticalEngagement)
        {
            Status = $"等待 {active.Value.Name} 自动编入并开始";
            return;
        }

        if (active.Value.Kind == OccultEventKind.CriticalEngagement &&
            !IsCurrentCriticalEngagement(active.Value.DataId))
        {
            if (now - awaitingStartedAt > 5_000)
            {
                BeginSettling($"未被编入 {active.Value.Name}，本轮已跳过");
                return;
            }
            Status = $"正在确认是否编入 {active.Value.Name}";
            return;
        }

        if (active.Value.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot &&
            !IsCurrentFate(active.Value.DataId))
        {
            if (now - awaitingStartedAt > 5_000)
            {
                Suspend($"已抵达 {active.Value.Name}，但游戏未识别进入该 FATE");
                return;
            }
            Status = $"正在确认进入 {active.Value.Name}";
            return;
        }

        if (active.Value.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot &&
            !PrepareFateEngagement(active.Value, playerPosition, now))
            return;
        if (active.Value.Kind == OccultEventKind.CriticalEngagement &&
            !PrepareCriticalEngagement(active.Value, playerPosition, now))
            return;

        if (!combatIntegrations.Arm(
                configuration.EventMechanicProvider,
                configuration.CombatRotationProvider,
                configuration.BossModAutomationPreset,
                out var error))
        {
            Suspend(error);
            return;
        }

        Stage = EventAutomationStage.Participating;
        Status = $"正在参与 {active.Value.Name} · {combatIntegrations.Status}";
    }

    private void UpdateParticipating(Vector3 playerPosition, long now)
    {
        var expected = target;
        var active = RefreshTarget(now);
        if (expected is { } current && IsEventExplicitlyComplete(current, active))
        {
            if (current.Kind == OccultEventKind.MagicPot)
                BeginAwaitingMagicPotReward();
            else
                BeginSettling($"{current.Name} 已结束，正在原地等待奖励结算");
            return;
        }
        if (active is not null)
        {
            if (active.Value.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot)
            {
                var engagement = MaintainOrHandOffFateEngagement(active.Value, playerPosition, now);
                Status = $"正在参与 {active.Value.Name} · {engagement}";
            }
            else if (active.Value.Kind == OccultEventKind.CriticalEngagement)
            {
                var engagement = CombatAutomationIntegrations.ManagesTargetSelection(
                    configuration.EventMechanicProvider,
                    configuration.CombatRotationProvider)
                    ? $"目标选择已交由 {combatIntegrations.Status} · {active.Value.StateText}"
                    : MaintainCriticalEngagement(active.Value, playerPosition, now);
                Status = $"正在参与 {active.Value.Name} · {engagement}";
            }
            else
                Status = $"正在参与 {active.Value.Name} · {active.Value.StateText}";
            return;
        }
        navigationService.Cancel(null);
        navigationIssued = false;
        if (now - targetLastSeenAt <= MissingEventGraceMilliseconds)
        {
            Status = "事件已从追踪中消失，正在原地确认结束";
            return;
        }
        if (condition[ConditionFlag.InCombat])
        {
            Status = "事件已结束，等待脱离战斗";
            return;
        }
        if (target?.Kind == OccultEventKind.MagicPot)
        {
            BeginAwaitingMagicPotReward();
            return;
        }
        BeginSettling("事件已结束，正在等待结算");
    }

    private string MaintainOrHandOffFateEngagement(
        OccultEventSnapshot active,
        Vector3 playerPosition,
        long now)
    {
        if (!combatIntegrations.IsArmed ||
            !CombatAutomationIntegrations.ManagesMovement(
                configuration.EventMechanicProvider,
                configuration.CombatRotationProvider))
            return MaintainFateEngagement(active, playerPosition, now);

        if (navigationService.IsNavigating) navigationService.Cancel(null);
        navigationIssued = false;
        return $"移动与目标追击已交由 {combatIntegrations.Status} · {active.StateText}";
    }

    private void BeginSettling(string status)
    {
        navigationService.Cancel(null);
        navigationIssued = false;
        combatIntegrations.Disarm();
        settleStartedAt = Environment.TickCount64;
        nextTreasureAction = 0;
        Stage = EventAutomationStage.Settling;
        Status = status;
    }

    private void UpdateSettling(Vector3 playerPosition, long now)
    {
        if (condition[ConditionFlag.InCombat])
        {
            Status = "等待脱离战斗后返回";
            return;
        }
        if (awaitingSpawnedMagicPotTreasure && TryCaptureSpawnedMagicPotTreasure(playerPosition, out var spawned))
        {
            activeTreasure = spawned;
            Stage = EventAutomationStage.TreasureHunting;
            UpdateConfirmedTreasure(playerPosition, spawned, now);
            return;
        }
        if (now - settleStartedAt < configuration.EventAutomationSettleSeconds * 1000f)
        {
            Status = "正在等待事件奖励结算";
            return;
        }

        var point = CurrentWaitingPoint();
        target = null;
        if (point is null)
        {
            Stage = EventAutomationStage.Waiting;
            Status = "没有当前区域等待点，继续原地等待";
            return;
        }

        var destination = new Vector3(point.X, point.Y, point.Z);
        if (HorizontalDistance(playerPosition, destination) <= 8f)
        {
            Stage = EventAutomationStage.Waiting;
            Status = "已在等待点，正在等待新事件";
            return;
        }

        var defaultCamp = DefaultWaitingAetheryte();
        navigationIssued = UsesDefaultWaitingPoint && defaultCamp is { } camp
            ? navigationService.ReturnToCamp(camp, "自动事件等待点")
            : navigationService.NavigateTo(destination, "自动事件等待点");
        if (!navigationIssued)
        {
            Suspend($"无法返回等待点：{navigationService.Status}");
            return;
        }
        Stage = EventAutomationStage.Returning;
        Status = "正在返回等待点";
    }

    private void UpdateReturning(Vector3 playerPosition, long now)
    {
        var point = CurrentWaitingPoint();
        if (point is null)
        {
            navigationService.Cancel(null);
            navigationIssued = false;
            Stage = EventAutomationStage.Waiting;
            Status = "等待点已清除，继续原地等待";
            return;
        }
        var destination = new Vector3(point.X, point.Y, point.Z);
        if (navigationIssued && !navigationService.IsNavigating &&
            HorizontalDistance(playerPosition, destination) > 8f)
        {
            Suspend($"返回等待点的导航提前结束：{navigationService.Status}");
            return;
        }

        var higherPriority = SelectTarget(playerPosition);
        if (higherPriority is { } next)
        {
            navigationService.Cancel(null);
            target = next;
            targetLastSeenAt = now;
            navigationIssued = navigationService.NavigateToEvent(next.Position, $"自动事件：{next.Name}",
                next.DataId, RouteKind(next.Kind), true);
            if (!navigationIssued)
            {
                Suspend($"改道前往 {next.Name} 失败：{navigationService.Status}");
                return;
            }
            Stage = EventAutomationStage.Traveling;
            Status = $"返回途中发现新事件，正在改道前往 {next.Name}";
            return;
        }

        if (HorizontalDistance(playerPosition, destination) <= 8f)
        {
            navigationService.Cancel(null);
            navigationIssued = false;
            Stage = EventAutomationStage.Waiting;
            Status = "已返回等待点，正在等待新事件";
            return;
        }
    }

    private OccultEventSnapshot? RefreshTarget(long now)
    {
        if (target is not { } expected) return null;
        var active = eventTracker.ActiveEvents.FirstOrDefault(item =>
            item.DataId == expected.DataId && SameAutomationKind(item.Kind, expected.Kind));
        if (active.DataId == 0) return null;
        target = active;
        targetLastSeenAt = now;
        return active;
    }

    private OccultEventSnapshot? SelectTarget(Vector3 playerPosition)
    {
        var candidates = eventTracker.ActiveEvents.Where(IsSelectable).ToArray();
        if (candidates.Length == 0) return null;
        return candidates
            .OrderBy(item => PriorityGroup(item.Kind))
            .ThenBy(item => HorizontalDistance(playerPosition, item.Position))
            .ThenBy(item => item.Progress)
            .First();
    }

    private bool IsSelectable(OccultEventSnapshot item)
    {
        if (item.Kind == OccultEventKind.CriticalEngagement)
            return configuration.AutomatedCeIds.Contains(item.DataId) &&
                   item.Phase is OccultEventPhase.Register or OccultEventPhase.Warmup;
        if (item.Kind is not OccultEventKind.Fate and not OccultEventKind.MagicPot) return false;
        if (item.Kind == OccultEventKind.MagicPot &&
            configuration.EventAutomationPriority == EventAutomationPriority.MagicPotFirst)
            return item.IsJoinable && item.Phase == OccultEventPhase.Battle && item.RemainingSeconds > 10;
        return configuration.AutomatedFateIds.Contains(item.DataId) &&
               item.IsJoinable && item.Phase == OccultEventPhase.Battle &&
               item.Progress <= configuration.EventAutomationMaximumFateProgress &&
               item.RemainingSeconds >= configuration.EventAutomationMinimumRemainingSeconds;
    }

    private int PriorityGroup(OccultEventKind kind)
    {
        if (configuration.EventAutomationPriority == EventAutomationPriority.Nearest) return 0;
        if (configuration.EventAutomationPriority == EventAutomationPriority.MagicPotFirst)
            return kind == OccultEventKind.MagicPot ? 0 : kind == OccultEventKind.CriticalEngagement ? 1 : 2;
        var isCe = kind == OccultEventKind.CriticalEngagement;
        return configuration.EventAutomationPriority == EventAutomationPriority.CeFirst
            ? isCe ? 0 : 1
            : isCe ? 1 : 0;
    }

    private bool TryGetMagicPotForecast(out OccultEventSnapshot forecast)
    {
        forecast = eventTracker.ActiveEvents
            .Where(item => item.Kind == OccultEventKind.MagicPotForecast)
            .OrderBy(item => item.DataId)
            .FirstOrDefault();
        return forecast.DataId != 0;
    }

    private bool TryGetActiveMagicPot(out OccultEventSnapshot magicPot)
    {
        var expectedId = target?.DataId ?? 0;
        magicPot = eventTracker.ActiveEvents
            .Where(item => item.Kind == OccultEventKind.MagicPot && item.IsJoinable)
            .OrderByDescending(item => item.DataId == expectedId)
            .ThenByDescending(item => item.RemainingSeconds)
            .FirstOrDefault();
        return magicPot.DataId != 0;
    }

    private bool IsCommittedToCurrentEvent()
    {
        if (Stage == EventAutomationStage.Participating) return true;
        if (target is not { } current) return condition[ConditionFlag.InCombat];
        return condition[ConditionFlag.InCombat] ||
               current.Kind == OccultEventKind.CriticalEngagement && IsCurrentCriticalEngagement(current.DataId) ||
               current.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot && IsCurrentFate(current.DataId);
    }

    private void ResolveTreasureIdentifiers()
    {
        if (guidanceStatusId != 0 && elixirItemId != 0) return;
        var now = Environment.TickCount64;
        if (now < nextIdentifierResolve) return;
        nextIdentifierResolve = now + 5_000;

        if (guidanceStatusId == 0)
        {
            foreach (var row in dataManager.GetExcelSheet<StatusRow>())
            {
                var name = row.Name.ToString().Trim();
                if (!IsTreasureGuidanceName(name)) continue;
                guidanceStatusId = row.RowId;
                log.Information("Resolved treasure guidance status {StatusId}: {Name}.", row.RowId, name);
                break;
            }
        }

        if (elixirItemId == 0)
        {
            foreach (var row in dataManager.GetExcelSheet<EventItemRow>())
            {
                var name = row.Name.ToString().Trim();
                if (string.IsNullOrEmpty(name)) name = row.Singular.ToString().Trim();
                if (!string.Equals(name, "魔法圣灵药", StringComparison.Ordinal) &&
                    !name.Contains("Magic Elixir", StringComparison.OrdinalIgnoreCase))
                    continue;
                elixirItemId = row.RowId;
                log.Information("Resolved magic-pot event item {ItemId}: {Name}.", row.RowId, name);
                break;
            }
        }
    }

    private bool HasTreasureGuidance(IPlayerCharacter player)
    {
        if (guidanceStatusId != 0 && player.StatusList.Any(status => status.StatusId == guidanceStatusId))
            return true;

        foreach (var status in player.StatusList)
        {
            var row = dataManager.GetExcelSheet<StatusRow>().GetRowOrDefault(status.StatusId);
            var name = row?.Name.ToString().Trim();
            if (name == null || !IsTreasureGuidanceName(name)) continue;
            guidanceStatusId = status.StatusId;
            return true;
        }
        return false;
    }

    private static bool IsTreasureGuidanceName(string name) =>
        name.Contains("指引财宝", StringComparison.Ordinal) ||
        name.Contains("财宝诱导", StringComparison.Ordinal) ||
        name.Contains("Cache Me if You Can", StringComparison.OrdinalIgnoreCase);

    private void BeginTreasureHunt()
    {
        navigationService.Cancel(null);
        combatIntegrations.Disarm();
        target = null;
        navigationIssued = false;
        treasureCandidateId = 0;
        activeTreasure = null;
        treasureInteractionIssued = false;
        treasureInteractionStartedAt = 0;
        treasureGuidanceLostAt = 0;
        treasureObjectsAtHuntStart.Clear();
        foreach (var gameObject in objectTable)
            if (gameObject != null && gameObject.IsValid() && gameObject.ObjectKind == ObjectKind.Treasure)
                treasureObjectsAtHuntStart.Add(gameObject.GameObjectId);
        awaitingSpawnedMagicPotTreasure = true;
        ResetTreasureCandidateFailures();
        nextTreasureAction = 0;
        Stage = EventAutomationStage.TreasureHunting;
        Status = "已确认获得指引财宝，开始自动寻宝";
    }

    private void UpdateTreasureHunt(Vector3 playerPosition, long now, bool hasGuidance)
    {
        RefreshTreasureCandidateFailures();
        if (activeTreasure == null && TryCaptureSpawnedMagicPotTreasure(playerPosition, out var spawnedTreasure))
            activeTreasure = spawnedTreasure;
        if (treasureTracker.ConfirmedTreasure is { } confirmedTreasure)
        {
            if (activeTreasure?.GameObjectId != confirmedTreasure.GameObjectId)
            {
                treasureInteractionIssued = false;
                treasureInteractionStartedAt = 0;
                nextTreasureAction = 0;
            }
            activeTreasure = confirmedTreasure;
        }
        if (activeTreasure is { } treasure)
        {
            UpdateConfirmedTreasure(playerPosition, treasure, now);
            return;
        }
        if (!hasGuidance)
        {
            navigationService.Cancel(null);
            treasureCandidateId = 0;
            treasureGuidanceLostAt = treasureGuidanceLostAt == 0 ? now : treasureGuidanceLostAt;
            if (now - treasureGuidanceLostAt < TreasureDiscoveryGraceMilliseconds)
            {
                Status = "指引财宝已结束，正在确认出现的宝箱";
                return;
            }
            BeginSettling("指引财宝已结束，恢复自动事件循环");
            return;
        }
        treasureGuidanceLostAt = 0;
        if (condition[ConditionFlag.InCombat])
        {
            navigationService.Cancel(null);
            Status = "指引财宝有效，等待脱离战斗后继续寻宝";
            return;
        }

        if (treasureTracker.FocusedCandidate is not { } candidate)
        {
            navigationService.Cancel(null);
            treasureCandidateId = 0;
            if (TryUseElixir(now))
                Status = "已使用魔法圣灵药，正在等待魔法罐给出提示";
            else if (elixirItemId == 0)
                Status = "指引财宝有效，但未能识别任务道具“魔法圣灵药”";
            else
                Status = $"魔法圣灵药尚未使用：{treasureActionFailure}";
            return;
        }

        if (treasureCandidateId != candidate.Id)
        {
            navigationService.Cancel(null);
            treasureCandidateId = candidate.Id;
            navigationIssued = false;
        }

        if (treasureCandidatesExhausted)
        {
            Status = "当前提示下的候选点均已尝试，等待新的魔法罐提示";
            return;
        }

        if (pendingTreasureProbeCandidateId != 0)
        {
            if (treasureTracker.Session.Stage == PotSessionStage.AwaitingTreasure)
            {
                Status = "已发现财宝，正在等待宝箱出现";
                return;
            }
            if (pendingTreasureProbeCandidateId == candidate.Id && now < pendingTreasureProbeDeadline)
            {
                Status = $"已在候选 #{treasureTracker.GetCandidateNumber(candidate):D2} 使用魔法圣灵药，正在确认结果";
                return;
            }
            pendingTreasureProbeCandidateId = 0;
            pendingTreasureProbeDeadline = 0;
        }

        if (attemptedTreasureCandidateIds.Contains(candidate.Id))
        {
            AdvanceTreasureCandidate(candidate, "未发现财宝");
            return;
        }

        if (unreachableTreasureCandidateIds.Contains(candidate.Id))
        {
            Status = "当前提示下的候选点均找不到可达导航网格，等待新的魔法罐提示";
            return;
        }

        var originalDistance = HorizontalDistance(playerPosition, candidate.Position);
        var resolvedDistance = navigationIssued
            ? HorizontalDistance(playerPosition, navigationService.ResolvedDestination)
            : float.MaxValue;
        var distance = Math.Min(originalDistance, resolvedDistance);
        if (distance > TreasureArrivalRadius)
        {
            if (!EnsureTreasureNavigation(candidate.Position,
                    $"魔法罐财宝候选 #{treasureTracker.GetCandidateNumber(candidate):D2}",
                    allowLargeHeightCorrection: true))
            {
                SkipUnreachableTreasureCandidate(candidate);
                return;
            }
            Status = $"正在前往财宝候选 #{treasureTracker.GetCandidateNumber(candidate):D2} · 距离 {distance:F0}m";
            return;
        }

        navigationService.Cancel(null);
        navigationIssued = false;
        if (TryUseElixir(now))
        {
            attemptedTreasureCandidateIds.Add(candidate.Id);
            pendingTreasureProbeCandidateId = candidate.Id;
            pendingTreasureProbeDeadline = now + TreasureProbeConfirmationMilliseconds;
            Status = $"已在候选 #{treasureTracker.GetCandidateNumber(candidate):D2} 使用魔法圣灵药，正在确认结果";
        }
        else
            Status = $"已抵达候选 #{treasureTracker.GetCandidateNumber(candidate):D2}：{treasureActionFailure}";
    }

    private void UpdateConfirmedTreasure(Vector3 playerPosition, TreasureSnapshot treasure, long now)
    {
        var distance = HorizontalDistance(playerPosition, treasure.Position);
        if (distance > TreasureArrivalRadius)
        {
            if (!EnsureTreasureNavigation(treasure.Position, "魔法罐发现的财宝")) return;
            Status = $"财宝已经出现，正在接近 · 距离 {distance:F0}m";
            return;
        }

        navigationService.Cancel(null);
        navigationIssued = false;
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting])
        {
            navigationService.RequestDismount();
            Status = "已抵达财宝，正在下坐骑";
            return;
        }
        if (now < nextTreasureAction) return;
        nextTreasureAction = now + 2_000;

        var gameObject = objectTable.FirstOrDefault(item =>
            item.GameObjectId == treasure.GameObjectId && item.ObjectKind == ObjectKind.Treasure &&
            item.Address != nint.Zero);
        if (gameObject is null)
        {
            if (treasureInteractionIssued &&
                now - treasureInteractionStartedAt < TreasureInteractionConfirmationMilliseconds)
            {
                Status = "已发送开启请求，正在确认宝箱状态";
            }
            else
            {
                treasureInteractionIssued = false;
                treasureInteractionStartedAt = 0;
                activeTreasure = null;
                Status = "未确认宝箱已开启，正在重新查找宝箱对象";
            }
            return;
        }

        var targetSystem = TargetSystem.Instance();
        var nativeObject = (NativeGameObject*)(void*)gameObject.Address;
        var nativeTreasure = (NativeTreasure*)(void*)gameObject.Address;
        if (targetSystem == null || nativeObject == null || nativeTreasure == null)
        {
            Status = "无法取得宝箱交互状态";
            return;
        }

        if ((nativeTreasure->Flags & NativeTreasure.TreasureFlags.Opened) != 0)
        {
            var calibrationCandidateId = pendingTreasureProbeCandidateId != 0
                ? pendingTreasureProbeCandidateId
                : treasureCandidateId;
            treasureTracker.RecordOpenedMagicPotTreasure(treasure, calibrationCandidateId);
            activeTreasure = null;
            treasureInteractionIssued = false;
            treasureInteractionStartedAt = 0;
            treasureGuidanceLostAt = 0;
            awaitingSpawnedMagicPotTreasure = false;
            treasureObjectsAtHuntStart.Clear();
            BeginSettling("已确认开启魔法罐发现的财宝，恢复自动事件循环");
            return;
        }

        if (!gameObject.IsTargetable)
        {
            if (treasureInteractionIssued &&
                now - treasureInteractionStartedAt >= TreasureInteractionConfirmationMilliseconds)
            {
                treasureInteractionIssued = false;
                treasureInteractionStartedAt = 0;
                activeTreasure = null;
                Status = "宝箱未返回开启确认，正在重新查找宝箱对象";
            }
            else
            {
                Status = treasureInteractionIssued
                    ? "已发送开启请求，正在等待宝箱确认"
                    : "已抵达财宝位置，正在等待宝箱对象可交互";
            }
            return;
        }

        targetManager.Target = gameObject;
        targetSystem->Target = nativeObject;
        var interacted = targetSystem->InteractWithObject(nativeObject, false) != 0;
        if (interacted)
        {
            targetSystem->OpenObjectInteraction(nativeObject);
            treasureInteractionIssued = true;
            treasureInteractionStartedAt = now;
            Status = "已发送宝箱开启请求，正在确认结果";
        }
        else
        {
            treasureInteractionIssued = false;
            treasureInteractionStartedAt = 0;
            Status = "客户端未接受宝箱开启请求，正在重试";
        }
    }

    private bool TryCaptureSpawnedMagicPotTreasure(Vector3 playerPosition, out TreasureSnapshot treasure)
    {
        treasure = default;
        if (!awaitingSpawnedMagicPotTreasure) return false;
        var focus = treasureTracker.FocusedCandidate?.Position;
        var gameObject = objectTable
            .Where(item => item != null && item.IsValid() && item.ObjectKind == ObjectKind.Treasure &&
                           item.Address != nint.Zero && !treasureObjectsAtHuntStart.Contains(item.GameObjectId) &&
                           !treasureTracker.IsFieldTreasureObject(item))
            .Where(item => HorizontalDistance(playerPosition, item.Position) <= 35f ||
                           focus is { } candidate && HorizontalDistance(candidate, item.Position) <= 35f)
            .OrderBy(item => HorizontalDistance(playerPosition, item.Position))
            .FirstOrDefault();
        if (gameObject == null) return false;
        var nativeTreasure = (NativeTreasure*)(void*)gameObject.Address;
        if (nativeTreasure == null ||
            (nativeTreasure->Flags & NativeTreasure.TreasureFlags.Opened) != 0 ||
            (nativeTreasure->Flags & NativeTreasure.TreasureFlags.FadedOut) != 0 ||
            !gameObject.IsTargetable)
            return false;
        treasure = new TreasureSnapshot(gameObject.GameObjectId, gameObject.Position);
        Status = "已通过新出现的宝箱对象确认魔法罐财宝";
        return true;
    }

    private bool EnsureTreasureNavigation(
        Vector3 destination,
        string label,
        bool allowLargeHeightCorrection = false)
    {
        var following = navigationService.ActiveDestination is { } active &&
                        HorizontalDistance(active, destination) <= 3f;
        if (navigationService.IsNavigating && (navigationIssued || following)) return true;
        navigationService.Cancel(null);
        navigationIssued = navigationService.NavigateTo(
            destination,
            label,
            allowLargeHeightCorrection: allowLargeHeightCorrection);
        if (!navigationIssued)
            Status = $"无法前往{label}：{navigationService.Status}";
        return navigationIssued;
    }

    private bool EnsureNavigation(Vector3 destination, string label)
    {
        var following = navigationService.ActiveDestination is { } active &&
                        HorizontalDistance(active, destination) <= 3f;
        if (navigationService.IsNavigating && following) return true;
        navigationService.Cancel(null);
        navigationIssued = navigationService.NavigateTo(destination, label);
        return navigationIssued;
    }

    private void RefreshTreasureCandidateFailures()
    {
        var round = treasureTracker.Session.Round;
        var hintCount = treasureTracker.Session.AcceptedHints.Count;
        if (treasureCandidateRound != round)
        {
            ResetTreasureCandidateFailures();
            return;
        }
        if (treasureHintCount == hintCount) return;
        unreachableTreasureCandidateIds.Clear();
        treasureHintCount = hintCount;
        pendingTreasureProbeCandidateId = 0;
        pendingTreasureProbeDeadline = 0;
        treasureCandidatesExhausted = false;
    }

    private void ResetTreasureCandidateFailures()
    {
        unreachableTreasureCandidateIds.Clear();
        attemptedTreasureCandidateIds.Clear();
        treasureCandidateRound = treasureTracker.Session.Round;
        treasureHintCount = treasureTracker.Session.AcceptedHints.Count;
        pendingTreasureProbeCandidateId = 0;
        pendingTreasureProbeDeadline = 0;
        treasureCandidatesExhausted = false;
    }

    private void SkipUnreachableTreasureCandidate(PotCandidate failedCandidate)
    {
        unreachableTreasureCandidateIds.Add(failedCandidate.Id);
        AdvanceTreasureCandidate(failedCandidate, "附近没有可达网格");
    }

    private void AdvanceTreasureCandidate(PotCandidate failedCandidate, string reason)
    {
        navigationService.Cancel(null);
        navigationIssued = false;

        for (var index = 0; index < treasureTracker.Session.Candidates.Count; index++)
        {
            if (!treasureTracker.FocusNext() || treasureTracker.FocusedCandidate is not { } nextCandidate)
                break;
            if (unreachableTreasureCandidateIds.Contains(nextCandidate.Id) ||
                attemptedTreasureCandidateIds.Contains(nextCandidate.Id))
                continue;

            treasureCandidateId = nextCandidate.Id;
            nextTreasureAction = 0;
            Status = $"候选 #{treasureTracker.GetCandidateNumber(failedCandidate):D2} {reason}，改试候选 #{treasureTracker.GetCandidateNumber(nextCandidate):D2}";
            return;
        }

        treasureCandidateId = 0;
        treasureCandidatesExhausted = true;
        Status = "当前提示下的候选点均已尝试，等待新的魔法罐提示";
    }

    private bool TryUseElixir(long now)
    {
        if (elixirItemId == 0)
        {
            treasureActionFailure = "未识别任务道具";
            return false;
        }
        if (!HasMagicElixir())
        {
            treasureActionFailure = "任务道具栏中没有魔法圣灵药";
            return false;
        }
        if (now < nextTreasureAction)
        {
            treasureActionFailure = "等待再次尝试";
            return false;
        }
        if (condition[ConditionFlag.Casting])
        {
            treasureActionFailure = "角色正在咏唱";
            return false;
        }
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            treasureActionFailure = "无法取得动作管理器";
            return false;
        }
        var actionStatus = actionManager->GetActionStatus(ActionType.EventItem, elixirItemId);
        if (actionStatus != 0)
        {
            treasureActionFailure = $"任务道具当前不可用（状态 {actionStatus}）";
            return false;
        }
        nextTreasureAction = now + TreasureActionRetryMilliseconds;
        var used = actionManager->UseAction(ActionType.EventItem, elixirItemId);
        treasureActionFailure = used ? string.Empty : "客户端拒绝使用任务道具";
        return used;
    }

    private bool HasMagicElixir()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;
        var container = inventoryManager->GetInventoryContainer(InventoryType.KeyItems);
        if (container == null || !container->IsLoaded) return false;
        for (var index = 0; index < container->Size; index++)
        {
            var slot = container->GetInventorySlot(index);
            if (slot != null && slot->ItemId == elixirItemId && slot->Quantity > 0) return true;
        }
        return false;
    }

    private void Suspend(string reason)
    {
        if (Stage == EventAutomationStage.Suspended) return;
        navigationService.Cancel(null);
        combatIntegrations.Disarm();
        navigationIssued = false;
        Stage = EventAutomationStage.Suspended;
        Status = reason;
        log.Warning("Event automation suspended: {Reason}", reason);
    }

    private void Reset(EventAutomationStage stage, string status, bool cancelNavigation)
    {
        if (cancelNavigation) navigationService.Cancel(null);
        combatIntegrations.Disarm();
        target = null;
        targetLastSeenAt = 0;
        awaitingStartedAt = 0;
        settleStartedAt = 0;
        nextCombatApproach = 0;
        magicPotDeadline = 0;
        nextTreasureAction = 0;
        treasureCandidateId = 0;
        activeTreasure = null;
        treasureInteractionIssued = false;
        treasureInteractionStartedAt = 0;
        treasureObjectsAtHuntStart.Clear();
        awaitingSpawnedMagicPotTreasure = false;
        treasureGuidanceLostAt = 0;
        ResetTreasureCandidateFailures();
        navigationIssued = false;
        Stage = stage;
        Status = status;
    }

    private EventAutomationWaitingPoint? CurrentWaitingPoint()
    {
        var custom = configuration.EventAutomationWaitingPoints.FirstOrDefault(item =>
            item.TerritoryId == clientState.TerritoryType);
        if (custom is not null) return custom;

        var baseCrystal = CrescentAetheryteCatalog.ForTerritory(clientState.TerritoryType)
            .FirstOrDefault();
        return baseCrystal.DataId == 0 ? null : new EventAutomationWaitingPoint
        {
            TerritoryId = clientState.TerritoryType,
            X = baseCrystal.Position.X,
            Y = baseCrystal.Position.Y,
            Z = baseCrystal.Position.Z
        };
    }

    private CrescentAetheryte? DefaultWaitingAetheryte()
    {
        var camp = CrescentAetheryteCatalog.ForTerritory(clientState.TerritoryType).FirstOrDefault();
        return camp.DataId == 0 ? null : camp;
    }

    private static bool IsCurrentFate(uint fateId)
    {
        var fateManager = FateManager.Instance();
        return fateManager != null && fateManager->CurrentFate != null &&
               fateManager->CurrentFate->FateId == fateId;
    }

    private static bool IsCurrentCriticalEngagement(uint eventId)
    {
        var container = DynamicEventContainer.GetInstance();
        return container != null && container->CurrentEventId == eventId;
    }

    private static bool IsEventExplicitlyComplete(
        OccultEventSnapshot expected,
        OccultEventSnapshot? active)
    {
        // A missing tracker entry still uses the normal three-second confirmation window.
        // These checks are only for completion evidence that is visible while the entry remains.
        if (active is not { } observed) return false;
        if (expected.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot)
            return observed.Progress >= 100 || !IsCurrentFate(expected.DataId);
        if (expected.Kind == OccultEventKind.CriticalEngagement)
            return observed.Phase != OccultEventPhase.Battle ||
                   !IsCurrentCriticalEngagement(expected.DataId);
        return false;
    }

    private string MaintainFateEngagement(OccultEventSnapshot active, Vector3 playerPosition, long now)
    {
        var enemy = AcquireFateTarget(active.DataId, playerPosition);
        if (enemy is null)
        {
            if (navigationService.IsNavigating) navigationService.Cancel(null);
            navigationIssued = false;
            return $"暂未发现存活敌人，正在原地等待下一批 · {active.StateText}";
        }

        var edgeDistance = MathF.Max(0f,
            HorizontalDistance(playerPosition, enemy.Position) - MathF.Max(0f, enemy.HitboxRadius));
        if (edgeDistance <= CurrentEngagementRange)
        {
            if (navigationService.IsNavigating) navigationService.Cancel(null);
            return $"已锁定 {enemy.Name} · {active.StateText}";
        }

        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting])
        {
            navigationService.Cancel(null);
            navigationService.RequestDismount();
            return $"正在下坐骑并接近 {enemy.Name} · 距离 {edgeDistance:F0}m";
        }

        if (!navigationService.IsNavigating && now >= nextCombatApproach)
        {
            nextCombatApproach = now + CombatApproachRetryMilliseconds;
            navigationIssued = navigationService.NavigateTo(enemy.Position,
                $"FATE 敌人：{enemy.Name}", false);
        }
        return $"正在追击 {enemy.Name} · 距离 {edgeDistance:F0}m";
    }

    private bool PrepareFateEngagement(OccultEventSnapshot active, Vector3 playerPosition, long now)
    {
        var enemy = AcquireFateTarget(active.DataId, playerPosition);
        if (enemy is null)
        {
            var centerDistance = HorizontalDistance(playerPosition, active.Position);
            if (centerDistance > 5f && !navigationService.IsNavigating && now >= nextCombatApproach)
            {
                nextCombatApproach = now + CombatApproachRetryMilliseconds;
                navigationIssued = navigationService.NavigateTo(active.Position,
                    $"寻找 FATE 敌人：{active.Name}");
            }
            Status = centerDistance > 5f
                ? $"正在进入 {active.Name} 核心区域寻找敌人"
                : $"已到达 {active.Name} 核心区域，正在寻找敌人";
            return false;
        }

        var edgeDistance = MathF.Max(0f,
            HorizontalDistance(playerPosition, enemy.Position) - MathF.Max(0f, enemy.HitboxRadius));
        if (edgeDistance > CurrentEngagementRange)
        {
            var activeDestination = navigationService.ActiveDestination;
            var followingEnemy = activeDestination is { } destination &&
                                 HorizontalDistance(destination, enemy.Position) <= 5f;
            if ((!navigationService.IsNavigating || !followingEnemy) && now >= nextCombatApproach)
            {
                nextCombatApproach = now + CombatApproachRetryMilliseconds;
                navigationService.Cancel(null);
                navigationIssued = navigationService.NavigateTo(enemy.Position,
                    $"接近 FATE 敌人：{enemy.Name}");
            }
            Status = $"已发现 {enemy.Name}，正在接近可攻击距离 · {edgeDistance:F0}m";
            return false;
        }

        navigationService.Cancel(null);
        navigationIssued = false;
        targetManager.Target = enemy;
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting])
        {
            navigationService.RequestDismount();
            Status = $"已锁定 {enemy.Name}，正在下坐骑";
            return false;
        }

        Status = $"已锁定 {enemy.Name}，准备启动战斗接管";
        return true;
    }

    private bool PrepareCriticalEngagement(OccultEventSnapshot active, Vector3 playerPosition, long now)
    {
        var enemy = AcquireCriticalEngagementTarget(active.Position, playerPosition);
        if (enemy is null)
        {
            if (navigationService.IsNavigating) navigationService.Cancel(null);
            navigationIssued = false;
            Status = $"已进入 {active.Name}，正在等待可攻击目标出现";
            return false;
        }

        return PrepareEnemyEngagement(enemy, playerPosition, now, "CE");
    }

    private string MaintainCriticalEngagement(OccultEventSnapshot active, Vector3 playerPosition, long now)
    {
        var enemy = AcquireCriticalEngagementTarget(active.Position, playerPosition);
        if (enemy is null)
        {
            if (navigationService.IsNavigating) navigationService.Cancel(null);
            navigationIssued = false;
            return $"暂未发现存活敌人，正在原地等待下一批 · {active.StateText}";
        }

        var edgeDistance = MathF.Max(0f,
            HorizontalDistance(playerPosition, enemy.Position) - MathF.Max(0f, enemy.HitboxRadius));
        if (edgeDistance <= CurrentEngagementRange)
        {
            if (navigationService.IsNavigating) navigationService.Cancel(null);
            targetManager.Target = enemy;
            return $"已锁定 {enemy.Name} · {active.StateText}";
        }

        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting])
        {
            navigationService.Cancel(null);
            navigationService.RequestDismount();
            return $"正在下坐骑并接近 {enemy.Name} · 距离 {edgeDistance:F0}m";
        }

        if (!navigationService.IsNavigating && now >= nextCombatApproach)
        {
            nextCombatApproach = now + CombatApproachRetryMilliseconds;
            navigationIssued = navigationService.NavigateTo(enemy.Position,
                $"CE 敌人：{enemy.Name}", false);
        }
        return $"正在追击 {enemy.Name} · 距离 {edgeDistance:F0}m";
    }

    private bool PrepareEnemyEngagement(IBattleNpc enemy, Vector3 playerPosition, long now, string eventLabel)
    {
        var edgeDistance = MathF.Max(0f,
            HorizontalDistance(playerPosition, enemy.Position) - MathF.Max(0f, enemy.HitboxRadius));
        if (edgeDistance > CurrentEngagementRange)
        {
            var activeDestination = navigationService.ActiveDestination;
            var followingEnemy = activeDestination is { } destination &&
                                 HorizontalDistance(destination, enemy.Position) <= 5f;
            if ((!navigationService.IsNavigating || !followingEnemy) && now >= nextCombatApproach)
            {
                nextCombatApproach = now + CombatApproachRetryMilliseconds;
                navigationService.Cancel(null);
                navigationIssued = navigationService.NavigateTo(enemy.Position,
                    $"接近 {eventLabel} 敌人：{enemy.Name}");
            }
            Status = $"已发现 {enemy.Name}，正在接近可攻击距离 · {edgeDistance:F0}m";
            return false;
        }

        navigationService.Cancel(null);
        navigationIssued = false;
        targetManager.Target = enemy;
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting])
        {
            navigationService.RequestDismount();
            Status = $"已锁定 {enemy.Name}，正在下坐骑";
            return false;
        }

        Status = $"已锁定 {enemy.Name}，准备启动战斗接管";
        return true;
    }

    private IBattleNpc? AcquireFateTarget(uint fateId, Vector3 playerPosition)
    {
        if (targetManager.Target is IBattleNpc current)
        {
            if (IsFateEnemy(current, fateId)) return current;
            targetManager.Target = null;
        }

        var enemy = FindFateEnemy(fateId, playerPosition);
        if (enemy is not null) targetManager.Target = enemy;
        return enemy;
    }

    private IBattleNpc? FindFateEnemy(uint fateId, Vector3 playerPosition) =>
        objectTable.OfType<IBattleNpc>()
            .Where(item => IsFateEnemy(item, fateId))
            .OrderByDescending(item => item.MaxHp)
            .ThenBy(item => HorizontalDistance(playerPosition, item.Position) - MathF.Max(0f, item.HitboxRadius))
            .FirstOrDefault();

    private IBattleNpc? AcquireCriticalEngagementTarget(Vector3 eventCenter, Vector3 playerPosition)
    {
        var enemy = objectTable.OfType<IBattleNpc>()
            .Where(item => IsCriticalEngagementEnemy(item, eventCenter))
            .OrderByDescending(item => item.MaxHp)
            .ThenBy(item => HorizontalDistance(playerPosition, item.Position) - MathF.Max(0f, item.HitboxRadius))
            .FirstOrDefault();
        if (enemy is not null)
            targetManager.Target = enemy;
        else if (targetManager.Target is IBattleNpc current && IsCriticalEngagementEnemy(current, eventCenter))
            return current;
        else
            targetManager.Target = null;
        return enemy;
    }

    private static bool IsCriticalEngagementEnemy(IBattleNpc enemy, Vector3 eventCenter) =>
        IsHostileCombatant(enemy) &&
        HorizontalDistance(enemy.Position, eventCenter) <= CriticalEngagementCombatRadius;

    private static bool IsFateEnemy(IBattleNpc enemy, uint fateId)
    {
        if (!IsHostileCombatant(enemy)) return false;
        var battleChara = (BattleChara*)(void*)enemy.Address;
        return battleChara != null && battleChara->IsHostile && battleChara->FateId == fateId;
    }

    private static bool IsHostileCombatant(IBattleNpc enemy)
    {
        if (enemy.Address == nint.Zero || enemy.IsDead || !enemy.IsTargetable || enemy.CurrentHp == 0 ||
            enemy.BattleNpcKind != BattleNpcSubKind.Combatant ||
            (enemy.StatusFlags & StatusFlags.Hostile) == 0)
            return false;
        var battleChara = (BattleChara*)(void*)enemy.Address;
        return battleChara != null && battleChara->IsHostile;
    }

    private static float EngagementRangeForJob(uint classJobId) => classJobId is
        5 or 6 or 7 or 23 or 24 or 25 or 26 or 27 or 28 or 31 or 33 or 35 or 36 or 38 or 40 or 42
            ? RangedEngagementRange
            : MeleeEngagementRange;

    private static bool SameAutomationKind(OccultEventKind left, OccultEventKind right) =>
        left == right ||
        left is OccultEventKind.Fate or OccultEventKind.MagicPot &&
        right is OccultEventKind.Fate or OccultEventKind.MagicPot;

    private static bool SupportedTerritory(uint territoryId) =>
        territoryId is PotCandidateCatalog.SouthHornTerritoryId or PotCandidateCatalog.NorthHornTerritoryId;

    private static string AreaName(uint territoryId) => territoryId switch
    {
        PotCandidateCatalog.SouthHornTerritoryId => "南部",
        PotCandidateCatalog.NorthHornTerritoryId => "北部",
        _ => "当前区域"
    };

    private static CustomNavigationRouteKind RouteKind(OccultEventKind kind) =>
        kind == OccultEventKind.CriticalEngagement
            ? CustomNavigationRouteKind.CriticalEngagement
            : CustomNavigationRouteKind.Fate;

    private static string KindLabel(OccultEventKind kind) => kind switch
    {
        OccultEventKind.CriticalEngagement => "CE",
        OccultEventKind.MagicPot => "魔法罐",
        OccultEventKind.MagicPotForecast => "魔法罐预告",
        _ => "FATE"
    };

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return MathF.Sqrt(x * x + z * z);
    }
}
