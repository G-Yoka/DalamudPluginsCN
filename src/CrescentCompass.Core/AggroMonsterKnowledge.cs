using System.Numerics;

namespace CrescentCompass.Core;

internal enum AggroSenseType
{
    Unknown,
    Sight,
    Sound,
    Proximity,
    Spell,
    LowHealth,
    Group
}

internal enum AggroRangeSource
{
    Fallback,
    LocalCalibration
}

internal enum AggroSightResolutionState
{
    Resolved,
    InsufficientTriggered,
    InsufficientRearClear,
    RearTriggerConflict,
    InsufficientForwardTriggered,
    InsufficientOuterClear,
    InvalidBounds
}

internal readonly record struct AggroSightResolution(
    AggroSightResolutionState State,
    int TriggeredCount,
    int RequiredTriggeredCount,
    int ForwardTriggeredCount,
    int RearClearCount,
    int RearTriggerCount,
    int OuterClearCount,
    float ConfirmedVisibleDegrees,
    float ConfirmedClearDegrees,
    float HalfAngleDegrees,
    float Confidence)
{
    public bool IsResolved => State == AggroSightResolutionState.Resolved;
}

internal readonly record struct AggroMonsterProfile(
    uint NameId,
    AggroSenseType SenseType,
    float FallbackEdgeRange,
    string Evidence);

/// <summary>
/// Bundled, versioned knowledge. Unknown entries deliberately remain unknown;
/// the runtime observer may refine distance but never invents a sense type.
/// </summary>
internal static class OccultCrescentMonsterCatalog
{
    public const int SchemaVersion = 6;
    public const float DefaultFallbackEdgeRange = 8f;
    public const float DefaultSightHalfAngleDegrees = 45f;
    public const uint FirstCommonNameId = 14857;
    public const uint LastCommonNameId = 14923;

    // South Horn ordinary field mobs, keyed by BNpcName row ID. The gaps are
    // event or unrelated actors and must not enter scene drawing/path avoidance.
    private static readonly HashSet<uint> SouthHornCommonNameIds =
    [
        13743, 13744, 13745, 13746, 13759,
        .. Enumerable.Range(13871, 22).Select(value => (uint)value),
        .. Enumerable.Range(13895, 11).Select(value => (uint)value),
        13907, 13908, 13910, 13911,
        .. Enumerable.Range(13913, 31).Select(value => (uint)value)
    ];

    public static bool TryGet(uint territoryId, uint nameId, out AggroMonsterProfile profile)
    {
        var isSouthHorn = territoryId == PotCandidateCatalog.SouthHornTerritoryId &&
                          SouthHornCommonNameIds.Contains(nameId);
        var isNorthHorn = territoryId == PotCandidateCatalog.NorthHornTerritoryId &&
                          nameId is >= FirstCommonNameId and <= LastCommonNameId;
        if (!isSouthHorn && !isNorthHorn)
        {
            profile = default;
            return false;
        }

        return CreateProfile(nameId, out profile);
    }

    public static bool TryGet(uint nameId, out AggroMonsterProfile profile)
    {
        if (!SouthHornCommonNameIds.Contains(nameId) &&
            nameId is < FirstCommonNameId or > LastCommonNameId)
        {
            profile = default;
            return false;
        }

        return CreateProfile(nameId, out profile);
    }

    private static bool CreateProfile(uint nameId, out AggroMonsterProfile profile)
    {
        // Verified entries from the North Horn enemy table as of 2026-09-07.
        // The remaining regular mobs keep Unknown until a reliable source or
        // clean in-game classification is available.
        var senseType = nameId switch
        {
            14917 => AggroSenseType.Sight,       // Crescent Geshunpest
            14918 => AggroSenseType.Proximity,   // Crescent Necrodium
            14923 => AggroSenseType.Spell,       // Crescent Flame
            _     => AggroSenseType.Unknown
        };
        var evidence = senseType == AggroSenseType.Unknown
                           ? "builtin-fallback"
                           : "north-horn-enemy-table-2026-09-07";
        profile = new(nameId, senseType, DefaultFallbackEdgeRange, evidence);
        return true;
    }

    public static string DisplayName(AggroSenseType senseType) => senseType switch
    {
        AggroSenseType.Sight     => "视线",
        AggroSenseType.Sound     => "声音",
        AggroSenseType.Proximity => "接近",
        AggroSenseType.Spell     => "魔法",
        AggroSenseType.LowHealth => "低血量",
        AggroSenseType.Group     => "连锁",
        _                        => "未知"
    };

