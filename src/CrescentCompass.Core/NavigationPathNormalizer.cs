using System.Numerics;

namespace CrescentCompass.Core;

public static class NavigationPathNormalizer
{
    public static List<Vector3> TrimPassedPrefix(
        Vector3 player,
        IReadOnlyList<Vector3> path,
        float corridorRadius = 3f,
        float reachedRadius = 1.5f)
    {
        if (path.Count == 0) return [];
        if (path.Count == 1) return [path[0]];

        var bestSegment = -1;
        var bestDistanceSquared = float.MaxValue;
        for (var index = 0; index + 1 < path.Count; index++)
        {
            var distanceSquared = DistanceToSegmentSquared(player, path[index], path[index + 1]);
            // At a shared or overlapping segment, prefer the later segment so a
            // restarted route cannot retain an already-passed loop prefix.
            if (distanceSquared > bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            bestSegment = index;
        }

        if (bestSegment >= 0 && bestDistanceSquared <= corridorRadius * corridorRadius)
        {
            var trimmed = path.Skip(bestSegment + 1).ToList();
            while (trimmed.Count > 1 && Vector3.DistanceSquared(player, trimmed[0]) <= reachedRadius * reachedRadius)
                trimmed.RemoveAt(0);
            return trimmed;
        }

        var result = path.ToList();
        while (result.Count > 1 && Vector3.DistanceSquared(player, result[0]) <= reachedRadius * reachedRadius)
            result.RemoveAt(0);
        return result;
    }

    private static float DistanceToSegmentSquared(Vector3 point, Vector3 start, Vector3 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= float.Epsilon) return Vector3.DistanceSquared(point, start);
        var progress = Math.Clamp(Vector3.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector3.DistanceSquared(point, start + segment * progress);
    }
}
