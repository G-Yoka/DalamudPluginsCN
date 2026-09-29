using System.Reflection;
using System.Text.Json;
using CrescentCompass.Configuration;
using CrescentCompass.Core;

namespace CrescentCompass.Services;

public static class BuiltInNavigationRouteLibrary
{
    private const string ResourceName = "CrescentCompass.Data.BuiltInNavigationRoutes.json";
    private static readonly Lazy<IReadOnlyList<CustomNavigationRoute>> LazyRoutes = new(Load);

    public static IReadOnlyList<CustomNavigationRoute> Routes => LazyRoutes.Value;

    public static bool IsBuiltIn(CustomNavigationRoute route) =>
        route.Id.StartsWith("builtin-", StringComparison.Ordinal);

    public static int RemovePromotedUserRouteDuplicates(List<CustomNavigationRoute> userRoutes) =>
        userRoutes.RemoveAll(userRoute => Routes.Any(builtInRoute => RoutesAreIdentical(userRoute, builtInRoute)));

    public static bool RoutesAreIdentical(CustomNavigationRoute left, CustomNavigationRoute right)
    {
        if (left.TerritoryId != right.TerritoryId ||
            left.SourceAetheryteDataId != right.SourceAetheryteDataId ||
            left.Kind != right.Kind || left.EventId != right.EventId ||
            left.Points.Count != right.Points.Count)
            return false;

        for (var index = 0; index < left.Points.Count; index++)
        {
            var leftPoint = left.Points[index];
            var rightPoint = right.Points[index];
            if (leftPoint.X != rightPoint.X || leftPoint.Y != rightPoint.Y || leftPoint.Z != rightPoint.Z ||
                leftPoint.Action != rightPoint.Action)
                return false;
        }

        return true;
    }

    private static IReadOnlyList<CustomNavigationRoute> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null) return [];
            var routes = JsonSerializer.Deserialize<List<CustomNavigationRoute>>(stream) ?? [];
            routes.RemoveAll(route =>
                !IsBuiltIn(route) ||
                route.TerritoryId is not PotCandidateCatalog.SouthHornTerritoryId and
                    not PotCandidateCatalog.NorthHornTerritoryId ||
                route.SourceAetheryteDataId == 0 || route.EventId == 0 ||
                !Enum.IsDefined(route.Kind) || route.Points == null || route.Points.Count < 2 ||
                route.Points.Any(point =>
                    !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)));
            return routes;
        }
        catch
        {
            return [];
        }
    }
}
