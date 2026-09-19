using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Integrations;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GameMap = Lumina.Excel.Sheets.Map;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CrescentCompass.Services;

public sealed unsafe class NavigationService : IDisposable
{
    private const uint DemiReturnActionId = 41343;
    private const float MountMinimumPathDistance = 40f;
    private const float CrystalCampRadius = 100f;
    private const float PathOriginReplanDistance = 2.5f;
    private const int MaxPathOriginReplans = 2;
    private const long PathPlanningFallbackMilliseconds = 8_000;
    private const long RouteDecisionWindowMilliseconds = 3_000;
    private const long CrystalNavigationRetryMilliseconds = 1_000;
    private const float CrystalNavigationArrivalRadius = 0.75f;
    private const float CrystalInteractionPadding = 1f;
    private const float CrystalStuckRecoverySuppressionRadius = 15f;
    private const long NavigationIdleConfirmationMilliseconds = 750;
    private const float RouteRecordingArrivalRadius = 8f;
    private const long RouteRecordingArrivalConfirmationMilliseconds = 750;
    private const float CustomRouteJumpTriggerRadius = 2.25f;
    private const long CustomRouteJumpRetryMilliseconds = 700;
    private const long StuckSampleMilliseconds = 1_000;
    private const float StuckMovementDistance = 1f;
    private const float StuckRecoveryClearDistance = 3f;
    private const long MountRequestIntervalMilliseconds = 250;
    private readonly PluginConfiguration configuration;
    private readonly TreasureTracker tracker;
    private readonly VNavmeshIpc vnavmesh;
    private readonly IKeyState keyState;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly Action save;
    private CancellationTokenSource? pathCancellation;
    private Task<List<Vector3>>? pathTask;
    private Vector3 pathOrigin;
    private Vector3 pathRequestPlayerPosition;
    private Vector3 pathNavigationPoint;
    private int pathOriginReplans;
    private bool active;
    private bool ownsMovingPath;
    private bool observedBusy;
    private long navigationIdleSince;
    private long startedAt;
    private Vector3 destination;
    private Vector3 requestedDestination;
    private Vector3? eventNavigationTarget;
    private string targetName = string.Empty;
    private CrescentAetheryte? pendingAetheryte;
    private CrescentAetheryte? sourceAetheryte;
    private string pendingAetheryteName = string.Empty;
    private long nextInteractionAt;
    private long nextCrystalNavigationAt;
    private long teleportDeadline;
    private bool demiReturnPending;
    private bool demiReturnAccepted;
    private bool demiReturnSawCasting;
    private bool demiReturnSawTransition;
    private bool completeAtCampAfterDemiReturn;
    private Vector3 demiReturnOrigin;
    private long demiReturnStartedAt;
    private long nextDemiReturnAt;
    private long cancelInputArmedAt;
    private PendingRoutePlan? routePlan;
    private NavigationFollowUp? pendingFollowUp;
    private NavigationFollowUp? arrivalFollowUp;
    private CrescentAetheryte? arrivalAetheryte;
    private Vector3 arrivalOrigin;
    private long arrivalArmedAt;
    private long nextAggroPathCheck;
    private long nextMountRequest;
    private long mountRequestedAt;
    private long nextDismountRequest;
    private long dismountRequestedAt;
    private IReadOnlyList<Vector3> displayPath = [];
    private StuckRecoveryStage stuckRecoveryStage;
    private Vector3 stuckSamplePosition;
    private long stuckSampleAt;
    private Vector3 stuckOrigin;
    private long jumpRecoveryAt;
    private bool recoveryDetourActive;
    private Vector3 recoveryFinalDestination;
    private string recoveryFinalName = string.Empty;
    private RecoveryCell recoveryCell;
    private int recoverySide;
    private uint recoveryTerritory;
    private readonly Dictionary<RecoveryCell, RecoveryMemory> recoveryMemory = [];
    private readonly List<RecordedRouteSample> recordedRouteSamples = [];
    private uint recordedRouteTerritory;
    private uint recordedRouteSourceAetheryte;
    private uint recordedRouteEventId;
    private CustomNavigationRouteKind recordedRouteKind;
    private string recordedRouteEventName = string.Empty;
    private Vector3? recordedRouteDestination;
    private long recordedRouteArrivalSince;
    private long nextRouteRecordSample;
    private bool recordingWasJumping;
    private readonly List<Vector3> activeCustomRouteJumps = [];
    private readonly List<Vector3> activeCustomRoutePath = [];
    private uint activeCustomRouteEventId;
    private int activeCustomRouteJumpIndex;
    private int customRouteRestartAttempts;
    private long customRouteJumpRequestedAt;

    public NavigationService(
        PluginConfiguration configuration,
        TreasureTracker tracker,
        VNavmeshIpc vnavmesh,
        IKeyState keyState,
        IFramework framework,
        IObjectTable objectTable,
        ICondition condition,
        ICommandManager commandManager,
        IDataManager dataManager,
        Action save)
    {
        this.configuration = configuration;
        this.tracker = tracker;
        this.vnavmesh = vnavmesh;
        this.keyState = keyState;
        this.framework = framework;
        this.objectTable = objectTable;
        this.condition = condition;
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.save = save;
        framework.Update += OnFrameworkUpdate;
    }

    public string Status { get; private set; } = string.Empty;
    public string DecisionStatus { get; private set; } = "尚未选择路线";
    public bool IsNavigating => active || pendingAetheryte != null || routePlan != null || arrivalFollowUp != null;
    public Vector3? ActiveDestination => eventNavigationTarget ??
        (active ? destination : routePlan?.FollowUp.Position ?? pendingFollowUp?.Position);
    public Vector3 ResolvedDestination => destination;
    public IReadOnlyList<Vector3> DisplayPath => displayPath;
    public bool IsVnavmeshReady => vnavmesh.IsAvailable();
    public bool IsFollowingCustomRoute => active && activeCustomRoutePath.Count > 1;
    public uint CurrentCustomRouteEventId => activeCustomRouteEventId != 0
        ? activeCustomRouteEventId
        : pendingFollowUp?.CustomRoute?.EventId ?? arrivalFollowUp?.CustomRoute?.EventId ?? 0;
    public bool IsRecordingCustomRoute => recordedRouteEventId != 0;
    public uint RecordingEventId => recordedRouteEventId;
    public CustomNavigationRouteKind RecordingRouteKind => recordedRouteKind;
    public Vector3? RecordingDestination => recordedRouteDestination;
    public IReadOnlyList<CustomNavigationRoute> AllNavigationRoutes
    {
        get
        {
            var userKeys = configuration.CustomNavigationRoutes
                .Select(RouteKey)
                .ToHashSet();
            return configuration.CustomNavigationRoutes
                .Concat(BuiltInNavigationRouteLibrary.Routes.Where(route => !userKeys.Contains(RouteKey(route))))
                .ToArray();
        }
    }
    public string RouteRecordingStatus { get; private set; } = "尚未录制自定义路线";

    public IReadOnlyList<CustomNavigationRoute> EffectiveNavigationRoutes(
        uint territoryId,
        uint eventId,
        CustomNavigationRouteKind kind)
    {
        var userRoutes = configuration.CustomNavigationRoutes
            .Where(route => RouteMatches(route, territoryId, eventId, kind))
            .ToArray();
        return userRoutes.Length > 0
            ? userRoutes
            : BuiltInNavigationRouteLibrary.Routes
                .Where(route => RouteMatches(route, territoryId, eventId, kind))
                .ToArray();
    }

    public static bool IsBuiltInRoute(CustomNavigationRoute route) =>
        BuiltInNavigationRouteLibrary.IsBuiltIn(route);

    public bool BeginCustomRouteRecording(uint territoryId, uint eventId,
        CustomNavigationRouteKind kind, string eventName, Vector3? eventDestination = null)
    {
        if (territoryId != tracker.TerritoryId || tracker.PlayerPosition is not { } player)
        {
            RouteRecordingStatus = "请先进入该事件所在的新月岛区域";
            return false;
        }
        var source = CrescentAetheryteCatalog.ForTerritory(territoryId)
            .OrderBy(item => HorizontalDistanceSquared(item.Position, player))
            .FirstOrDefault();
        if (source.DataId == 0 || HorizontalDistanceSquared(source.Position, player) > 55f * 55f)
        {
            RouteRecordingStatus = "请在目标传送点落地位置附近开始录制";
            return false;
        }

        Cancel(null);
        recordedRouteSamples.Clear();
        recordedRouteSamples.Add(new RecordedRouteSample(player, false, false));
        displayPath = recordedRouteSamples.Select(item => item.Position).ToArray();
        recordedRouteTerritory = territoryId;
        recordedRouteSourceAetheryte = source.DataId;
        recordedRouteEventId = eventId;
        recordedRouteKind = kind;
        recordedRouteEventName = eventName;
        recordedRouteDestination = eventDestination;
        recordedRouteArrivalSince = 0;
        nextRouteRecordSample = 0;
        recordingWasJumping = condition[ConditionFlag.Jumping];
        RouteRecordingStatus = eventDestination == null
            ? $"正在录制 {source.FallbackName} → {eventName} · 终点未知，请手动保存"
            : $"正在录制 {source.FallbackName} → {eventName} · 距终点 {HorizontalDistance(player, eventDestination.Value):F0}m";
        return true;
    }

    public bool FinishCustomRouteRecording()
    {
        if (!IsRecordingCustomRoute || tracker.PlayerPosition is not { } player) return false;
        AddRecordedRoutePoint(player, 0.5f, condition[ConditionFlag.Jumping]);
        var savedPoints = BuildRecordedRoutePoints();
        if (savedPoints.Count < 2 || PathDistance(ToVector(savedPoints[0]), savedPoints.Select(ToVector).ToList()) < 5f)
        {
            RouteRecordingStatus = "路线过短，未保存";
            ClearRouteRecording();
            return false;
        }
        var route = new CustomNavigationRoute
        {
            TerritoryId = recordedRouteTerritory,
            SourceAetheryteDataId = recordedRouteSourceAetheryte,
            Kind = recordedRouteKind,
            EventId = recordedRouteEventId,
            EventName = recordedRouteEventName,
            Points = savedPoints
        };
        configuration.CustomNavigationRoutes.Add(route);
        save();
        var pointCount = route.Points.Count;
        var name = route.EventName;
        ClearRouteRecording();
        RouteRecordingStatus = $"已保存前往 {name} 的完整路线 · {pointCount} 点";
        return true;
    }

    public void CancelCustomRouteRecording()
    {
        if (!IsRecordingCustomRoute) return;
        ClearRouteRecording();
        RouteRecordingStatus = "已取消路线录制";
    }

    public void DeleteCustomRoute(string routeId)
    {
        if (configuration.CustomNavigationRoutes.RemoveAll(route => route.Id == routeId) == 0) return;
        save();
        RouteRecordingStatus = "已删除自定义路线";
    }