    public static float DefaultHalfAngleDegrees(AggroSenseType senseType) =>
        senseType == AggroSenseType.Sight ? DefaultSightHalfAngleDegrees : 180f;
}

[Serializable]
internal sealed class AggroCalibrationRecord
{
    private const int MaximumSamples = 9;
    private const int MaximumAngleSamples = 48;
    private const int MinimumTriggeredSightSamples = 3;
    private const int MinimumClearRearSamples = 4;

    public List<float> EdgeRangeSamples = [];
    public List<float> TriggerBearingSamples = [];
    public List<float> ClearBearingSamples = [];
    public long LastObservedAtUnix;

    public int SampleCount => EdgeRangeSamples?.Count ?? 0;
    public int TriggerBearingSampleCount => TriggerBearingSamples?.Count ?? 0;
    public int ClearBearingSampleCount => ClearBearingSamples?.Count ?? 0;

    public bool AddSample(float edgeRange, long observedAtUnix)
    {
        if (!float.IsFinite(edgeRange) || edgeRange is < 4f or > 20f)
            return false;

        EdgeRangeSamples ??= [];
        if (EdgeRangeSamples.Count >= MaximumSamples)
            EdgeRangeSamples.RemoveAt(0);
        EdgeRangeSamples.Add(MathF.Round(edgeRange * 10f) / 10f);
        LastObservedAtUnix = observedAtUnix;
        return true;
    }

    public bool AddTriggeredSample(float edgeRange, float absoluteBearingDegrees, long observedAtUnix)
    {
        if (!IsBearingValid(absoluteBearingDegrees) || !AddSample(edgeRange, observedAtUnix))
            return false;

        AddBounded(TriggerBearingSamples ??= [], NormalizeBearing(absoluteBearingDegrees));
        return true;
    }

    public bool AddClearSightSample(float absoluteBearingDegrees, long observedAtUnix)
    {
        if (!IsBearingValid(absoluteBearingDegrees)) return false;
        AddBounded(ClearBearingSamples ??= [], NormalizeBearing(absoluteBearingDegrees));
        LastObservedAtUnix = observedAtUnix;
        return true;
    }

    public float Resolve(float fallback)
    {
        if (EdgeRangeSamples is not { Count: > 0 }) return fallback;
        var samples = EdgeRangeSamples
            .Where(sample => float.IsFinite(sample) && sample is >= 4f and <= 20f)
            .Order()
            .ToArray();
        if (samples.Length == 0) return fallback;

        var index = (int)MathF.Ceiling((samples.Length - 1) * 0.75f);
        return Math.Clamp(samples[index] + 0.5f, 4.5f, 20.5f);
    }

    public bool TryResolveSightCone(
        AggroSenseType builtInSenseType,
        out float halfAngleDegrees,
        out float confidence)
        => TryResolveSightCone(
            builtInSenseType,
            out halfAngleDegrees,
            out confidence,
            out _,
            out _);

    public bool TryResolveSightCone(
        AggroSenseType builtInSenseType,
        out float halfAngleDegrees,
        out float confidence,
        out float confirmedVisibleDegrees,
        out float confirmedClearDegrees)
    {
        var resolution = EvaluateSightCone(builtInSenseType);
        halfAngleDegrees = resolution.HalfAngleDegrees;
        confidence = resolution.Confidence;
        confirmedVisibleDegrees = resolution.ConfirmedVisibleDegrees;
        confirmedClearDegrees = resolution.ConfirmedClearDegrees;
        return resolution.IsResolved;
    }

