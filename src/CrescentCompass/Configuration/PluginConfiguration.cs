using CrescentCompass.Core;
using Dalamud.Configuration;

namespace CrescentCompass.Configuration;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    public const int CurrentVersion = 15;
    public int Version { get; set; } = CurrentVersion;
    public bool ShowMainWindow { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public bool ShowCandidateIndicators { get; set; } = true;
    public bool ShowTreasureIndicators { get; set; } = true;
    public bool ShowFieldTreasureIndicators { get; set; } = true;
    public float FieldTreasureIndicatorRadius { get; set; } = 300f;
    public bool ShowFieldTreasureMapMarkers { get; set; } = true;
    public bool ShowConfirmedFieldTreasureMapMarkers { get; set; } = true;
    public bool AutoSurveyFieldTreasureCounts { get; set; } = true;
    public bool ShowEventSceneIndicators { get; set; } = true;
    public bool HideIndicatorsInCombat { get; set; } = true;
    public bool ShowMap { get; set; } = true;
    public bool InterruptNavigationOnMovementInput { get; set; } = true;
    public bool ShowMonsterAggroRanges { get; set; } = true;
    public bool AvoidMonsterAggroRanges { get; set; } = true;
    public bool RandomizeEventNavigationDestination { get; set; } = true;
    public float EventNavigationRandomRadius { get; set; } = 8f;
    public bool AutoCalibrateAggroRanges { get; set; }
    public bool ShowAggroDebug { get; set; }
    public float DirectNavigationDistance { get; set; } = 60f;
    public float MinimumTeleportSavingSeconds { get; set; } = 10f;
    public float AverageDemiReturnSeconds { get; set; } = 8f;
    public float AverageCrystalTransferSeconds { get; set; } = 7f;
    public float AverageMountSeconds { get; set; } = 2f;
    public float AverageDismountSeconds { get; set; } = 1.5f;
    public int DemiReturnTimingSamples { get; set; }
    public int CrystalTransferTimingSamples { get; set; }
    public int MountTimingSamples { get; set; }
    public int DismountTimingSamples { get; set; }
    public float AggroEdgeRange { get; set; } = 8f;
    public float AggroSafetyMargin { get; set; } = 2f;
    public float AggroScanRange { get; set; } = 120f;
    public float AggroVerticalTolerance { get; set; } = 6f;
    public bool Paused { get; set; }
    public int MaxSceneCandidates { get; set; } = 5;
    public float IndicatorRadius { get; set; } = 300f;
    public float WindowOpacity { get; set; } = 0.9f;
    public bool ComfortableUiDensity { get; set; }
    public bool ReduceMotion { get; set; }
    public bool MapDetailsExpanded { get; set; }
    public bool ShowMapRouteLayer { get; set; }
    public float MapTextureOpacity { get; set; } = 1f;
    public float MapMarkerOpacity { get; set; } = 1f;
    public float AetheryteIconScale { get; set; } = 1.25f;
    public float LandmarkIconScale { get; set; } = 1f;
    public float EventIconScale { get; set; } = 1f;
    public float CeIconScale { get; set; } = 1f;
    public float FateIconScale { get; set; } = 1f;
    public float MagicPotIconScale { get; set; } = 1f;
    public float ForecastIconScale { get; set; } = 1f;
    public float CandidateIconScale { get; set; } = 0.8f;
    public float FocusedCandidateIconScale { get; set; } = 0.8f;
    public float FieldTreasurePointIconScale { get; set; } = 0.5f;
    public float ConfirmedFieldTreasureIconScale { get; set; } = 0.5f;
    public float PartyMemberIconScale { get; set; } = 0.6f;
    public float MapIconHoverScale { get; set; } = 1.15f;
    public HashSet<uint> WatchedCeIds { get; set; } = [];
    public HashSet<uint> WatchedFateIds { get; set; } = [];
    public bool NotifyWatchedCe { get; set; } = true;
    public bool WatchedCeSound { get; set; } = true;
    public int EventNotificationSoundEffect { get; set; } = 1;
    public bool ShowWindowsEventNotifications { get; set; } = true;
    public bool ShowProminentInGameEventNotifications { get; set; } = true;
    public EventBannerPosition ProminentBannerPosition { get; set; } = EventBannerPosition.TopCenter;
    public EventBannerDetail ProminentBannerDetail { get; set; } = EventBannerDetail.Standard;
    public float ProminentBannerDurationSeconds { get; set; } = 8f;
    public float ProminentBannerWidth { get; set; } = 620f;
    public float ProminentBannerOpacity { get; set; } = 0.6f;
    public float ProminentBannerCustomX { get; set; } = 0.5f;
    public float ProminentBannerCustomY { get; set; } = 0.04f;
    public bool PauseProminentBannerOnHover { get; set; } = true;
    public bool ShowProminentBannerNavigateButton { get; set; } = true;
    public bool NotifyCeOnEntry { get; set; } = true;
    public bool EnableEventAutomation { get; set; }
    public HashSet<uint> AutomatedCeIds { get; set; } = [];
    public HashSet<uint> AutomatedFateIds { get; set; } = [];
    public EventAutomationPriority EventAutomationPriority { get; set; } = EventAutomationPriority.CeFirst;
    public int EventAutomationMinimumRemainingSeconds { get; set; } = 120;
    public int EventAutomationMaximumFateProgress { get; set; } = 70;
    public float EventAutomationArrivalRadius { get; set; } = 35f;
    public float EventAutomationSettleSeconds { get; set; } = 5f;
    public EventMechanicProvider EventMechanicProvider { get; set; } = EventMechanicProvider.BossModReborn;
    public CombatRotationProvider CombatRotationProvider { get; set; } = CombatRotationProvider.AEAssistV3;
    public string BossModAutomationPreset { get; set; } = string.Empty;
    public List<EventAutomationWaitingPoint> EventAutomationWaitingPoints { get; set; } = [];
    public List<CustomNavigationRoute> CustomNavigationRoutes { get; set; } = [];
    public bool UseCustomRoutesForAutomation { get; set; } = true;
    public bool UseCustomRoutesForManualNavigation { get; set; } = true;
    public List<ConfirmedFieldTreasureRecord> ConfirmedFieldTreasures { get; set; } = [];
    public bool AutoCalibratePotCandidates { get; set; } = true;
    public List<PotCandidateCalibrationRecord> PotCandidateCalibrations { get; set; } = [];
    public void Normalize()
    {
        if (Version < 3)
        {
            AutoSurveyFieldTreasureCounts = true;
            Version = 3;
        }
        if (Version < 6)
        {
            WatchedCeSound = true;
            ShowWindowsEventNotifications = true;
            Version = 6;
        }
        if (Version < 7)
        {
            ShowProminentInGameEventNotifications = true;
            Version = 7;
        }
        if (Version < 8)
        {
            ShowConfirmedFieldTreasureMapMarkers = true;
            Version = 8;
        }
        if (Version < 9)
        {
            ProminentBannerPosition = EventBannerPosition.TopCenter;
            ProminentBannerDetail = EventBannerDetail.Standard;
            ProminentBannerDurationSeconds = 8f;
            ProminentBannerWidth = 620f;
            ProminentBannerOpacity = 0.6f;
            ProminentBannerCustomX = 0.5f;
            ProminentBannerCustomY = 0.04f;
            PauseProminentBannerOnHover = true;
            ShowProminentBannerNavigateButton = true;
            Version = 9;
        }
        if (Version < 10)
        {
            AutoCalibratePotCandidates = true;
            Version = 10;
        }
        if (Version < 11)
        {
            RandomizeEventNavigationDestination = true;
            EventNavigationRandomRadius = 8f;
            Version = 11;
        }
        if (Version < 12)
        {
            EventNotificationSoundEffect = 1;
            Version = 12;
        }
        if (Version < 13)
        {
            EnableEventAutomation = false;
            EventAutomationPriority = EventAutomationPriority.CeFirst;
            EventAutomationMinimumRemainingSeconds = 120;
            EventAutomationMaximumFateProgress = 70;
            EventAutomationArrivalRadius = 35f;
            EventAutomationSettleSeconds = 5f;
            EventMechanicProvider = EventMechanicProvider.BossModReborn;
            CombatRotationProvider = CombatRotationProvider.AEAssistV3;
            Version = 13;
        }
        if (Version < 14)
        {
            CustomNavigationRoutes = [];
            Version = 14;
        }
        if (Version < 15)
        {
            UseCustomRoutesForAutomation = true;
            UseCustomRoutesForManualNavigation = true;
            Version = 15;
        }
        Version = Math.Max(CurrentVersion, Version);
        DirectNavigationDistance = Math.Clamp(DirectNavigationDistance, 0f, 300f);
        MinimumTeleportSavingSeconds = Math.Clamp(MinimumTeleportSavingSeconds, 0f, 60f);
        AverageDemiReturnSeconds = Math.Clamp(AverageDemiReturnSeconds, 1f, 35f);
        AverageCrystalTransferSeconds = Math.Clamp(AverageCrystalTransferSeconds, 1f, 60f);
        AverageMountSeconds = Math.Clamp(AverageMountSeconds, 0.2f, 8f);
        AverageDismountSeconds = Math.Clamp(AverageDismountSeconds, 0.2f, 8f);
        DemiReturnTimingSamples = Math.Clamp(DemiReturnTimingSamples, 0, 20);
        CrystalTransferTimingSamples = Math.Clamp(CrystalTransferTimingSamples, 0, 20);
        MountTimingSamples = Math.Clamp(MountTimingSamples, 0, 20);
        DismountTimingSamples = Math.Clamp(DismountTimingSamples, 0, 20);
        AggroEdgeRange = Math.Clamp(AggroEdgeRange, 4f, 20f);
        AggroSafetyMargin = Math.Clamp(AggroSafetyMargin, 0f, 6f);
        AggroScanRange = Math.Clamp(AggroScanRange, 20f, 150f);
        AggroVerticalTolerance = Math.Clamp(AggroVerticalTolerance, 1f, 20f);
        EventNavigationRandomRadius = Math.Clamp(EventNavigationRandomRadius, 2f, 15f);
        EventNotificationSoundEffect = Math.Clamp(EventNotificationSoundEffect, 1, 16);
        EventAutomationMinimumRemainingSeconds = Math.Clamp(EventAutomationMinimumRemainingSeconds, 30, 900);
        EventAutomationMaximumFateProgress = Math.Clamp(EventAutomationMaximumFateProgress, 10, 95);
        EventAutomationArrivalRadius = Math.Clamp(EventAutomationArrivalRadius, 10f, 80f);
        EventAutomationSettleSeconds = Math.Clamp(EventAutomationSettleSeconds, 2f, 15f);
        MaxSceneCandidates = Math.Clamp(MaxSceneCandidates, 1, 12);
        IndicatorRadius = Math.Clamp(IndicatorRadius, 50f, 1000f);
        FieldTreasureIndicatorRadius = Math.Clamp(FieldTreasureIndicatorRadius, 20f, 1000f);
        WindowOpacity = Math.Clamp(WindowOpacity, 0.25f, 1f);
        MapTextureOpacity = Math.Clamp(MapTextureOpacity, 0.25f, 1f);
        MapMarkerOpacity = Math.Clamp(MapMarkerOpacity, 0.25f, 1f);
        AetheryteIconScale = Math.Clamp(AetheryteIconScale, 0.5f, 3f);
        LandmarkIconScale = Math.Clamp(LandmarkIconScale, 0.5f, 3f);
        EventIconScale = Math.Clamp(EventIconScale, 0.5f, 3f);
        CeIconScale = Math.Clamp(CeIconScale, 0.5f, 3f);
        FateIconScale = Math.Clamp(FateIconScale, 0.5f, 3f);
        MagicPotIconScale = Math.Clamp(MagicPotIconScale, 0.5f, 3f);
        ForecastIconScale = Math.Clamp(ForecastIconScale, 0.5f, 3f);
        CandidateIconScale = Math.Clamp(CandidateIconScale, 0.5f, 3f);
        FocusedCandidateIconScale = Math.Clamp(FocusedCandidateIconScale, 0.5f, 3f);
        FieldTreasurePointIconScale = Math.Clamp(FieldTreasurePointIconScale, 0.5f, 3f);
        ConfirmedFieldTreasureIconScale = Math.Clamp(ConfirmedFieldTreasureIconScale, 0.5f, 3f);
        PartyMemberIconScale = Math.Clamp(PartyMemberIconScale, 0.5f, 3f);
        MapIconHoverScale = Math.Clamp(MapIconHoverScale, 1f, 2.5f);
        ProminentBannerDurationSeconds = Math.Clamp(ProminentBannerDurationSeconds, 3f, 30f);
        ProminentBannerWidth = Math.Clamp(ProminentBannerWidth, 420f, 900f);
        ProminentBannerOpacity = Math.Clamp(ProminentBannerOpacity, 0.5f, 1f);
        ProminentBannerCustomX = Math.Clamp(ProminentBannerCustomX, 0f, 1f);
        ProminentBannerCustomY = Math.Clamp(ProminentBannerCustomY, 0f, 1f);
        if (!Enum.IsDefined(ProminentBannerPosition)) ProminentBannerPosition = EventBannerPosition.TopCenter;
        if (!Enum.IsDefined(ProminentBannerDetail)) ProminentBannerDetail = EventBannerDetail.Standard;
        WatchedCeIds ??= [];
        WatchedFateIds ??= [];
        AutomatedCeIds ??= [];
        AutomatedFateIds ??= [];
        BossModAutomationPreset ??= string.Empty;
        EventAutomationWaitingPoints ??= [];
        EventAutomationWaitingPoints.RemoveAll(item =>
            item.TerritoryId is not PotCandidateCatalog.SouthHornTerritoryId and
                not PotCandidateCatalog.NorthHornTerritoryId ||
            !float.IsFinite(item.X) || !float.IsFinite(item.Y) || !float.IsFinite(item.Z));
        CustomNavigationRoutes ??= [];
        CustomNavigationRoutes.RemoveAll(route =>
            string.IsNullOrWhiteSpace(route.Id) || route.TerritoryId is not PotCandidateCatalog.SouthHornTerritoryId and
                not PotCandidateCatalog.NorthHornTerritoryId || route.SourceAetheryteDataId == 0 ||
            route.EventId == 0 || !Enum.IsDefined(route.Kind) || route.Points == null || route.Points.Count < 2 ||
            route.Points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)));
        if (!Enum.IsDefined(EventAutomationPriority)) EventAutomationPriority = EventAutomationPriority.CeFirst;
        if (!Enum.IsDefined(EventMechanicProvider)) EventMechanicProvider = EventMechanicProvider.BossModReborn;
        if (!Enum.IsDefined(CombatRotationProvider)) CombatRotationProvider = CombatRotationProvider.AEAssistV3;
        ConfirmedFieldTreasures ??= [];
        PotCandidateCalibrations ??= [];
        PotCandidateCalibrations.RemoveAll(item =>
            item.CandidateId == 0 || item.SampleCount <= 0 ||
            !float.IsFinite(item.X) || !float.IsFinite(item.Y) || !float.IsFinite(item.Z));
    }
}