    public bool TestCustomRoute(string routeId)
    {
        var route = configuration.CustomNavigationRoutes
                        .Concat(BuiltInNavigationRouteLibrary.Routes)
                        .FirstOrDefault(item => item.Id == routeId);
        if (route == null || route.TerritoryId != tracker.TerritoryId ||
            tracker.PlayerPosition is not { } player || route.Points.Count < 2)
        {
            Status = "请先进入该路线所在的新月岛区域";
            return false;
        }
        var endpoint = ToVector(route.Points[^1]);
        return BeginCustomEventRoute(route, endpoint, endpoint, $"测试路线：{route.EventName}", player, false);
    }

    public bool NavigateToEvent(Vector3 target, string name, uint eventId = 0,
        CustomNavigationRouteKind? routeKind = null, bool automaticRequest = false)
    {
        if (IsNavigating && (eventNavigationTarget ?? ActiveDestination) is { } currentTarget &&
            HorizontalDistanceSquared(currentTarget, target) < 1f)
        {
            Cancel("已取消自动导航");
            return false;
        }
        Cancel(null);
        if (tracker.PlayerPosition is not { } player) return false;
        var destinationPoint = vnavmesh.IsAvailable() ? ResolveDestinationPoint(target) : target;
        var randomizedEventEndpoint = configuration.RandomizeEventNavigationDestination &&
                                      destinationPoint is { } eventEndpoint &&
                                      HorizontalDistanceSquared(eventEndpoint, target) >= 1.5f * 1.5f;
        var useCustomRoute = automaticRequest
            ? configuration.UseCustomRoutesForAutomation
            : configuration.UseCustomRoutesForManualNavigation;
        if (useCustomRoute && eventId != 0 && routeKind is { } kind &&
            SelectCustomRoute(eventId, kind, player) is { } customRoute &&
            BeginCustomEventRoute(customRoute, target, destinationPoint ?? target, name, player,
                randomizedEventEndpoint))
            return true;
        if (configuration.DirectNavigationDistance > 0f &&
            HorizontalDistanceSquared(player, target) <=
            configuration.DirectNavigationDistance * configuration.DirectNavigationDistance)
        {
            DecisionStatus = $"{tracker.AreaName}：目标在 {configuration.DirectNavigationDistance:F0}m 直达阈值内";
            return RememberEventTarget(NavigateTo(destinationPoint ?? target, name), target);
        }
        if (!vnavmesh.IsAvailable())
        {
            return RememberEventTarget(
                FallBackFromRouteComparison(target, target, name, player, "vnavmesh 尚未就绪"), target);
        }
        var sources = CrescentAetheryteCatalog.ForTerritory(
            tracker.AreaName == "新月岛北部"
                ? Core.PotCandidateCatalog.NorthHornTerritoryId
                : Core.PotCandidateCatalog.SouthHornTerritoryId);
        if (destinationPoint is not { } resolved || sources.Count == 0)
            return RememberEventTarget(
                FallBackFromRouteComparison(target, target, name, player, "目标附近没有可比较落点"), target);

        var cancellation = new CancellationTokenSource();
        // Submit the direct route first so the most important baseline is not queued behind every crystal query.
        var directTask = ObservePathTask(vnavmesh.Pathfind(player, resolved, cancellation.Token));
        var routes = sources.OrderBy(source => HorizontalDistanceSquared(source.Position, resolved)).Take(2).Select(source =>
        {
            var origin = vnavmesh.QueryNearestReachable(source.Position, 8f, 80f) ?? source.Position;
            return (source, origin, path: ObservePathTask(vnavmesh.Pathfind(origin, resolved, cancellation.Token)));
        }).Where(route => route.path != null).ToArray();
        if (routes.Length == 0 || directTask == null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
            return RememberEventTarget(
                FallBackFromRouteComparison(target, resolved, name, player, "无法提交完整路线比较"), target);
        }

        var nearCrystal = sources.Any(source => HorizontalDistanceSquared(player, source.Position) <= 12f * 12f);
        var actionManager = ActionManager.Instance();
        var demiReturnAvailable = !IsInsideCrystalCamp(player, sources) && actionManager != null &&
                                  actionManager->GetActionStatus(ActionType.Action, DemiReturnActionId) == 0;
        // The crystal used to enter the teleport network is independent from the crystal nearest the destination.
        // Compare nearby access crystals separately; reusing destination-side candidates would count a walk across
        // the island before teleporting and grossly overestimate the transfer route.
        var approaches = nearCrystal
            ? []
            : sources.OrderBy(source => HorizontalDistanceSquared(player, source.Position)).Take(2).Select(source =>
            {
                var destination = vnavmesh.QueryNearestReachable(source.Position, 8f, 80f) ?? source.Position;
                return (source, origin: player,
                    path: ObservePathTask(vnavmesh.Pathfind(player, destination, cancellation.Token)));
            })
                .Where(route => route.path != null)
                .ToArray();
        var followUp = new NavigationFollowUp(target, resolved, name);
        var now = Environment.TickCount64;
        routePlan = new PendingRoutePlan(
            followUp,
            now,
            cancellation,
            player,
            resolved,
            condition[ConditionFlag.Mounted],
            nearCrystal,
            demiReturnAvailable,
            CollectRouteComparisonAsync(directTask, routes!, approaches!, cancellation.Token));
        startedAt = now;
        cancelInputArmedAt = now + 300;
        DecisionStatus = $"{tracker.AreaName}：正在比较直达与 {routes.Length} 条水晶路线到 {name} 的预计用时";
        Status = "正在比较直达、亚返回与步行到水晶的预计用时";
        eventNavigationTarget = target;
        return true;
    }

    private CustomNavigationRoute? SelectCustomRoute(uint eventId, CustomNavigationRouteKind kind, Vector3 player)
    {
        var sources = CrescentAetheryteCatalog.ForTerritory(tracker.TerritoryId);
        return EffectiveNavigationRoutes(tracker.TerritoryId, eventId, kind)
            .Where(route => route.Points.Count >= 2 &&
                            sources.Any(source => source.DataId == route.SourceAetheryteDataId))
            .OrderBy(route =>
            {
                var source = sources.First(item => item.DataId == route.SourceAetheryteDataId);
                var routeLength = RouteLength(route.Points);
                return HorizontalDistanceSquared(player, source.Position) <= CrystalCampRadius * CrystalCampRadius
                    ? routeLength
                    : routeLength + 300f;
            })
            .FirstOrDefault();
    }

    private static bool RouteMatches(
        CustomNavigationRoute route,
        uint territoryId,
        uint eventId,
        CustomNavigationRouteKind kind) =>
        route.TerritoryId == territoryId && route.EventId == eventId && route.Kind == kind;

    private static (uint TerritoryId, uint EventId, CustomNavigationRouteKind Kind) RouteKey(
        CustomNavigationRoute route) =>
        (route.TerritoryId, route.EventId, route.Kind);

    private bool BeginCustomEventRoute(CustomNavigationRoute route, Vector3 eventTarget,
        Vector3 navigationEndpoint, string name, Vector3 player, bool replaceRecordedEndpoint)
    {
        var source = CrescentAetheryteCatalog.ForTerritory(route.TerritoryId)
            .FirstOrDefault(item => item.DataId == route.SourceAetheryteDataId);
        if (source.DataId == 0) return false;
        var first = ToVector(route.Points[0]);
        if (HorizontalDistanceSquared(player, source.Position) <= 55f * 55f &&
            HorizontalDistanceSquared(player, first) <= 25f * 25f)
            return StartCustomRoute(route, eventTarget, navigationEndpoint, name, replaceRecordedEndpoint);

        var followUp = new NavigationFollowUp(eventTarget, navigationEndpoint, name, route,
            replaceRecordedEndpoint);
        var started = BeginAetheryteTravel(source, source.FallbackName, followUp);
        eventNavigationTarget = started ? eventTarget : null;
        if (started)
        {
            DecisionStatus = $"{tracker.AreaName}：使用自定义路线 {source.FallbackName} → {route.EventName}";
            Status = $"正在前往自定义路线起点：{source.FallbackName}";
        }
        return started;
    }

    private bool StartCustomRoute(CustomNavigationRoute route, Vector3 eventTarget,
        Vector3 navigationEndpoint, string name, bool replaceRecordedEndpoint)
    {
        if (tracker.PlayerPosition is not { } player || route.Points.Count < 2) return false;
        var recorded = route.Points.Select(ToVector).ToList();
        var closestStart = recorded
            .Take(Math.Min(recorded.Count, 12))
            .Select((point, index) => (point, index, distance: HorizontalDistanceSquared(player, point)))
            .OrderBy(item => item.distance)
            .First();
        if (closestStart.distance > 25f * 25f)
        {
            Status = $"传送落点未进入自定义路线起点范围：{route.EventName}";
            return false;
        }

        Cancel(null);
        var path = new List<Vector3> { player };
        path.AddRange(recorded.Skip(closestStart.index + (closestStart.distance < 1f ? 1 : 0)));
        if (replaceRecordedEndpoint && path.Count >= 2)
            path[^1] = navigationEndpoint;
        path = ApplyAggroAvoidance(path);
        if (path.Count < 2 || !vnavmesh.MoveAlong(path))
        {
            Status = $"无法启动自定义路线：{route.EventName}";
            return false;
        }
        active = true;
        activeCustomRouteJumps.Clear();
        activeCustomRouteJumps.AddRange(route.Points
            .Where(point => point.Action == CustomNavigationRoutePointAction.Jump)
            .Select(ToVector));
        activeCustomRoutePath.Clear();
        activeCustomRoutePath.AddRange(path);
        activeCustomRouteEventId = route.EventId;
        activeCustomRouteJumpIndex = 0;
        customRouteRestartAttempts = 0;
        customRouteJumpRequestedAt = 0;
        ownsMovingPath = true;
        observedBusy = false;
        destination = path[^1];
        requestedDestination = path[^1];
        eventNavigationTarget = eventTarget;
        targetName = name;
        displayPath = path;
        startedAt = Environment.TickCount64;
        cancelInputArmedAt = startedAt + 750;
        DecisionStatus = $"{tracker.AreaName}：正在使用录制的完整路线前往 {route.EventName}";
        Status = $"正在沿自定义路线前往：{route.EventName}";
        return true;
    }

    private static Vector3 ToVector(CustomNavigationRoutePoint point) => new(point.X, point.Y, point.Z);

    private void ClearActiveCustomRoute()
    {
        activeCustomRoutePath.Clear();
        activeCustomRouteEventId = 0;
        activeCustomRouteJumps.Clear();
        activeCustomRouteJumpIndex = 0;
        customRouteRestartAttempts = 0;
        customRouteJumpRequestedAt = 0;
    }