    public AggroSightResolution EvaluateSightCone(AggroSenseType builtInSenseType)
    {
        var triggered = ValidBearings(TriggerBearingSamples).Order().ToArray();
        var clear = ValidBearings(ClearBearingSamples).Order().ToArray();
        var clearRear = clear.Count(angle => angle >= 120f);
        var rearTriggers = triggered.Count(angle => angle >= 150f);
        var forwardTriggers = triggered.Count(angle => angle <= 120f);
        var requiredTriggered = builtInSenseType == AggroSenseType.Sight ? 2 : MinimumTriggeredSightSamples;
        if (triggered.Length < requiredTriggered)
            return Failed(AggroSightResolutionState.InsufficientTriggered);
        if (clearRear < MinimumClearRearSamples)
            return Failed(AggroSightResolutionState.InsufficientRearClear);

        // A rear trigger contradicts a stable forward-only sight cone. Stay with a
        // conservative circle until later observations resolve the conflict.
        if (rearTriggers > 0) return Failed(AggroSightResolutionState.RearTriggerConflict);
        if (builtInSenseType != AggroSenseType.Sight &&
            forwardTriggers < MinimumTriggeredSightSamples)
            return Failed(AggroSightResolutionState.InsufficientForwardTriggered);

        var lowerIndex = (int)MathF.Ceiling((triggered.Length - 1) * 0.9f);
        var visibleBound = triggered[lowerIndex];
        var clearOutsideVisible = clear
            .Where(angle => angle >= visibleBound + 5f)
            .ToArray();
        if (clearOutsideVisible.Length < 2)
            return Failed(AggroSightResolutionState.InsufficientOuterClear, visibleBound, clearOutsideVisible.Length);

        // The lower quartile resists one unusually small clear sample while still
        // allowing repeated side observations to narrow an overly broad cone.
        var upperIndex = (int)MathF.Floor((clearOutsideVisible.Length - 1) * 0.25f);
        var clearBound = clearOutsideVisible[upperIndex];
        if (clearBound <= visibleBound + 5f)
            return Failed(AggroSightResolutionState.InvalidBounds, visibleBound, clearOutsideVisible.Length, clearBound);

        var halfAngle = Math.Clamp(
            clearBound + 5f,
            visibleBound + 5f,
            150f);
        var resolvedConfidence = Math.Clamp(
            Math.Min(
                Math.Min(triggered.Length / 8f, clearOutsideVisible.Length / 10f),
                (clearBound - visibleBound) <= 45f ? 1f : 0.75f),
            0f,
            1f);
        return new(
            AggroSightResolutionState.Resolved,
            triggered.Length,
            requiredTriggered,
            forwardTriggers,
            clearRear,
            rearTriggers,
            clearOutsideVisible.Length,
            visibleBound,
            clearBound,
            halfAngle,
            resolvedConfidence);

        AggroSightResolution Failed(
            AggroSightResolutionState state,
            float visible = 0f,
            int outerClear = 0,
            float clearBound = 180f) => new(
                state,
                triggered.Length,
                requiredTriggered,
                forwardTriggers,
                clearRear,
                rearTriggers,
                outerClear,
                visible,
                clearBound,
                180f,
                0f);
    }

    public void ClearSightSamples()
    {
        TriggerBearingSamples = [];
        ClearBearingSamples = [];
    }

    private static IEnumerable<float> ValidBearings(IEnumerable<float>? values) =>
        values?.Where(IsBearingValid).Select(NormalizeBearing) ?? [];

    private static bool IsBearingValid(float value) => float.IsFinite(value) && value is >= 0f and <= 180f;

    private static float NormalizeBearing(float value) => MathF.Round(Math.Clamp(MathF.Abs(value), 0f, 180f) * 10f) / 10f;

    private static void AddBounded(List<float> samples, float value)
    {
        if (samples.Count >= MaximumAngleSamples) samples.RemoveAt(0);
        samples.Add(value);
    }
}

internal static class AggroSightKnowledge
{
    public static float AbsoluteBearingDegrees(Vector3 observer, Vector3 monster, float monsterRotation)
    {
        var bearing = MathF.Atan2(observer.X - monster.X, observer.Z - monster.Z);
        return MathF.Abs(MathF.IEEERemainder(bearing - monsterRotation, MathF.Tau)) * 180f / MathF.PI;
    }

    public static bool IsInsideSightCone(Vector3 point, Vector3 center, float rotation, float halfAngleDegrees) =>
        AbsoluteBearingDegrees(point, center, rotation) <= Math.Clamp(halfAngleDegrees, 0f, 180f);
}

internal static class AggroRangeKnowledge
{
    public static float ResolveEdgeRange(
        AggroMonsterProfile profile,
        IReadOnlyDictionary<uint, AggroCalibrationRecord>? calibrations,
        float configuredFallback,
        out int sampleCount,
        out AggroRangeSource source)
    {
        if (calibrations != null &&
            calibrations.TryGetValue(profile.NameId, out var calibration) &&
            calibration is { SampleCount: > 0 })
        {
            sampleCount = calibration.SampleCount;
            source = AggroRangeSource.LocalCalibration;
            return calibration.Resolve(configuredFallback);
        }

        sampleCount = 0;
        source = AggroRangeSource.Fallback;
        return configuredFallback;
    }
}

