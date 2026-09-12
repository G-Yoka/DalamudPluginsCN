namespace CrescentCompass.Core;

internal enum DemiatmaColor
{
    Azure,
    Verdigris,
    Malachite,
    Realgar,
    Purple,
    Yellow
}

/// <summary>
/// Confirmed South Horn demiatma drops. North Horn uses phantom dispellers
/// instead, so it deliberately has no demiatma labels here.
/// </summary>
public static class OccultEventRewardCatalog
{
    private static readonly IReadOnlyDictionary<(uint TerritoryId, uint EventId), SoulShardReward> SoulShards =
        new Dictionary<(uint, uint), SoulShardReward>
        {
            [(PotCandidateCatalog.SouthHornTerritoryId, 34)] = new("[游]", "游侠"),
            [(PotCandidateCatalog.SouthHornTerritoryId, 35)] = new("[狂]", "狂战士"),
            [(PotCandidateCatalog.SouthHornTerritoryId, 42)] = new("[预]", "预言师"),
            [(PotCandidateCatalog.NorthHornTerritoryId, 57)] = new("[死]", "死灵法师"),
            [(PotCandidateCatalog.NorthHornTerritoryId, 59)] = new("[青魔]", "青魔法师")
        };

    private static readonly IReadOnlyDictionary<uint, DemiatmaColor> SouthHornFates =
        new Dictionary<uint, DemiatmaColor>
        {
            [1962] = DemiatmaColor.Azure,
            [1963] = DemiatmaColor.Azure,
            [1964] = DemiatmaColor.Yellow,
            [1965] = DemiatmaColor.Realgar,
            [1966] = DemiatmaColor.Malachite,
            [1967] = DemiatmaColor.Realgar,
            [1968] = DemiatmaColor.Verdigris,
            [1969] = DemiatmaColor.Verdigris,
            [1970] = DemiatmaColor.Azure,
            [1971] = DemiatmaColor.Yellow,
            [1972] = DemiatmaColor.Purple,
            [1976] = DemiatmaColor.Yellow,
            [1977] = DemiatmaColor.Verdigris
        };

    private static readonly IReadOnlyDictionary<uint, DemiatmaColor> SouthHornCriticalEncounters =
        new Dictionary<uint, DemiatmaColor>
        {
            [33] = DemiatmaColor.Azure,
            [34] = DemiatmaColor.Yellow,
            [35] = DemiatmaColor.Azure,
            [36] = DemiatmaColor.Azure,
            [37] = DemiatmaColor.Verdigris,
            [38] = DemiatmaColor.Malachite,
            [39] = DemiatmaColor.Malachite,
            [40] = DemiatmaColor.Purple,
            [41] = DemiatmaColor.Realgar,
            [42] = DemiatmaColor.Purple,
            [43] = DemiatmaColor.Realgar,
            [44] = DemiatmaColor.Yellow,
            [45] = DemiatmaColor.Realgar,
            [46] = DemiatmaColor.Purple,
            [47] = DemiatmaColor.Malachite
        };

    public static string FateTag(uint territoryId, uint fateId) =>
        TryGet(SouthHornFates, territoryId, fateId, out var color) ? DisplayTag(color) : string.Empty;

    public static string CriticalEncounterTag(uint territoryId, uint eventId) =>
        TryGet(SouthHornCriticalEncounters, territoryId, eventId, out var color) ? DisplayTag(color) : string.Empty;

    public static bool TryGetSoulShard(uint territoryId, uint eventId, out SoulShardReward reward) =>
        SoulShards.TryGetValue((territoryId, eventId), out reward);

    private static bool TryGet(
        IReadOnlyDictionary<uint, DemiatmaColor> source,
        uint territoryId,
        uint eventId,
        out DemiatmaColor color)
    {
        if (territoryId == PotCandidateCatalog.SouthHornTerritoryId && source.TryGetValue(eventId, out color))
            return true;

        color = default;
        return false;
    }

    private static string DisplayTag(DemiatmaColor color) => color switch
    {
        DemiatmaColor.Azure     => "[青]",
        DemiatmaColor.Verdigris => "[碧]",
        DemiatmaColor.Malachite => "[绿]",
        DemiatmaColor.Realgar   => "[橙]",
        DemiatmaColor.Purple    => "[紫]",
        DemiatmaColor.Yellow    => "[黄]",
        _                       => string.Empty
    };
}

public readonly record struct SoulShardReward(string Tag, string JobName);
