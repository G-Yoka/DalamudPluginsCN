using System.Numerics;

namespace CrescentCompass.Core;

internal readonly record struct NorthHornAggroZone(
    ulong GameObjectId,
    uint NameId,
    Vector3 Center,
    float Radius,
    AggroSenseType SenseType = AggroSenseType.Unknown,
    float Rotation = 0f,
    float EdgeRange = OccultCrescentMonsterCatalog.DefaultFallbackEdgeRange,
    int SampleCount = 0,
    AggroRangeSource RangeSource = AggroRangeSource.Fallback,
    float SightHalfAngleDegrees = 180f,
    float SightConfidence = 0f);

internal static class NorthHornAggroAvoidance
{
    private const float Clearance = 0.8f;
    private const float Epsilon = 0.05f;
    private const int MaxDetours = 24;
    private const int MaxPathPoints = 512;
    private const float ArcStep = MathF.PI / 8f;

    public static bool TryCreateSafePath(
        IReadOnlyList<Vector3> source,
        IReadOnlyList<NorthHornAggroZone> zones,
        float verticalTolerance,
        Func<Vector3, Vector3?> project,
        out List<Vector3> result)
    {
        result = RemoveDuplicates(source);
        if (result.Count < 2 || zones.Count == 0) return result.Count >= 2;

        var destination = result[^1];
        var relevant = zones
            .Where(zone => DistanceXZ(zone.Center, destination) >= zone.Radius - Epsilon)
            .ToArray();

        for (var count = 0; count < MaxDetours; count++)
        {
            if (!TryFindBlockedSegment(result, relevant, verticalTolerance, out var index, out var zone))
                return result.Count <= MaxPathPoints;

            if (!TryBuildDetour(result[index], result[index + 1], zone, relevant, verticalTolerance, project, out var detour))
                return false;

            result.InsertRange(index + 1, detour);
            result = RemoveDuplicates(result);
            if (result.Count > MaxPathPoints) return false;
        }

        return IsPathClear(result, relevant, verticalTolerance);
    }

    public static bool IsPathClear(
        IReadOnlyList<Vector3> path,
        IReadOnlyList<NorthHornAggroZone> zones,
        float verticalTolerance) =>
        !TryFindBlockedSegment(path, zones, verticalTolerance, out _, out _);

    private static bool TryFindBlockedSegment(
        IReadOnlyList<Vector3> path,
        IReadOnlyList<NorthHornAggroZone> zones,
        float verticalTolerance,
        out int segmentIndex,
        out NorthHornAggroZone blocked)
    {
        for (var index = 0; index + 1 < path.Count; index++)
        foreach (var zone in zones)
        {
            if (!SegmentEntersZone(path[index], path[index + 1], zone, verticalTolerance)) continue;
            segmentIndex = index;
            blocked = zone;
            return true;
        }

        segmentIndex = -1;
        blocked = default;
        return false;
    }

    private static bool SegmentEntersZone(
        Vector3 start,
        Vector3 end,
        NorthHornAggroZone zone,
        float verticalTolerance)
    {
        var minY = Math.Min(start.Y, end.Y) - verticalTolerance;
        var maxY = Math.Max(start.Y, end.Y) + verticalTolerance;
        if (zone.Center.Y < minY || zone.Center.Y > maxY) return false;

        var startDistance = DistanceXZ(start, zone.Center);
        var closest = DistanceToSegmentXZ(zone.Center, start, end);
        if (closest >= zone.Radius - Epsilon) return false;

        if (zone.SightHalfAngleDegrees < 179.5f)
        {
            var segmentLength = DistanceXZ(start, end);
            var stepLength = Math.Clamp(zone.Radius / 16f, 0.35f, 1f);
            var steps = Math.Clamp((int)MathF.Ceiling(segmentLength / stepLength), 1, 384);
            var entersSightCone = false;
            for (var step = 0; step <= steps; step++)
            {
                var point = Vector3.Lerp(start, end, (float)step / steps);
                if (DistanceXZ(point, zone.Center) >= zone.Radius - Epsilon ||
                    !AggroSightKnowledge.IsInsideSightCone(
                        point,
                        zone.Center,
                        zone.Rotation,
                        zone.SightHalfAngleDegrees))
                    continue;
                entersSightCone = true;
                break;
            }
            if (!entersSightCone) return false;
        }

        if (startDistance < zone.Radius - Epsilon && IsInsideZone(start, zone))
            return closest + Epsilon < startDistance;

        return true;
    }

    private static bool IsInsideZone(Vector3 point, NorthHornAggroZone zone) =>
        DistanceXZ(point, zone.Center) < zone.Radius - Epsilon &&
        (zone.SightHalfAngleDegrees >= 179.5f ||
         AggroSightKnowledge.IsInsideSightCone(point, zone.Center, zone.Rotation, zone.SightHalfAngleDegrees));

