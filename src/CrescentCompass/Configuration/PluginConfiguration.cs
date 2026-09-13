using Dalamud.Configuration;

namespace CrescentCompass.Configuration;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 8;
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
    public bool ShowWindowsEventNotifications { get; set; } = true;
    public bool ShowProminentInGameEventNotifications { get; set; } = true;
    public bool NotifyCeOnEntry { get; set; } = true;
    public List<ConfirmedFieldTreasureRecord> ConfirmedFieldTreasures { get; set; } = [];
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
        Version = Math.Max(8, Version);
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
        WatchedCeIds ??= [];
        WatchedFateIds ??= [];
        ConfirmedFieldTreasures ??= [];
    }
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
