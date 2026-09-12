using System.Numerics;

namespace CrescentCompass.Core;

// Community spawn research, checked 2026-09-12:
// https://ffxiv.consolegameswiki.com/wiki/Occult_Crescent_FATEs
// https://redfreshet.com/ff14-north-horn-ce-spawn-conditions/
// https://ff14.huijiwiki.com/wiki/%E7%B4%A7%E6%80%A5%E9%81%AD%E9%81%87%E6%88%98/%E5%8D%97%E5%BE%81%E4%B9%8B%E7%AB%A0
// DynamicEvent and BNpcName IDs cross-checked against v2.xivapi.com.
// Coordinates describe a search area, not a guaranteed live monster position.
public sealed record CeSpawnDefinition(uint Id, uint TerritoryId, string EnglishName,
    uint MobNameId = 0, string MobFallback = "", int Level = 0, Vector2 SearchPosition = default,
    bool CanSpawnNaturally = false);

public static class CeSpawnCatalog
{
    public static readonly IReadOnlyList<CeSpawnDefinition> All =
    [
        new(33, 1252, "Scourge of the Mind", 13879, "Crescent Monk", 15, new(27, 34)),
        new(34, 1252, "The Black Regiment", 13911, "Crescent Panther", 14, new(29, 29), true),
        new(35, 1252, "The Unbridled", 13901, "Crescent Demon Pawn", 17, new(30, 36), true),
        new(36, 1252, "Crawling Death"),
        new(37, 1252, "Calamity Bound", 13875, "Crescent Inkstain", 20, new(13, 35)),
        new(38, 1252, "Trial by Claw", 13903, "Crescent Claw", 19, new(13, 31), true),
        new(39, 1252, "From Times Bygone", 13895, "Crescent Byblos", 13, new(7, 25)),
        new(40, 1252, "Company of Stone"),
        new(41, 1252, "Shark Attack", 13913, "Crescent Petalodite", 7, new(17, 9)),
        new(42, 1252, "On the Hunt", 13876, "Crescent Fan", 5, new(36, 20)),
        new(43, 1252, "With Extreme Prejudice"),
        new(44, 1252, "Noise Complaint", 13884, "Crescent Garula", 1, new(33, 7)),
        new(45, 1252, "Cursed Concern", 13902, "Crescent Cetus", 7, new(22, 7), true),
        new(46, 1252, "Eternal Watch"),
        new(47, 1252, "Flame of Dusk", 13935, "Crescent Harpuia", 11, new(11, 17), true),
        new(49, 1346, "Many Mouths to Feed", 14908, "Crescent Wamoura", 46, new(3, 3)),
        new(50, 1346, "Doubled Trouble", 14896, "Crescent Blackguard", 39, new(20, 19)),
        new(51, 1346, "Quarried Away"),
        new(52, 1346, "Forbidden Folios"),
        new(53, 1346, "Cursed Resurgence", 14887, "Crescent Big Horn", 34, new(5, 27)),
        new(54, 1346, "Imbalanced Diet"),
        new(55, 1346, "Web of Terror", 14897, "Crescent Hellhound", 39, new(24, 18)),
        new(56, 1346, "A Beast Unleashed"),
        new(57, 1346, "Dark Artistry"),
        new(58, 1346, "Familiar Tactics"),
        new(59, 1346, "Appalling Behavior"),
        new(60, 1346, "Tiny Terror"),
        new(61, 1346, "Lost on the Wind"),
        new(62, 1346, "Ahead of the Competition"),
        new(63, 1346, "Accept No Imitators")
    ];
}

// One alert per observed appearance. Battle-only observations are remembered
// without issuing an invitation to an encounter that can no longer be joined.
public sealed class CeAppearanceTracker
{
    private readonly HashSet<uint> seen = [];
    public void Reset() => seen.Clear();
    public bool Observe(uint id, bool joinable) => seen.Add(id) && joinable;
    public void Retain(IEnumerable<uint> activeIds) => seen.IntersectWith(activeIds);
}