    private bool RestartRemainingCustomRoute(Vector3 player, string reason)
    {
        if (activeCustomRoutePath.Count < 2 || customRouteRestartAttempts >= 3) return false;

        var remaining = NavigationPathNormalizer.TrimPassedPrefix(player, activeCustomRoutePath);
        if (remaining.Count == 0) return false;
        while (activeCustomRouteJumpIndex < activeCustomRouteJumps.Count &&
               !remaining.Any(point => Vector3.DistanceSquared(
                   point, activeCustomRouteJumps[activeCustomRouteJumpIndex]) < 0.01f))
        {
            activeCustomRouteJumpIndex++;
            customRouteJumpRequestedAt = 0;
        }

        if (Vector3.DistanceSquared(player, remaining[0]) > 0.25f)
            remaining.Insert(0, player);
        if (remaining.Count < 2) return false;

        if (ownsMovingPath) vnavmesh.Stop();
        if (!vnavmesh.MoveAlong(remaining)) return false;

        customRouteRestartAttempts++;
        activeCustomRoutePath.Clear();
        activeCustomRoutePath.AddRange(remaining);
        displayPath = remaining;
        ownsMovingPath = true;
        observedBusy = false;
        navigationIdleSince = 0;
        startedAt = Environment.TickCount64;
        stuckSamplePosition = player;
        stuckSampleAt = startedAt;
        Status = $"{reason}，正从当前位置继续录制路线：{targetName}";
        return true;
    }

    private static float RouteLength(IReadOnlyList<CustomNavigationRoutePoint> points)
    {
        var result = 0f;
        for (var index = 1; index < points.Count; index++)
            result += Vector3.Distance(ToVector(points[index - 1]), ToVector(points[index]));
        return result;
    }

    private bool RememberEventTarget(bool started, Vector3 target)
    {
        eventNavigationTarget = started ? target : null;
        return started;
    }

    public bool NavigateToMapPosition(Vector2 mapPosition, string name, uint eventId = 0,
        CustomNavigationRouteKind? routeKind = null)
    {
        var map = dataManager.GetExcelSheet<GameMap>().GetRowOrDefault(tracker.MapId);
        if (map is not { } row || row.SizeFactor == 0)
        {
            Status = "无法读取当前区域地图坐标";
            return false;
        }
        var scale = row.SizeFactor / 100f;
        var texture = (mapPosition - Vector2.One) * scale / 40.96f * 2048f;
        var world = (texture - new Vector2(1024f)) / scale - new Vector2(row.OffsetX, row.OffsetY);
        return NavigateToEvent(new Vector3(world.X, 0f, world.Y), name, eventId, routeKind);
    }

    public bool TravelToAetheryte(CrescentAetheryte target, string name)
        => BeginAetheryteTravel(target, name, null);

    public bool TravelToNearestAetheryte(Vector3 destination, string destinationName)
    {
        var target = CrescentAetheryteCatalog.ForTerritory(tracker.TerritoryId)
            .OrderBy(item => HorizontalDistanceSquared(item.Position, destination))
            .FirstOrDefault();
        if (target.DataId == 0)
        {
            Status = "当前区域没有可用的传送水晶";
            return false;
        }
        var crystalName = CrescentAetheryteCatalog.Name(target, dataManager);
        return BeginAetheryteTravel(target, $"{crystalName}（靠近 {destinationName}）", null);
    }

    public bool ReturnToCamp(CrescentAetheryte camp, string name)
    {
        if (tracker.PlayerPosition is not { } player)
        {
            Status = "无法读取玩家位置";
            return false;
        }
        var crystals = CrescentAetheryteCatalog.ForTerritory(tracker.TerritoryId);
        return IsInsideCrystalCamp(player, crystals)
            ? BeginAetheryteTravel(camp, name, null)
            : BeginAetheryteTravel(camp, name, null, completeAfterDemiReturn: true);
    }

    private bool BeginAetheryteTravel(CrescentAetheryte target, string name, NavigationFollowUp? followUp,
        bool allowDemiReturn = true, CrescentAetheryte? preferredSource = null,
        bool completeAfterDemiReturn = false)
    {
        if (pendingAetheryte is { } pending && pending.DataId == target.DataId)
        {
            Cancel("已取消水晶传送");
            return false;
        }
        var logicalEventTarget = followUp?.Position;
        Cancel(null);
        eventNavigationTarget = logicalEventTarget;
        if (!tracker.IsSupportedTerritory)
        {
            Status = "水晶传送仅在新月岛南部或北部可用";
            return false;
        }
        if (condition[ConditionFlag.InCombat])
        {
            Status = "战斗中无法使用传送水晶";
            return false;
        }
        pendingFollowUp = followUp;
        completeAtCampAfterDemiReturn = completeAfterDemiReturn;
        if (!completeAfterDemiReturn && !IsMountedOrMounting() && TryTeleport(target, name)) return true;
        if (tracker.PlayerPosition is not { } player)
        {
            Status = "无法读取玩家位置";
            return false;
        }

        var currentSources = CrescentAetheryteCatalog.ForTerritory(
                target.DataId is >= 5571 and <= 5576
                    ? Core.PotCandidateCatalog.NorthHornTerritoryId
                    : Core.PotCandidateCatalog.SouthHornTerritoryId);
        var source = completeAfterDemiReturn
            ? target
            : preferredSource is { } preferred && currentSources.Contains(preferred)
            ? preferred
            : currentSources.OrderBy(item => HorizontalDistanceSquared(item.Position, player)).FirstOrDefault();
        if (source.DataId == 0)
        {
            Status = "未找到当前区域的传送水晶";
            return false;
        }

        pendingAetheryte = target;
        sourceAetheryte = source;
        pendingAetheryteName = name;
        teleportDeadline = Environment.TickCount64 + 45_000;
        nextInteractionAt = 0;
        nextCrystalNavigationAt = 0;
        cancelInputArmedAt = Environment.TickCount64 + 300;
        if (HorizontalDistanceSquared(player, source.Position) <= 5f * 5f)
        {
            Status = $"正在与附近水晶交互并传送至：{name}";
            return true;
        }
        if (!allowDemiReturn || IsInsideCrystalCamp(player, currentSources))
        {
            demiReturnPending = false;
            Status = $"正在前往水晶，随后传送至：{name}";
            StartNavigationToSource(player);
            return true;
        }

        demiReturnPending = true;
        demiReturnAccepted = false;
        demiReturnOrigin = player;
        demiReturnStartedAt = Environment.TickCount64;
        nextDemiReturnAt = 0;
        Status = $"正在准备亚返回，随后传送至：{name}";
        return true;
    }

    public bool NavigateTo(
        Vector3 target,
        string name,
        bool allowMount = true,
        bool allowLargeHeightCorrection = false)
    {
        if (active)
        {
            if (Vector3.DistanceSquared(requestedDestination, target) < 1f)
            {
                Cancel("已取消自动导航");
                return false;
            }
            Cancel(null);
        }
        ClearActiveCustomRoute();
        if (!tracker.IsSupportedTerritory)
        {
            Status = "自动导航仅在新月岛南部或北部可用";
            return false;
        }
        if (!vnavmesh.IsAvailable())
        {
            Status = "vnavmesh 未启用、导航网格尚未就绪或 IPC 不兼容";
            return false;
        }
        if (tracker.PlayerPosition is not { } player)
        {
            Status = "无法读取玩家位置";
            return false;
        }
        var longRoute = HorizontalDistanceSquared(player, target) >
                        MountMinimumPathDistance * MountMinimumPathDistance;
        if (longRoute && allowMount) TryRequestMount();
        // Pot candidates come from a two-dimensional map. Resolve them with the same downward
        // floor probe used by their scene markers so navigation cannot silently choose a lower
        // floor at the same X/Z coordinate.
        var reachableTarget = allowLargeHeightCorrection
            ? ResolveTreasureCandidateFloor(target, player)
            : vnavmesh.QueryNearestReachable(target, 12f, 80f);
        if (reachableTarget is not { } resolvedTarget)
        {
            Status = $"目标附近没有可达导航网格：{name}";
            return false;
        }
        pathCancellation = new CancellationTokenSource();
        pathRequestPlayerPosition = player;
        pathOrigin = ResolvePathOrigin(player);
        pathNavigationPoint = resolvedTarget;
        pathOriginReplans = 0;
        pathTask = ObservePathTask(vnavmesh.Pathfind(pathOrigin, pathNavigationPoint, pathCancellation.Token));
        if (pathTask == null)
        {
            pathCancellation.Dispose();
            pathCancellation = null;
            if (!vnavmesh.NavigateTo(resolvedTarget))
            {
                Status = $"无法向 vnavmesh 提交目标：{name}";
                return false;
            }
            ownsMovingPath = true;
        }
        requestedDestination = target;
        destination = resolvedTarget;
        targetName = name;
        startedAt = Environment.TickCount64;
        observedBusy = false;
        navigationIdleSince = 0;
        active = true;
        cancelInputArmedAt = Environment.TickCount64 + 300;
        Status = pathTask == null
            ? $"正在自动导航至：{name}；再次点击或按 WASD／Esc 取消"
            : longRoute && !condition[ConditionFlag.Mounted]
                ? $"正在上坐骑并规划前往：{name}"
                : $"正在规划前往：{name}";
        return true;
    }

    private Vector3? ResolveTreasureCandidateFloor(Vector3 target, Vector3 player)
    {
        var probe = new Vector3(target.X, player.Y + 50f, target.Z);
        return vnavmesh.QueryPointOnFloor(probe, 3f) ??
               vnavmesh.QueryNearestReachable(probe, 3f, 30f);
    }

    private Vector3 ResolvePathOrigin(Vector3 player)
    {
        var probe = player + new Vector3(0f, 5f, 0f);
        return vnavmesh.QueryNearestReachable(player, 2f, 3f) ??
               vnavmesh.QueryPointOnFloor(probe, 4f) ??
               vnavmesh.QueryNearestReachable(player, 4f, 20f) ??
               player;
    }

