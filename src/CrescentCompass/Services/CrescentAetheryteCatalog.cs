using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CrescentCompass.Services;

public readonly record struct CrescentAetheryte(uint DataId, Vector3 Position, byte Index, string FallbackName);

public static class CrescentAetheryteCatalog
{
    private static readonly CrescentAetheryte[] SouthHorn =
    [
        new(4927, new(834f, 73f, -694.6f), 0, "远征军总部"),
        new(4928, new(-169f, 6.5f, -609.1f), 1, "流浪者营地"),
        new(4929, new(-354.4f, 100f, -120.2f), 2, "水晶化洞窟"),
        new(4930, new(302.9f, 103f, 305.4f), 3, "古树湿原"),
        new(4947, new(-384.9f, 97.4f, 277.1f), 4, "岩石沼泽")
    ];

    private static readonly CrescentAetheryte[] NorthHorn =
    [
        new(5571, new(881.1f, 258.5f, 882.2f), 0, "北部总部"),
        new(5576, new(454f, 70f, 530.6f), 1, "卡纳克王冠"),
        new(5572, new(358.2f, 45.1f, -557.3f), 2, "沉没圣所"),
        new(5573, new(-549.2f, 67.2f, 597.2f), 3, "悬空石工场"),
        new(5574, new(-386.6f, 39.2f, -437.6f), 4, "腐朽边地"),
        new(5575, new(-15.7f, 2.1f, -44.5f), 5, "不净村落")
    ];

    public static IReadOnlyList<CrescentAetheryte> ForTerritory(uint territoryId) => territoryId switch
    {
        Core.PotCandidateCatalog.SouthHornTerritoryId => SouthHorn,
        Core.PotCandidateCatalog.NorthHornTerritoryId => NorthHorn,
        _ => []
    };

    public static string Name(CrescentAetheryte aetheryte, IDataManager dataManager)
    {
        var name = dataManager.GetExcelSheet<PlaceName>().GetRowOrDefault(aetheryte.DataId)?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? aetheryte.FallbackName : name;
    }
}
