namespace CrescentCompass.Core;

public enum CrystalAccessMode
{
    Nearby,
    DemiReturn,
    Walk
}

public readonly record struct TransferRouteEstimate(float Seconds, CrystalAccessMode AccessMode);

public static class RouteTimeEstimator
{
    public const float WalkingSpeed = 6f;
    public const float MountedSpeed = 9f;
    public const float MountDistance = 40f;

    public static float MovementSeconds(float pathDistance, bool initiallyMounted, float mountSeconds)
    {
        if (!float.IsFinite(pathDistance)) return float.PositiveInfinity;
        if (pathDistance <= 0f) return 0f;
        if (initiallyMounted) return pathDistance / MountedSpeed;
        return pathDistance < MountDistance
            ? pathDistance / WalkingSpeed
            : mountSeconds + pathDistance / MountedSpeed;
    }

    public static TransferRouteEstimate TransferSeconds(
        float destinationDistance,
        float approachDistance,
        bool initiallyMounted,
        bool nearCrystal,
        bool demiReturnAvailable,
        float demiReturnSeconds,
        float crystalTransferSeconds,
        float mountSeconds,
        float dismountSeconds)
    {
        if (!float.IsFinite(destinationDistance))
            return new(float.PositiveInfinity, CrystalAccessMode.Walk);

        float accessSeconds;
        CrystalAccessMode accessMode;
        if (nearCrystal)
        {
            accessSeconds = initiallyMounted ? dismountSeconds : 0f;
            accessMode = CrystalAccessMode.Nearby;
        }
        else
        {
            var demiSeconds = demiReturnAvailable
                ? (initiallyMounted ? dismountSeconds : 0f) + demiReturnSeconds
                : float.PositiveInfinity;
            var walkSeconds = float.IsFinite(approachDistance)
                ? MovementSeconds(approachDistance, initiallyMounted, mountSeconds) +
                  (initiallyMounted || approachDistance >= MountDistance ? dismountSeconds : 0f)
                : float.PositiveInfinity;
            accessMode = demiSeconds <= walkSeconds ? CrystalAccessMode.DemiReturn : CrystalAccessMode.Walk;
            accessSeconds = Math.Min(demiSeconds, walkSeconds);
        }

        return new(accessSeconds + crystalTransferSeconds +
            MovementSeconds(destinationDistance, false, mountSeconds), accessMode);
    }

    public static bool PreferTransfer(float directSeconds, float transferSeconds, float minimumSavingSeconds) =>
        !float.IsFinite(directSeconds) ||
        float.IsFinite(transferSeconds) && transferSeconds + minimumSavingSeconds <= directSeconds;
}