    public void Cancel(string? message = "已取消自动导航")
    {
        var hadTravel = IsNavigating || pathTask != null;
        pathCancellation?.Cancel();
        ObservePathTask(pathTask);
        pathCancellation?.Dispose();
        pathCancellation = null;
        pathTask = null;
        pathOrigin = default;
        pathRequestPlayerPosition = default;
        pathNavigationPoint = default;
        pathOriginReplans = 0;
        routePlan?.Cancellation.Cancel();
        routePlan?.Cancellation.Dispose();
        routePlan = null;
        if (hadTravel) vnavmesh.Stop();
        if (hadTravel) commandManager.ProcessCommand("/automove off");
        active = false;
        activeCustomRouteJumps.Clear();
        activeCustomRoutePath.Clear();
        activeCustomRouteEventId = 0;
        activeCustomRouteJumpIndex = 0;
        customRouteRestartAttempts = 0;
        customRouteJumpRequestedAt = 0;
        eventNavigationTarget = null;
        displayPath = [];
        ownsMovingPath = false;
        observedBusy = false;
        navigationIdleSince = 0;
        ClearPendingTeleport();
        pendingFollowUp = null;
        arrivalFollowUp = null;
        arrivalAetheryte = null;
        ResetStuckRecovery();
        if (message != null) Status = message;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        Cancel(null);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        UpdateCustomRouteRecording();
        CompleteObservedActionTimings();
        var territory = tracker.TerritoryId;
        if (recoveryTerritory != territory)
        {
            recoveryTerritory = territory;
            recoveryMemory.Clear();
            ResetStuckRecovery();
        }
        if (IsNavigating && configuration.InterruptNavigationOnMovementInput &&
            Environment.TickCount64 >= cancelInputArmedAt && IsCancelInputPressed())
        {
            Cancel("检测到手动移动，自动导航已取消");
            return;
        }
        if (UpdateRoutePlan()) return;
        if (UpdateArrivalFollowUp()) return;
        if (pendingAetheryte is { } pending && UpdatePendingTeleport(pending)) return;
        if (!active) return;
        if (!tracker.IsSupportedTerritory)
        {
            Cancel("已离开新月岛，自动导航已取消");
            return;
        }
        if (tracker.PlayerPosition is { } jumpPlayer)
            UpdateCustomRouteJump(jumpPlayer, Environment.TickCount64);
        var navigationArrivalRadius = pendingAetheryte != null
            ? CrystalNavigationArrivalRadius
            : 3f;
        if (tracker.PlayerPosition is { } player &&
            HorizontalDistanceSquared(player, destination) <= navigationArrivalRadius * navigationArrivalRadius)
        {
            if (recoveryDetourActive)
            {
                CompleteRecoveryDetour(player);
                return;
            }
            if (pendingAetheryte != null)
            {
                if (ownsMovingPath) vnavmesh.Stop();
                active = false;
                ownsMovingPath = false;
                observedBusy = false;
                displayPath = [];
                Status = $"已到达水晶导航点，正在确认实际交互位置：{pendingAetheryteName}";
                return;
            }
            Cancel(null);
            Status = $"已到达：{targetName}";
            return;
        }
        if (pathTask is { IsCompleted: true } completedPath)
        {
            if (completedPath.IsCompletedSuccessfully && completedPath.Result.Count > 0 &&
                tracker.PlayerPosition is { } currentPlayer &&
                HorizontalDistanceSquared(currentPlayer, pathRequestPlayerPosition) >
                PathOriginReplanDistance * PathOriginReplanDistance &&
                pathOriginReplans < MaxPathOriginReplans)
            {
                var replacementCancellation = new CancellationTokenSource();
                var replacementOrigin = ResolvePathOrigin(currentPlayer);
                var replacementTask = ObservePathTask(vnavmesh.Pathfind(
                    replacementOrigin,
                    pathNavigationPoint,
                    replacementCancellation.Token));
                if (replacementTask != null)
                {
                    pathCancellation?.Dispose();
                    pathCancellation = replacementCancellation;
                    pathTask = replacementTask;
                    pathRequestPlayerPosition = currentPlayer;
                    pathOrigin = replacementOrigin;
                    pathOriginReplans++;
                    startedAt = Environment.TickCount64;
                    Status = $"位置已变化，正在从当前位置重新规划：{targetName}";
                    return;
                }
                replacementCancellation.Dispose();
            }
            pathTask = null;
            pathCancellation?.Dispose();
            pathCancellation = null;
            if (!completedPath.IsCompletedSuccessfully || completedPath.Result.Count == 0)
            {
                if (vnavmesh.NavigateTo(destination))
                {
                    ownsMovingPath = true;
                    observedBusy = false;
                    startedAt = Environment.TickCount64;
                    Status = $"详细路径不可用，已切换普通导航：{targetName}";
                    return;
                }
                active = false;
                Status = $"没有找到前往目标的路线：{targetName}";
                return;
            }
            var route = ApplyAggroAvoidance(completedPath.Result);
            if (tracker.PlayerPosition is { } livePlayer)
                route = NavigationPathNormalizer.TrimPassedPrefix(livePlayer, route);
            if (!recoveryDetourActive) route = ApplyLearnedRecoveryPoints(route);
            if (route.Count == 0 || !vnavmesh.MoveAlong(route) && !vnavmesh.NavigateTo(route[^1]))
            {
                active = false;
                Status = $"无法启动前往目标的路径：{targetName}";
                return;
            }
            ownsMovingPath = true;
            displayPath = route;
            observedBusy = false;
            startedAt = Environment.TickCount64;
            Status = $"正在自动导航至：{targetName}；再次点击或按 WASD／Esc 取消";
            return;
        }
        if (pathTask != null)
        {
            if (tracker.PlayerPosition is { } planningPlayer &&
                HorizontalDistanceSquared(planningPlayer, destination) >
                MountMinimumPathDistance * MountMinimumPathDistance)
            {
                TryRequestMount();
                Status = $"正在上坐骑并规划路线：{targetName}";
            }
            if (Environment.TickCount64 - startedAt <= PathPlanningFallbackMilliseconds) return;
            pathCancellation?.Cancel();
            ObservePathTask(pathTask);
            pathCancellation?.Dispose();
            pathCancellation = null;
            pathTask = null;
            vnavmesh.CancelPathfinds();
            if (vnavmesh.NavigateTo(destination))
            {
                ownsMovingPath = true;
                observedBusy = false;
                startedAt = Environment.TickCount64;
                Status = $"详细路径规划超时，已切换普通导航：{targetName}";
                return;
            }
            active = false;
            ownsMovingPath = false;
            Status = $"路径规划超时且普通导航启动失败：{targetName}";
            return;
        }
        var busy = vnavmesh.IsBusy();
        if (busy && tracker.PlayerPosition is { } movingPlayer &&
            !IsNearPendingAetheryte(movingPlayer) &&
            UpdateStuckRecovery(movingPlayer, Environment.TickCount64))
            return;
        if (busy) TryMountForLongNavigation();
        if (busy && Environment.TickCount64 >= nextAggroPathCheck)
        {
            nextAggroPathCheck = Environment.TickCount64 + 1_250;
            var currentPath = vnavmesh.GetWaypoints();
            if (currentPath.Count > 1) displayPath = currentPath;
            var safePath = ApplyAggroAvoidance(currentPath);
            if (safePath.Count > 1 && PathChanged(currentPath, safePath))
            {
                if (!vnavmesh.MoveAlong(safePath))
                {
                    Cancel("目标路线出现新警戒目标，重新规划失败");
                    return;
                }
                Status = $"检测到新的怪物警戒范围，已调整路线：{targetName}";
                displayPath = safePath;
            }
        }
        if (busy) navigationIdleSince = 0;
        observedBusy |= busy;
        if (observedBusy && !busy)
        {
            var now = Environment.TickCount64;
            if (navigationIdleSince == 0)
            {
                navigationIdleSince = now;
                return;
            }
            if (now - navigationIdleSince < NavigationIdleConfirmationMilliseconds) return;
            if (recoveryDetourActive && tracker.PlayerPosition is { } stoppedPlayer)
            {
                FailRecoveryDetour(stoppedPlayer);
                return;
            }
            if (tracker.PlayerPosition is { } customRoutePlayer &&
                RestartRemainingCustomRoute(customRoutePlayer, "vnavmesh 短暂停止"))
                return;
            active = false;
            ownsMovingPath = false;
            navigationIdleSince = 0;
            Status = $"导航已结束：{targetName}";
        }
        else if (!observedBusy && Environment.TickCount64 - startedAt > 10_000)
        {
            if (tracker.PlayerPosition is { } customRoutePlayer &&
                RestartRemainingCustomRoute(customRoutePlayer, "vnavmesh 未开始移动"))
                return;
            active = false;
            ownsMovingPath = false;
            Status = $"vnavmesh 未开始移动：{targetName}";
        }
    }

    private void UpdateCustomRouteRecording()
    {
        if (!IsRecordingCustomRoute) return;
        if (tracker.TerritoryId != recordedRouteTerritory)
        {
            ClearRouteRecording();
            RouteRecordingStatus = "区域发生变化，路线录制已取消";
            return;
        }
        if (tracker.PlayerPosition is not { } player) return;
        var now = Environment.TickCount64;
        if (now < nextRouteRecordSample) return;
        nextRouteRecordSample = now + 100;
        var jumping = condition[ConditionFlag.Jumping];
        if (jumping && !recordingWasJumping && recordedRouteSamples.Count > 0)
            recordedRouteSamples[^1] = recordedRouteSamples[^1] with { Jump = true };
        if (AddRecordedRoutePoint(player, jumping ? 0.75f : 1.5f, jumping))
            displayPath = recordedRouteSamples.Select(item => item.Position).ToArray();
        recordingWasJumping = jumping;
        if (recordedRouteDestination is not { } destination)
        {
            RouteRecordingStatus = $"正在录制 {recordedRouteEventName} · 已记录 {recordedRouteSamples.Count} 点 · 终点未知";
            return;
        }

        var distance = HorizontalDistance(player, destination);
        var jumpCount = recordedRouteSamples.Count(item => item.Jump);
        RouteRecordingStatus = $"正在录制 {recordedRouteEventName} · 已记录 {recordedRouteSamples.Count} 点、{jumpCount} 次跳跃 · 距终点 {distance:F0}m";
        if (distance > RouteRecordingArrivalRadius)
        {
            recordedRouteArrivalSince = 0;
            return;
        }
        if (recordedRouteArrivalSince == 0)
        {
            recordedRouteArrivalSince = now;
            RouteRecordingStatus = $"已到达 {recordedRouteEventName} 终点，正在确认…";
            return;
        }
        if (now - recordedRouteArrivalSince < RouteRecordingArrivalConfirmationMilliseconds) return;
        FinishCustomRouteRecording();
    }

    private bool AddRecordedRoutePoint(Vector3 point, float minimumDistance, bool airborne)
    {
        if (recordedRouteSamples.Count > 0 &&
            Vector3.DistanceSquared(recordedRouteSamples[^1].Position, point) < minimumDistance * minimumDistance)
            return false;
        recordedRouteSamples.Add(new RecordedRouteSample(point, false, airborne));
        return true;
    }

    private List<CustomNavigationRoutePoint> BuildRecordedRoutePoints()
    {
        var result = new List<CustomNavigationRoutePoint>(recordedRouteSamples.Count);
        foreach (var sample in recordedRouteSamples)
        {
            // vnavmesh cannot replay airborne XYZ samples. The takeoff point carries the action and
            // the first grounded sample after it becomes the landing target.
            if (sample.Airborne) continue;
            result.Add(new CustomNavigationRoutePoint
            {
                X = sample.Position.X,
                Y = sample.Position.Y,
                Z = sample.Position.Z,
                Action = sample.Jump ? CustomNavigationRoutePointAction.Jump : CustomNavigationRoutePointAction.None
            });
        }
        return result;
    }

