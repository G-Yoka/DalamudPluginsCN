using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace CrescentCompass.Services;

internal static class FieldTreasureLayoutReader
{
    private const uint BronzeSgb = 1596;
    private const uint SilverSgb = 1597;

    public static IReadOnlyList<FieldTreasurePoint> Read(
        IDataManager dataManager,
        IPluginLog log,
        uint territoryId)
    {
        try
        {
            var territory = dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
            var background = territory?.Bg.ToString();
            if (string.IsNullOrWhiteSpace(background)) return [];
            var levelIndex = background.IndexOf("/level/", StringComparison.Ordinal);
            if (levelIndex < 0) return [];
            var levelPath = "bg/" + background[..(levelIndex + 1)] + "level/";

            if (territoryId == Core.PotCandidateCatalog.NorthHornTerritoryId)
                return ReadNorth(dataManager, levelPath);
            return ReadTyped(dataManager, levelPath);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Unable to read field treasure layout for territory {TerritoryId}.", territoryId);
            return [];
        }
    }

    private static IReadOnlyList<FieldTreasurePoint> ReadNorth(IDataManager dataManager, string levelPath)
    {
        var lgb = dataManager.GetFile<LgbFile>(levelPath + "planmap.lgb");
        if (lgb == null) return [];
        return lgb.Layers
            .Where(layer => string.Equals(layer.Name, "LVD_FLD_treasure", StringComparison.Ordinal))
            .SelectMany(layer => layer.InstanceObjects)
            .Where(instance => instance.AssetType == LayerEntryType.Treasure)
            .Select(instance => new FieldTreasurePoint(
                instance.InstanceId,
                new Vector3(
                    instance.Transform.Translation.X,
                    instance.Transform.Translation.Y,
                    instance.Transform.Translation.Z),
                FieldTreasureKind.Unknown))
            .Where(point => IsFinite(point.Position))
            .DistinctBy(point => point.Id)
            .OrderBy(point => point.Id)
            .ToArray();
    }

    private static IReadOnlyList<FieldTreasurePoint> ReadTyped(IDataManager dataManager, string levelPath)
    {
        var treasureSheet = dataManager.GetExcelSheet<Treasure>();
        var result = new List<FieldTreasurePoint>();
        foreach (var filename in new[] { "planevent.lgb", "planmap.lgb" })
        {
            var lgb = dataManager.GetFile<LgbFile>(levelPath + filename);
            if (lgb == null) continue;
            foreach (var instance in lgb.Layers
                         .Where(layer => string.Equals(layer.Name, "Field_Treasure", StringComparison.Ordinal))
                         .SelectMany(layer => layer.InstanceObjects))
            {
                if (instance.AssetType != LayerEntryType.Treasure ||
                    instance.Object is not LayerCommon.TreasureInstanceObject treasure)
                    continue;
                var row = treasureSheet.GetRowOrDefault(treasure.ParentData.BaseId);
                var sgb = row?.SGB.RowId ?? 0;
                var kind = sgb switch
                {
                    BronzeSgb => FieldTreasureKind.Bronze,
                    SilverSgb => FieldTreasureKind.Silver,
                    _ => FieldTreasureKind.Unknown
                };
                var position = new Vector3(
                    instance.Transform.Translation.X,
                    instance.Transform.Translation.Y,
                    instance.Transform.Translation.Z);
                if (IsFinite(position)) result.Add(new(instance.InstanceId, position, kind));
            }
        }
        return result.DistinctBy(point => point.Id).OrderBy(point => point.Id).ToArray();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public readonly record struct FieldTreasurePoint(uint Id, Vector3 Position, FieldTreasureKind Kind);