    private static bool TryBuildDetour(
        Vector3 start,
        Vector3 end,
        NorthHornAggroZone zone,
        IReadOnlyList<NorthHornAggroZone> allZones,
        float verticalTolerance,
        Func<Vector3, Vector3?> project,
        out List<Vector3> detour)
    {
        detour = [];
        var routeRadius = zone.Radius + Clearance;
        var startDistance = DistanceXZ(start, zone.Center);
        if (startDistance < zone.Radius - Epsilon)
        {
            var direction = NormalizeXZ(start - zone.Center);
            if (direction == Vector3.Zero) direction = Vector3.UnitX;
            var exit = project(new(
                zone.Center.X + direction.X * routeRadius,
                start.Y,
                zone.Center.Z + direction.Z * routeRadius));
            if (exit == null) return false;
            detour.Add(exit.Value);
            return true;
        }

        var endDistance = DistanceXZ(end, zone.Center);
        if (endDistance <= zone.Radius + Epsilon) return false;

        var startBase = MathF.Atan2(start.Z - zone.Center.Z, start.X - zone.Center.X);
        var endBase = MathF.Atan2(end.Z - zone.Center.Z, end.X - zone.Center.X);
        var startOffset = MathF.Acos(Math.Clamp(routeRadius / Math.Max(routeRadius, startDistance), -1f, 1f));
        var endOffset = MathF.Acos(Math.Clamp(routeRadius / Math.Max(routeRadius, endDistance), -1f, 1f));
        List<Vector3>? best = null;
        var bestLength = float.MaxValue;

        foreach (var startAngle in new[] { startBase - startOffset, startBase + startOffset })
        foreach (var endAngle in new[] { endBase - endOffset, endBase + endOffset })
        foreach (var direction in new[] { -1f, 1f })
        {
            var delta = DirectedDelta(startAngle, endAngle, direction);
            var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / ArcStep));
            var candidate = new List<Vector3>(steps + 1);
            var valid = true;
            for (var step = 0; step <= steps; step++)
            {
                var progress = (float)step / steps;
                var angle = startAngle + delta * progress;
                var point = project(new(
                    zone.Center.X + MathF.Cos(angle) * routeRadius,
                    start.Y + (end.Y - start.Y) * progress,
                    zone.Center.Z + MathF.Sin(angle) * routeRadius));
                if (point == null || DistanceXZ(point.Value, zone.Center) < zone.Radius)
                {
                    valid = false;
                    break;
                }
                candidate.Add(point.Value);
            }

            if (!valid) continue;
            var complete = new List<Vector3>(candidate.Count + 2) { start };
            complete.AddRange(candidate);
            complete.Add(end);
            if (!IsPathClear(complete, allZones, verticalTolerance)) continue;
            var length = PathLength(complete);
            if (length >= bestLength) continue;
            bestLength = length;
            best = candidate;
        }

        if (best == null) return false;
        detour = best;
        return true;
    }

    private static float DirectedDelta(float start, float end, float direction)
    {
        var delta = MathF.IEEERemainder(end - start, MathF.Tau);
        if (direction > 0f && delta < 0f) delta += MathF.Tau;
        if (direction < 0f && delta > 0f) delta -= MathF.Tau;
        return delta;
    }

    private static float DistanceToSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
    {
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var lengthSquared = dx * dx + dz * dz;
        if (lengthSquared <= float.Epsilon) return DistanceXZ(point, start);
        var t = Math.Clamp(((point.X - start.X) * dx + (point.Z - start.Z) * dz) / lengthSquared, 0f, 1f);
        var x = point.X - (start.X + dx * t);
        var z = point.Z - (start.Z + dz * t);
        return MathF.Sqrt(x * x + z * z);
    }

    private static float DistanceXZ(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return MathF.Sqrt(x * x + z * z);
    }

    private static Vector3 NormalizeXZ(Vector3 value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Z * value.Z);
        return length <= float.Epsilon ? Vector3.Zero : new(value.X / length, 0f, value.Z / length);
    }

    private static float PathLength(IReadOnlyList<Vector3> path)
    {
        var result = 0f;
        for (var index = 1; index < path.Count; index++) result += Vector3.Distance(path[index - 1], path[index]);
        return result;
    }

    private static List<Vector3> RemoveDuplicates(IReadOnlyList<Vector3> path)
    {
        var result = new List<Vector3>(path.Count);
        foreach (var point in path)
            if (result.Count == 0 || Vector3.DistanceSquared(result[^1], point) > 0.01f)
                result.Add(point);
        return result;
    }
}