    private void ClearRouteRecording()
    {
        recordedRouteSamples.Clear();
        recordedRouteTerritory = 0;
        recordedRouteSourceAetheryte = 0;
        recordedRouteEventId = 0;
        recordedRouteEventName = string.Empty;
        recordedRouteDestination = null;
        recordedRouteArrivalSince = 0;
        nextRouteRecordSample = 0;
        recordingWasJumping = false;
        displayPath = [];
    }

    private void TryMountForLongNavigation()
    {
        var waypoints = vnavmesh.GetWaypoints();
        if (tracker.PlayerPosition is not { } player || PathDistance(player, waypoints) <= MountMinimumPathDistance)
            return;
        TryRequestMount();
    }

    private void UpdateCustomRouteJump(Vector3 player, long now)
    {
        if (activeCustomRouteJumpIndex >= activeCustomRouteJumps.Count) return;
        var jumpPoint = activeCustomRouteJumps[activeCustomRouteJumpIndex];

        if (customRouteJumpRequestedAt != 0 && condition[ConditionFlag.Jumping])
        {
            activeCustomRouteJumpIndex++;
            customRouteJumpRequestedAt = 0;
            Status = $"已执行路线跳跃 {activeCustomRouteJumpIndex}/{activeCustomRouteJumps.Count}：{targetName}";
            return;
        }

        if (HorizontalDistanceSquared(player, jumpPoint) >
            CustomRouteJumpTriggerRadius * CustomRouteJumpTriggerRadius)
            return;
        if (customRouteJumpRequestedAt != 0 && now - customRouteJumpRequestedAt < CustomRouteJumpRetryMilliseconds)
            return;

        var actionManager = ActionManager.Instance();
        if (actionManager == null || actionManager->GetActionStatus(ActionType.GeneralAction, 2) != 0) return;
        actionManager->UseAction(ActionType.GeneralAction, 2);
        customRouteJumpRequestedAt = now;
        Status = $"正在执行路线跳跃 {activeCustomRouteJumpIndex + 1}/{activeCustomRouteJumps.Count}：{targetName}";
    }

    private bool UpdateStuckRecovery(Vector3 player, long now)
    {
        if (condition[ConditionFlag.Casting] || condition[ConditionFlag.Mounting] ||
            condition[ConditionFlag.Jumping] || condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51])
        {
            stuckSamplePosition = player;
            stuckSampleAt = now;
            return false;
        }

        if (stuckRecoveryStage == StuckRecoveryStage.ObservingJump)
        {
            if (now - jumpRecoveryAt < StuckSampleMilliseconds) return false;
            if (HorizontalDistanceSquared(player, stuckOrigin) >=
                StuckMovementDistance * StuckMovementDistance)
            {
                ResetStuckMonitor(player, now);
                Status = $"跳跃脱困成功，继续前往：{targetName}";
                return false;
            }

            if (RestartRemainingCustomRoute(player, "跳跃脱困后续跑"))
            {
                stuckRecoveryStage = StuckRecoveryStage.Replanned;
                return true;
            }

            var replanTarget = destination;
            var replanName = targetName;
            if (!NavigateToPreservingTeleport(replanTarget, replanName))
            {
                stuckRecoveryStage = StuckRecoveryStage.Replanned;
                return StartRecoveryDetour(player, replanTarget, replanName);
            }
            stuckRecoveryStage = StuckRecoveryStage.Replanned;
            stuckSamplePosition = player;
            stuckSampleAt = now;
            Status = $"跳跃未脱困，正在从当前位置重新规划：{replanName}";
            return true;
        }

        if (stuckSampleAt == 0)
        {
            stuckSamplePosition = player;
            stuckSampleAt = now;
            return false;
        }
        if (now - stuckSampleAt < StuckSampleMilliseconds) return false;

        var moved = HorizontalDistanceSquared(player, stuckSamplePosition);
        stuckSamplePosition = player;
        stuckSampleAt = now;
        if (moved >= StuckMovementDistance * StuckMovementDistance)
        {
            customRouteRestartAttempts = 0;
            if (stuckRecoveryStage == StuckRecoveryStage.Replanned &&
                HorizontalDistanceSquared(player, stuckOrigin) >=
                StuckRecoveryClearDistance * StuckRecoveryClearDistance)
                stuckRecoveryStage = StuckRecoveryStage.Monitoring;
            return false;
        }

        if (stuckRecoveryStage == StuckRecoveryStage.Replanned)
            return recoveryDetourActive
                ? FailRecoveryDetour(player)
                : StartRecoveryDetour(player, destination, targetName);

