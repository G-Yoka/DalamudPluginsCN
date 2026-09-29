using CrescentCompass.Configuration;

namespace CrescentCompass.Services;

/// <summary>
/// Event-specific target priorities. Add enemy data IDs in kill order as encounters are verified.
/// Unknown enemies retain the normal maximum-HP and distance ordering.
/// </summary>
public static class EventTargetPriorityCatalog
{
    private static readonly IReadOnlyDictionary<(CustomNavigationRouteKind Kind, uint EventId), uint[]> Priorities =
        new Dictionary<(CustomNavigationRouteKind, uint), uint[]>();

    public static int Priority(CustomNavigationRouteKind kind, uint eventId, uint enemyDataId)
    {
        if (!Priorities.TryGetValue((kind, eventId), out var orderedEnemyIds)) return int.MaxValue;
        var index = Array.IndexOf(orderedEnemyIds, enemyDataId);
        return index < 0 ? int.MaxValue : index;
    }
}