public enum EventAutomationPriority
{
    CeFirst,
    FateFirst,
    Nearest,
    MagicPotFirst
}

public enum EventMechanicProvider
{
    None,
    BossModReborn
}

public enum CombatRotationProvider
{
    None,
    AEAssistV3,
    PromeRotation,
    RotationSolverReborn,
    BossModReborn
}

[Serializable]
public sealed class EventAutomationWaitingPoint
{
    public uint TerritoryId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public enum CustomNavigationRouteKind
{
    Fate,
    CriticalEngagement
}

[Serializable]
public sealed class CustomNavigationRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public uint TerritoryId { get; set; }
    public uint SourceAetheryteDataId { get; set; }
    public CustomNavigationRouteKind Kind { get; set; }
    public uint EventId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public List<CustomNavigationRoutePoint> Points { get; set; } = [];
}

[Serializable]
public sealed class CustomNavigationRoutePoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public CustomNavigationRoutePointAction Action { get; set; }
}

public enum CustomNavigationRoutePointAction
{
    None,
    Jump
}

[Serializable]
public sealed class PotCandidateCalibrationRecord
{
    public uint TerritoryId { get; set; }
    public uint CandidateId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int SampleCount { get; set; }
}

public enum EventBannerPosition
{
    TopCenter,
    TopLeft,
    TopRight,
    Custom
}

public enum EventBannerDetail
{
    Compact,
    Standard,
    Detailed
}

[Serializable]
public sealed class ConfirmedFieldTreasureRecord
{
    public uint TerritoryId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int Kind { get; set; }
}
