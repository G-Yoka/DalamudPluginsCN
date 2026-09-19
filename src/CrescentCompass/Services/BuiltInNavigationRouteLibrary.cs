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