        stuckOrigin = player;
        jumpRecoveryAt = now;
        stuckRecoveryStage = StuckRecoveryStage.ObservingJump;
        var actionManager = ActionManager.Instance();
        if (actionManager != null && actionManager->GetActionStatus(ActionType.GeneralAction, 2) == 0)
            actionManager->UseAction(ActionType.GeneralAction, 2);
        Status = $"检测到移动停滞，正在跳跃脱困：{targetName}";
        return true;
    }

    private bool StartRecoveryDetour(Vector3 player, Vector3 finalDestination, string finalName)
    {
        var cell = RecoveryCell.From(stuckOrigin == default ? player : stuckOrigin);
        recoveryMemory.TryGetValue(cell, out var memory);
        if (memory.SuccessfulWaypoint is { } remembered &&
            vnavmesh.QueryNearestReachable(remembered, 2f, 10f) is { } rememberedReachable)
            return BeginRecoveryDetour(rememberedReachable, finalDestination, finalName, cell, 0,
                "正在使用已记录的绕行点");

        var direction = NormalizeHorizontal(finalDestination - player);
        if (direction == Vector3.Zero) direction = Vector3.UnitZ;
        var left = new Vector3(-direction.Z, 0f, direction.X);
        var failedSides = memory.FailedSides == 3 ? 0 : memory.FailedSides;
        (Vector3 Point, int Side, float Score)? best = null;
        foreach (var side in new[] { -1, 1 })
        {
            var bit = side < 0 ? 1 : 2;
            if ((failedSides & bit) != 0) continue;
            foreach (var distance in new[] { 3f, 4.5f, 6f })
            {
                var probe = player + left * (side * distance) + direction * 1.5f;
                if (vnavmesh.QueryNearestReachable(probe, 2f, 10f) is not { } point ||
                    HorizontalDistanceSquared(player, point) < 2f * 2f)
                    continue;
                var safe = ApplyAggroAvoidance([player, point]);
                if (safe.Count == 0) continue;
                var score = HorizontalDistanceSquared(player, point) +
                            HorizontalDistanceSquared(point, finalDestination);
                if (best != null && score >= best.Value.Score) continue;
                best = (point, side, score);
            }
        }

        if (best is not { } selected)
        {
            stuckRecoveryStage = StuckRecoveryStage.Monitoring;
            Status = $"附近暂未找到侧向脱困点，将继续检测：{finalName}";
            return false;
        }
        return BeginRecoveryDetour(selected.Point, finalDestination, finalName, cell, selected.Side,
            selected.Side < 0 ? "正在向右侧绕开障碍" : "正在向左侧绕开障碍");
    }

    private bool BeginRecoveryDetour(Vector3 waypoint, Vector3 finalDestination, string finalName,
        RecoveryCell cell, int side, string status)
    {
        if (!NavigateToPreservingTeleport(waypoint, "脱困绕行点"))
        {
            stuckRecoveryStage = StuckRecoveryStage.Replanned;
            NavigateToPreservingTeleport(finalDestination, finalName);
            Status = $"侧向点暂时不可达，已继续重规划：{finalName}";
            return true;
        }
        recoveryDetourActive = true;
        recoveryFinalDestination = finalDestination;
        recoveryFinalName = finalName;
        recoveryCell = cell;
        recoverySide = side;
        stuckRecoveryStage = StuckRecoveryStage.Replanned;
        stuckSampleAt = 0;
        Status = $"{status}，随后继续前往：{finalName}";
        return true;
    }

    private void CompleteRecoveryDetour(Vector3 player)
    {
        recoveryMemory.TryGetValue(recoveryCell, out var memory);
        recoveryMemory[recoveryCell] = memory with
        {
            Origin = stuckOrigin,
            SuccessfulWaypoint = destination
        };
        var finalDestination = recoveryFinalDestination;
        var finalName = recoveryFinalName;
        recoveryDetourActive = false;
        recoverySide = 0;
        stuckRecoveryStage = StuckRecoveryStage.Monitoring;
        stuckSamplePosition = player;
        stuckSampleAt = Environment.TickCount64;
        if (!RestartRemainingCustomRoute(player, "绕开障碍后续跑"))
            NavigateToPreservingTeleport(finalDestination, finalName);
        Status = $"已绕开障碍，继续前往：{finalName}";
    }

    private bool FailRecoveryDetour(Vector3 player)
    {
        recoveryMemory.TryGetValue(recoveryCell, out var memory);
        var failedBit = recoverySide < 0 ? 1 : recoverySide > 0 ? 2 : 0;
        recoveryMemory[recoveryCell] = memory with
        {
            Origin = stuckOrigin,
            SuccessfulWaypoint = null,
            FailedSides = memory.FailedSides | failedBit
        };
        var finalDestination = recoveryFinalDestination;
        var finalName = recoveryFinalName;
        recoveryDetourActive = false;
        recoverySide = 0;
        stuckOrigin = player;
        return StartRecoveryDetour(player, finalDestination, finalName);
    }

    private List<Vector3> ApplyLearnedRecoveryPoints(List<Vector3> route)
    {
        if (route.Count < 2 || recoveryMemory.Count == 0) return route;
        foreach (var memory in recoveryMemory.Values.Where(item => item.SuccessfulWaypoint != null))
        {
            for (var index = 0; index + 1 < route.Count; index++)
            {
                if (DistanceToSegmentHorizontalSquared(memory.Origin, route[index], route[index + 1]) > 5f * 5f)
                    continue;
                route.Insert(index + 1, memory.SuccessfulWaypoint!.Value);
                break;
            }
        }
        return route;
    }

    private void ResetStuckMonitor(Vector3 player, long now)
    {
        stuckRecoveryStage = StuckRecoveryStage.Monitoring;
        stuckSamplePosition = player;
        stuckSampleAt = now;
        jumpRecoveryAt = 0;
    }

    private void ResetStuckRecovery()
    {
        stuckRecoveryStage = StuckRecoveryStage.Monitoring;
        stuckSamplePosition = default;
        stuckSampleAt = 0;
        stuckOrigin = default;
        jumpRecoveryAt = 0;
        recoveryDetourActive = false;
        recoveryFinalDestination = default;
        recoveryFinalName = string.Empty;
        recoveryCell = default;
        recoverySide = 0;
    }

    private static Vector3 NormalizeHorizontal(Vector3 value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Z * value.Z);
        return length <= float.Epsilon ? Vector3.Zero : new(value.X / length, 0f, value.Z / length);
    }

    private static float DistanceToSegmentHorizontalSquared(Vector3 point, Vector3 start, Vector3 end)
    {
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var lengthSquared = dx * dx + dz * dz;
        if (lengthSquared <= float.Epsilon) return HorizontalDistanceSquared(point, start);
        var progress = Math.Clamp(((point.X - start.X) * dx + (point.Z - start.Z) * dz) / lengthSquared, 0f, 1f);
        var x = point.X - (start.X + dx * progress);
        var z = point.Z - (start.Z + dz * progress);
        return x * x + z * z;
    }

    private static float PathDistance(Vector3 origin, IReadOnlyList<Vector3> path)
    {
        if (path.Count == 0) return 0f;
        var result = Vector3.Distance(origin, path[0]);
        for (var index = 1; index < path.Count; index++)
            result += Vector3.Distance(path[index - 1], path[index]);
        return result;
    }

    private List<Vector3> ApplyAggroAvoidance(IReadOnlyList<Vector3> path)
    {
        if (!configuration.AvoidMonsterAggroRanges || path.Count < 2 ||
            objectTable.LocalPlayer is not { } player)
            return path.ToList();
        var zones = new List<NorthHornAggroZone>();
        var playerBattleChara = (BattleChara*)(void*)player.Address;
        if (playerBattleChara == null) return path.ToList();
        var playerLevel = playerBattleChara->ForayInfo.Level;
        var manager = CharacterManager.Instance();
        if (manager == null) return path.ToList();
        for (var index = 0; index < 100; index++)
        {
            var battleChara = manager->BattleCharas[index].Value;
            if (battleChara == null || battleChara == playerBattleChara || battleChara->NameId == 0 ||
                battleChara->IsDead() || battleChara->Health == 0 || !battleChara->GetIsTargetable() ||
                HorizontalDistanceSquared(player.Position, battleChara->Position) >
                configuration.AggroScanRange * configuration.AggroScanRange ||
                !OccultCrescentMonsterCatalog.TryGet(tracker.TerritoryId,
                    battleChara->NameId, out var profile))
                continue;
            var mobLevel = battleChara->ForayInfo.Level;
            if (battleChara->FateId != 0 || playerLevel != 0 && mobLevel != 0 && mobLevel < playerLevel) continue;
            var edgeRange = configuration.AggroEdgeRange;
            var radius = Math.Max(0.5f,
                edgeRange + Math.Max(0f, battleChara->HitboxRadius) + configuration.AggroSafetyMargin);
            zones.Add(new NorthHornAggroZone(
                battleChara->EntityId,
                battleChara->NameId,
                battleChara->Position,
                radius,
                profile.SenseType,
                battleChara->Rotation,
                edgeRange,
                0,
                AggroRangeSource.Fallback,
                OccultCrescentMonsterCatalog.DefaultHalfAngleDegrees(profile.SenseType)));
        }
        if (zones.Count == 0) return path.ToList();
        return NorthHornAggroAvoidance.TryCreateSafePath(
            path,
            zones,
            configuration.AggroVerticalTolerance,
            point => vnavmesh.QueryNearestReachable(point, 3f, 10f),
            out var safePath)
            ? safePath
            : [];
    }

    private static bool PathChanged(IReadOnlyList<Vector3> original, IReadOnlyList<Vector3> adjusted)
    {
        if (original.Count != adjusted.Count) return true;
        for (var index = 0; index < original.Count; index++)
            if (Vector3.DistanceSquared(original[index], adjusted[index]) > 0.01f)
                return true;
        return false;
    }

    private bool UpdateRoutePlan()
    {
        var pending = routePlan;
        if (pending == null) return false;
        if (!tracker.IsSupportedTerritory || condition[ConditionFlag.InCombat])
        {
            Cancel("进入战斗或离开新月岛，已取消路线比较");
            return true;
        }
        var now = Environment.TickCount64;
        if (!pending.Task.IsCompleted)
        {
            if (now - pending.StartedAt <= 15_000) return true;
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
            vnavmesh.CancelPathfinds();
            routePlan = null;
            return FallBackFromRouteComparison(
                pending.FollowUp.Position, pending.FollowUp.NavigationPoint,
                pending.FollowUp.Name, pending.DirectOrigin, "路线比较超时");
        }

        RouteComparisonPaths? comparison = pending.Task.IsCompletedSuccessfully ? pending.Task.Result : null;
        pending.Cancellation.Cancel();
        pending.Cancellation.Dispose();
        vnavmesh.CancelPathfinds();
        routePlan = null;
        if (comparison == null)
            return FallBackFromRouteComparison(
                pending.FollowUp.Position, pending.FollowUp.NavigationPoint,
                pending.FollowUp.Name, pending.DirectOrigin, "没有可用的路线比较结果");

        var directDistance = comparison.DirectPath is { Count: > 0 } directPath
            ? CalculateSafePathDistance(pending.DirectOrigin, pending.DestinationPoint, directPath)
            : float.PositiveInfinity;
        CrescentAetheryte? selected = null;
        var destinationDistance = float.PositiveInfinity;
        var destinationSeconds = float.PositiveInfinity;
        foreach (var item in comparison.AetherytePaths)
        {
            var distance = CalculateSafePathDistance(item.Origin, pending.DestinationPoint, item.Path);
            var seconds = RouteTimeEstimator.MovementSeconds(
                distance, false, configuration.AverageMountSeconds);
            if (seconds >= destinationSeconds) continue;
            selected = item.Aetheryte;
            destinationDistance = distance;
            destinationSeconds = seconds;
        }

        var approachDistance = float.PositiveInfinity;
        CrescentAetheryte? approachSource = null;
        foreach (var item in comparison.ApproachPaths)
        {
            var distance = CalculateSafePathDistance(item.Origin, item.Aetheryte.Position, item.Path);
            if (distance >= approachDistance) continue;
            approachDistance = distance;
            approachSource = item.Aetheryte;
        }
        var directSeconds = RouteTimeEstimator.MovementSeconds(
            directDistance, pending.InitiallyMounted, configuration.AverageMountSeconds);
        var transfer = RouteTimeEstimator.TransferSeconds(
            destinationDistance,
            approachDistance,
            pending.InitiallyMounted,
            pending.NearCrystal,
            pending.DemiReturnAvailable,
            configuration.AverageDemiReturnSeconds,
            configuration.AverageCrystalTransferSeconds,
            configuration.AverageMountSeconds,
            configuration.AverageDismountSeconds);

        if (selected is not { } aetheryte || !float.IsFinite(transfer.Seconds))
        {
            if (float.IsFinite(directSeconds))
            {
                DecisionStatus = $"{tracker.AreaName}：只有直达路线可用，预计 {FormatSeconds(directSeconds)}";
                return NavigateTo(pending.FollowUp.NavigationPoint, pending.FollowUp.Name);
            }
            return FallBackFromRouteComparison(
                pending.FollowUp.Position, pending.FollowUp.NavigationPoint,
                pending.FollowUp.Name, pending.DirectOrigin, "实际路线均不可用");
        }

        var transferKind = transfer.AccessMode switch
        {
            CrystalAccessMode.Nearby => "水晶传送",
            CrystalAccessMode.DemiReturn => "亚返回/传送",
            _ => "前往水晶/传送"
        };
        if (RouteTimeEstimator.PreferTransfer(
                directSeconds, transfer.Seconds, configuration.MinimumTeleportSavingSeconds))
        {
            DecisionStatus = $"{tracker.AreaName}：直达 {FormatSeconds(directSeconds)} · {transferKind} {FormatSeconds(transfer.Seconds)} · 选择 {aetheryte.FallbackName}";
            BeginAetheryteTravel(
                aetheryte,
                aetheryte.FallbackName,
                pending.FollowUp,
                transfer.AccessMode == CrystalAccessMode.DemiReturn,
                transfer.AccessMode == CrystalAccessMode.Walk ? approachSource : null);
            return true;
        }

        DecisionStatus = $"{tracker.AreaName}：直达 {FormatSeconds(directSeconds)} · {transferKind} {FormatSeconds(transfer.Seconds)} · 节省不足 {configuration.MinimumTeleportSavingSeconds:F0}秒，选择直达";
        return NavigateTo(pending.FollowUp.NavigationPoint, pending.FollowUp.Name);
    }

    private bool UpdateArrivalFollowUp()
    {
        if (arrivalFollowUp is not { } followUp || arrivalAetheryte is not { } aetheryte) return false;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            tracker.PlayerPosition is not { } player) return true;
        var now = Environment.TickCount64;
        var moved = HorizontalDistanceSquared(player, arrivalOrigin) >= 20f * 20f;
        if (!moved && now - arrivalArmedAt < 1_000) return true;
        if (HorizontalDistanceSquared(player, aetheryte.Position) > 45f * 45f) return true;

        ObserveTiming(RouteTimingKind.CrystalTransfer, (now - arrivalArmedAt) / 1000f, 1f, 60f);
        arrivalFollowUp = null;
        arrivalAetheryte = null;
        if (followUp.CustomRoute is { } customRoute)
        {
            if (StartCustomRoute(customRoute, followUp.Position, followUp.NavigationPoint,
                    followUp.Name, followUp.ReplaceCustomRouteEndpoint))
                return true;
            DecisionStatus = $"自定义路线起点不可用，已回退普通导航：{followUp.Name}";
        }
        return NavigateTo(followUp.NavigationPoint, followUp.Name);
    }

    private Vector3? ResolveDestinationPoint(Vector3 target)
    {
        if (configuration.RandomizeEventNavigationDestination)
        {
            var maximumRadius = configuration.EventNavigationRandomRadius;
            var minimumRadius = MathF.Min(3f, maximumRadius);
            for (var index = 0; index < 8; index++)
            {
                var angle = Random.Shared.NextSingle() * MathF.Tau;
                var radius = MathF.Sqrt(
                    minimumRadius * minimumRadius + Random.Shared.NextSingle() *
                    (maximumRadius * maximumRadius - minimumRadius * minimumRadius));
                var probe = target + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
                var point = vnavmesh.QueryNearestReachable(probe, 2f, 30f);
                if (point is not { } reachable) continue;
                var distance = HorizontalDistanceSquared(reachable, target);
                if (distance >= 1.5f * 1.5f &&
                    distance <= (maximumRadius + 3f) * (maximumRadius + 3f))
                    return reachable;
            }
        }

        var direct = vnavmesh.QueryNearestReachable(target, 12f, 500f);
        if (direct != null) return direct;
        for (var index = 0; index < 8; index++)
        {
            var angle = MathF.Tau * index / 8f;
            var probe = target + new Vector3(MathF.Sin(angle) * 20f, 0f, MathF.Cos(angle) * 20f);
            var point = vnavmesh.QueryNearestReachable(probe, 4f, 500f);
            if (point != null) return point;
        }
        return null;
    }

    private static Task<RouteComparisonPaths> CollectRouteComparisonAsync(
        Task<List<Vector3>> directPath,
        IReadOnlyList<(CrescentAetheryte source, Vector3 origin, Task<List<Vector3>>? path)> routes,
        IReadOnlyList<(CrescentAetheryte source, Vector3 origin, Task<List<Vector3>>? path)> approaches,
        CancellationToken cancellationToken)
    {
        var allTasks = routes.Concat(approaches)
            .Where(item => item.path != null)
            .Select(item => item.path!)
            .Append(directPath)
            .ToArray();
        var allCompleted = Task.WhenAll(allTasks);
        return Task.WhenAny(allCompleted, Task.Delay(
                TimeSpan.FromMilliseconds(RouteDecisionWindowMilliseconds), cancellationToken))
            .ContinueWith(_ => new RouteComparisonPaths(
                    directPath.IsCompletedSuccessfully ? directPath.Result : null,
                    routes.Where(item => item.path?.IsCompletedSuccessfully == true)
                        .Select(item => new AetherytePath(item.source, item.origin, item.path!.Result)).ToArray(),
                    approaches.Where(item => item.path?.IsCompletedSuccessfully == true)
                        .Select(item => new AetherytePath(item.source, item.origin, item.path!.Result)).ToArray()),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private float CalculateSafePathDistance(Vector3 origin, Vector3 target, IReadOnlyList<Vector3> rawPath)
    {
        var path = ApplyAggroAvoidance(rawPath);
        if (path.Count == 0) return float.PositiveInfinity;
        var distance = Vector3.Distance(origin, path[0]);
        for (var index = 1; index < path.Count; index++)
            distance += Vector3.Distance(path[index - 1], path[index]);
        return distance + Vector3.Distance(path[^1], target);
    }

    private bool FallBackFromRouteComparison(Vector3 eventTarget, Vector3 navigationTarget,
        string name, Vector3 player, string reason)
    {
        if (configuration.DirectNavigationDistance > 0f &&
            HorizontalDistanceSquared(player, navigationTarget) <=
            configuration.DirectNavigationDistance * configuration.DirectNavigationDistance)
        {
            DecisionStatus = $"{tracker.AreaName}：{reason}，目标在回退阈值 {configuration.DirectNavigationDistance:F0}m 内，选择直达";
            return RememberEventTarget(NavigateTo(navigationTarget, name), eventTarget);
        }

        var fallback = CrescentAetheryteCatalog.ForTerritory(
                tracker.AreaName == "新月岛北部"
                    ? Core.PotCandidateCatalog.NorthHornTerritoryId
                    : Core.PotCandidateCatalog.SouthHornTerritoryId)
            .MinBy(item => HorizontalDistanceSquared(item.Position, navigationTarget));
        if (fallback.DataId == 0)
        {
            DecisionStatus = $"{tracker.AreaName}：{reason}，没有可用水晶，选择直达";
            return RememberEventTarget(NavigateTo(navigationTarget, name), eventTarget);
        }
        DecisionStatus = $"{tracker.AreaName}：{reason}，回退到目标最近水晶 {fallback.FallbackName}";
        return BeginAetheryteTravel(fallback, fallback.FallbackName,
            new NavigationFollowUp(eventTarget, navigationTarget, name));
    }

    private static string FormatSeconds(float seconds) =>
        float.IsFinite(seconds) ? $"{Math.Max(0f, seconds):F0}秒" : "不可达";

    private static bool IsInsideCrystalCamp(Vector3 position, IReadOnlyList<CrescentAetheryte> sources) =>
        sources.Any(source => HorizontalDistanceSquared(position, source.Position) <=
                              CrystalCampRadius * CrystalCampRadius);

    private bool UpdatePendingTeleport(CrescentAetheryte pending)
    {
        if (!tracker.IsSupportedTerritory)
        {
            Cancel("已离开新月岛，水晶传送已取消");
            return true;
        }
        if (condition[ConditionFlag.InCombat])
        {
            Cancel("进入战斗，水晶传送已取消");
            return true;
        }
        if (demiReturnPending && UpdateDemiReturn(playerPosition: tracker.PlayerPosition)) return true;
        if (tracker.PlayerPosition is { } currentPlayer && completeAtCampAfterDemiReturn &&
            HorizontalDistanceSquared(currentPlayer, pending.Position) <= 8f * 8f)
        {
            var completedName = pendingAetheryteName;
            ClearPendingTeleport();
            Status = $"已返回：{completedName}";
            return true;
        }
        if (!completeAtCampAfterDemiReturn && !IsMountedOrMounting() && TryTeleport(pending, pendingAetheryteName))
            return true;
        if (Environment.TickCount64 > teleportDeadline)
        {
            Cancel("水晶交互或传送超时");
            return true;
        }
        if (sourceAetheryte is not { } source || tracker.PlayerPosition is not { } player)
            return false;
        var crystal = FindClosestAetheryteObject(player);
        if (crystal is not { } actualCrystal)
        {
            if (HorizontalDistanceSquared(player, source.Position) > 3f * 3f)
                return active ? false : StartNavigationToSource(player);
            if (ownsMovingPath) vnavmesh.Stop();
            active = false;
            ownsMovingPath = false;
            Status = $"已到达水晶位置，正在等待加载可交互水晶：{pendingAetheryteName}";
            return true;
        }
        if (!IsWithinAetheryteInteractionRange(player, actualCrystal))
        {
            if (!active || Vector3.DistanceSquared(requestedDestination, actualCrystal.Position) > 1f)
            {
                if (Environment.TickCount64 < nextCrystalNavigationAt) return true;
                nextCrystalNavigationAt = Environment.TickCount64 + CrystalNavigationRetryMilliseconds;
                _ = NavigateToPreservingTeleport(actualCrystal.Position,
                    $"可交互水晶（随后传送至{pendingAetheryteName}）");
                return true;
            }
            return false;
        }
        if (ownsMovingPath) vnavmesh.Stop();
        active = false;
        ownsMovingPath = false;
        if (IsMountedOrMounting())
        {
            TryDismount();
            Status = $"正在下坐骑，随后传送至：{pendingAetheryteName}";
            return true;
        }
        if (Environment.TickCount64 < nextInteractionAt) return true;
        nextInteractionAt = Environment.TickCount64 + 1_000;
        if (TryInteractWithAetheryte(actualCrystal.Address))
            Status = $"已与水晶交互，正在传送至：{pendingAetheryteName}";
        else
            Status = $"已进入交互距离，正在重试水晶交互：{pendingAetheryteName}";
        return true;
    }

    private bool UpdateDemiReturn(Vector3? playerPosition)
    {
        var now = Environment.TickCount64;
        if (demiReturnAccepted)
        {
            demiReturnSawCasting |= condition[ConditionFlag.Casting];
            demiReturnSawTransition |= condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        }
        if (playerPosition is not { } player) return true;
        if (IsMountedOrMounting())
        {
            TryDismount();
            Status = $"正在下坐骑并准备亚返回，随后传送至：{pendingAetheryteName}";
            return true;
        }
        if (!demiReturnAccepted)
        {
            var actionManager = ActionManager.Instance();
            if (!condition[ConditionFlag.Casting] && actionManager != null && now >= nextDemiReturnAt &&
                actionManager->GetActionStatus(ActionType.Action, DemiReturnActionId) == 0)
            {
                nextDemiReturnAt = now + 1_000;
                if (actionManager->UseAction(ActionType.Action, DemiReturnActionId))
                {
                    demiReturnAccepted = true;
                    demiReturnSawCasting = false;
                    demiReturnSawTransition = false;
                    demiReturnStartedAt = now;
                    Status = $"正在亚返回，随后传送至：{pendingAetheryteName}";
                    return true;
                }
            }
            if (now - demiReturnStartedAt < 5_000) return true;
            demiReturnPending = false;
            Status = "亚返回当前不可用，改为自动前往水晶";
            return StartNavigationToSource(player);
        }
        var elapsed = now - demiReturnStartedAt;
        if (elapsed >= 35_000)
        {
            demiReturnPending = false;
            Status = "亚返回未能及时完成，改为自动前往水晶";
            return StartNavigationToSource(player);
        }
        var betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        if (betweenAreas || elapsed < 4_000 && condition[ConditionFlag.Casting]) return true;

        var movedFromOrigin = HorizontalDistanceSquared(player, demiReturnOrigin) >= 3f * 3f;
        var castCompleted = demiReturnSawCasting && elapsed >= 3_500;
        var fallbackElapsed = elapsed >= 4_000;
        var arrivedAtCamp = demiReturnSawTransition || movedFromOrigin;
        if (completeAtCampAfterDemiReturn && arrivedAtCamp)
        {
            var completedName = pendingAetheryteName;
            ClearPendingTeleport();
            Status = $"亚返回完成，已返回：{completedName}";
            return true;
        }
        if (arrivedAtCamp || castCompleted || fallbackElapsed)
        {
            demiReturnPending = false;
            teleportDeadline = now + 45_000;
            sourceAetheryte = CrescentAetheryteCatalog.ForTerritory(
                    pendingAetheryte is { DataId: >= 5571 and <= 5576 }
                        ? Core.PotCandidateCatalog.NorthHornTerritoryId
                        : Core.PotCandidateCatalog.SouthHornTerritoryId)
                .OrderBy(item => HorizontalDistanceSquared(item.Position, player))
                .FirstOrDefault();
            ObserveTiming(RouteTimingKind.DemiReturn, elapsed / 1000f, 1f, 35f);
            Status = $"亚返回完成，正在连接水晶并传送至：{pendingAetheryteName}";
            return StartNavigationToSource(player);
        }
        Status = $"正在等待亚返回完成，随后传送至：{pendingAetheryteName}";
        return true;
    }

    private bool StartNavigationToSource(Vector3 player)
    {
        if (sourceAetheryte is not { } source) return true;
        if (Environment.TickCount64 < nextCrystalNavigationAt) return true;
        nextCrystalNavigationAt = Environment.TickCount64 + CrystalNavigationRetryMilliseconds;
        var crystal = FindClosestAetheryteObject(player);
        var target = crystal?.Position ?? source.Position;
        if (crystal is { } actualCrystal && IsWithinAetheryteInteractionRange(player, actualCrystal))
            return false;
        var name = $"附近水晶（随后传送至{pendingAetheryteName}）";
        if (crystal != null)
            _ = NavigateToPreservingTeleport(target, name);
        else
            _ = NavigateTo(target, name);
        return true;
    }

    private bool TryTeleport(CrescentAetheryte target, string name)
    {
        var agent = AgentTelepotTown.Instance();
        if (agent == null || !agent->IsAgentActive()) return false;
        agent->TeleportToAetheryte(target.Index);
        if (ownsMovingPath) vnavmesh.Stop();
        active = false;
        ownsMovingPath = false;
        pathCancellation?.Cancel();
        pathCancellation?.Dispose();
        pathCancellation = null;
        pathTask = null;
        var followUp = pendingFollowUp;
        var origin = tracker.PlayerPosition ?? Vector3.Zero;
        ClearPendingTeleport();
        pendingFollowUp = null;
        if (followUp != null)
        {
            arrivalFollowUp = followUp;
            arrivalAetheryte = target;
            arrivalOrigin = origin;
            arrivalArmedAt = Environment.TickCount64;
            cancelInputArmedAt = arrivalArmedAt + 750;
        }
        Status = $"正在传送至：{name}";
        return true;
    }

    private AetheryteObject? FindClosestAetheryteObject(Vector3 player)
    {
        var southName = dataManager.GetExcelSheet<Lumina.Excel.Sheets.EObjName>()
            .GetRowOrDefault(2006473)?.Singular.ToString();
        var northName = dataManager.GetExcelSheet<Lumina.Excel.Sheets.EObjName>()
            .GetRowOrDefault(2014664)?.Singular.ToString();
        var gameObject = objectTable
            .Where(item => item != null && item.IsValid() && item.IsTargetable &&
                           item.ObjectKind == ObjectKind.EventObj && item.Address != nint.Zero &&
                           (item.BaseId is 2006473 or 2014664 ||
                            item.Name.ToString().Equals(southName, StringComparison.OrdinalIgnoreCase) ||
                            item.Name.ToString().Equals(northName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => HorizontalDistanceSquared(item.Position, player))
            .FirstOrDefault();
        return gameObject == null
            ? null
            : new AetheryteObject(gameObject.Address, gameObject.Position, MathF.Max(0f, gameObject.HitboxRadius));
    }

    private static bool IsWithinAetheryteInteractionRange(Vector3 player, AetheryteObject crystal)
    {
        var range = MathF.Max(4f, crystal.HitboxRadius + CrystalInteractionPadding);
        return HorizontalDistanceSquared(player, crystal.Position) <= range * range;
    }

    private bool IsNearPendingAetheryte(Vector3 player)
    {
        if (pendingAetheryte == null) return false;
        var target = FindClosestAetheryteObject(player)?.Position ?? sourceAetheryte?.Position;
        return target is { } position && HorizontalDistanceSquared(player, position) <=
            CrystalStuckRecoverySuppressionRadius * CrystalStuckRecoverySuppressionRadius;
    }

    private bool NavigateToPreservingTeleport(Vector3 target, string name)
    {
        if (ownsMovingPath) vnavmesh.Stop();
        pathCancellation?.Cancel();
        pathCancellation?.Dispose();
        pathCancellation = null;
        pathTask = null;
        active = false;
        ownsMovingPath = false;
        observedBusy = false;
        navigationIdleSince = 0;
        displayPath = [];
        var resolved = vnavmesh.QueryNearestReachable(target, 12f, 80f) ?? target;
        if (!vnavmesh.NavigateTo(resolved)) return NavigateTo(target, name);
        requestedDestination = target;
        destination = resolved;
        targetName = name;
        startedAt = Environment.TickCount64;
        cancelInputArmedAt = startedAt + 300;
        active = true;
        ownsMovingPath = true;
        Status = $"正在直接前往：{name}";
        return true;
    }

    private static Task<List<Vector3>>? ObservePathTask(Task<List<Vector3>>? task)
    {
        if (task == null) return null;
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static bool TryInteractWithAetheryte(nint address)
    {
        var nativeObject = (NativeGameObject*)(void*)address;
        var targetSystem = TargetSystem.Instance();
        if (nativeObject == null || targetSystem == null) return false;
        targetSystem->Target = nativeObject;
        var interacted = targetSystem->InteractWithObject(nativeObject, false) != 0;
        if (interacted) targetSystem->OpenObjectInteraction(nativeObject);
        return interacted;
    }

    private void ClearPendingTeleport()
    {
        pendingAetheryte = null;
        sourceAetheryte = null;
        pendingAetheryteName = string.Empty;
        nextInteractionAt = 0;
        nextCrystalNavigationAt = 0;
        teleportDeadline = 0;
        demiReturnPending = false;
        demiReturnAccepted = false;
        demiReturnSawCasting = false;
        demiReturnSawTransition = false;
        completeAtCampAfterDemiReturn = false;
        demiReturnOrigin = Vector3.Zero;
        demiReturnStartedAt = 0;
        nextDemiReturnAt = 0;
        cancelInputArmedAt = 0;
        nextAggroPathCheck = 0;
        nextMountRequest = 0;
        nextDismountRequest = 0;
    }

    private bool IsMountedOrMounting() =>
        condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting];

    private bool TryDismount()
    {
        if (!condition[ConditionFlag.Mounted]) return false;
        var now = Environment.TickCount64;
        if (now < nextDismountRequest) return true;
        nextDismountRequest = now + 750;
        var actionManager = ActionManager.Instance();
        var requested = actionManager != null && actionManager->UseAction(ActionType.GeneralAction, 9);
        if (requested && dismountRequestedAt == 0) dismountRequestedAt = now;
        return requested;
    }

    public bool RequestDismount() => TryDismount();

    private bool TryRequestMount()
    {
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting]) return true;
        if (condition[ConditionFlag.Casting]) return false;
        var now = Environment.TickCount64;
        if (now < nextMountRequest) return false;
        nextMountRequest = now + MountRequestIntervalMilliseconds;
        var actionManager = ActionManager.Instance();
        var requested = actionManager != null &&
                        actionManager->GetActionStatus(ActionType.GeneralAction, 9) == 0 &&
                        actionManager->UseAction(ActionType.GeneralAction, 9);
        if (requested && mountRequestedAt == 0) mountRequestedAt = now;
        return requested;
    }

    private void CompleteObservedActionTimings()
    {
        var now = Environment.TickCount64;
        if (mountRequestedAt != 0 && condition[ConditionFlag.Mounted])
        {
            ObserveTiming(RouteTimingKind.Mount, (now - mountRequestedAt) / 1000f, 0.2f, 8f);
            mountRequestedAt = 0;
        }
        else if (mountRequestedAt != 0 && now - mountRequestedAt > 8_000)
            mountRequestedAt = 0;

        if (dismountRequestedAt != 0 && !condition[ConditionFlag.Mounted] && !condition[ConditionFlag.Mounting])
        {
            ObserveTiming(RouteTimingKind.Dismount, (now - dismountRequestedAt) / 1000f, 0.2f, 8f);
            dismountRequestedAt = 0;
        }
        else if (dismountRequestedAt != 0 && now - dismountRequestedAt > 8_000)
            dismountRequestedAt = 0;
    }

    private void ObserveTiming(RouteTimingKind kind, float seconds, float minimum, float maximum)
    {
        if (!float.IsFinite(seconds) || seconds < minimum || seconds > maximum) return;
        var (average, sampleCount) = kind switch
        {
            RouteTimingKind.DemiReturn => (configuration.AverageDemiReturnSeconds, configuration.DemiReturnTimingSamples),
            RouteTimingKind.CrystalTransfer => (configuration.AverageCrystalTransferSeconds, configuration.CrystalTransferTimingSamples),
            RouteTimingKind.Mount => (configuration.AverageMountSeconds, configuration.MountTimingSamples),
            _ => (configuration.AverageDismountSeconds, configuration.DismountTimingSamples)
        };
        sampleCount = Math.Clamp(sampleCount, 0, 20);
        average = sampleCount < 20
            ? (average * sampleCount + seconds) / (sampleCount + 1)
            : average * 0.9f + seconds * 0.1f;
        sampleCount = Math.Min(20, sampleCount + 1);
        switch (kind)
        {
            case RouteTimingKind.DemiReturn:
                configuration.AverageDemiReturnSeconds = average;
                configuration.DemiReturnTimingSamples = sampleCount;
                break;
            case RouteTimingKind.CrystalTransfer:
                configuration.AverageCrystalTransferSeconds = average;
                configuration.CrystalTransferTimingSamples = sampleCount;
                break;
            case RouteTimingKind.Mount:
                configuration.AverageMountSeconds = average;
                configuration.MountTimingSamples = sampleCount;
                break;
            default:
                configuration.AverageDismountSeconds = average;
                configuration.DismountTimingSamples = sampleCount;
                break;
        }
        save();
    }

    private bool IsCancelInputPressed() =>
        IsRawPressed(VirtualKey.W) || IsRawPressed(VirtualKey.A) || IsRawPressed(VirtualKey.S) ||
        IsRawPressed(VirtualKey.D) || IsRawPressed(VirtualKey.ESCAPE);

    private bool IsRawPressed(VirtualKey key) => keyState[key] || keyState.GetRawValue(key) != 0;

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return x * x + z * z;
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right) =>
        MathF.Sqrt(HorizontalDistanceSquared(left, right));

    private sealed record NavigationFollowUp(Vector3 Position, Vector3 NavigationPoint, string Name,
        CustomNavigationRoute? CustomRoute = null, bool ReplaceCustomRouteEndpoint = false);

    private readonly record struct RecordedRouteSample(Vector3 Position, bool Jump, bool Airborne);

    private readonly record struct AetheryteObject(nint Address, Vector3 Position, float HitboxRadius);

    private sealed record PendingRoutePlan(
        NavigationFollowUp FollowUp,
        long StartedAt,
        CancellationTokenSource Cancellation,
        Vector3 DirectOrigin,
        Vector3 DestinationPoint,
        bool InitiallyMounted,
        bool NearCrystal,
        bool DemiReturnAvailable,
        Task<RouteComparisonPaths> Task);

    private sealed record RouteComparisonPaths(
        List<Vector3>? DirectPath,
        IReadOnlyList<AetherytePath> AetherytePaths,
        IReadOnlyList<AetherytePath> ApproachPaths);

    private sealed record AetherytePath(
        CrescentAetheryte Aetheryte,
        Vector3 Origin,
        List<Vector3> Path);

    private enum StuckRecoveryStage
    {
        Monitoring,
        ObservingJump,
        Replanned
    }

    private readonly record struct RecoveryCell(int X, int Z)
    {
        public static RecoveryCell From(Vector3 position) =>
            new((int)MathF.Round(position.X / 4f), (int)MathF.Round(position.Z / 4f));
    }

    private readonly record struct RecoveryMemory(
        Vector3 Origin,
        Vector3? SuccessfulWaypoint,
        int FailedSides);

    private enum RouteTimingKind
    {
        DemiReturn,
        CrystalTransfer,
        Mount,
        Dismount
    }
}
