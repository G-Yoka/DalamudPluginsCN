using System.Numerics;

namespace CrescentCompass.Core;

// Fixed FATE centers for the two Occult Crescent field areas. Row IDs are
// cross-checked against the game Fate sheet; coordinates are map coordinates.
public sealed record FateDefinition(uint Id, uint TerritoryId, string EnglishName, Vector2 MapPosition);

public static class FateCatalog
{
    public static readonly IReadOnlyList<FateDefinition> All =
    [
        new(1962, PotCandidateCatalog.SouthHornTerritoryId, "Rough Waters", new(24.6f, 34.8f)),
        new(1963, PotCandidateCatalog.SouthHornTerritoryId, "The Golden Guardian", new(28.8f, 31.2f)),
        new(1964, PotCandidateCatalog.SouthHornTerritoryId, "King of the Crescent", new(17.0f, 26.5f)),
        new(1965, PotCandidateCatalog.SouthHornTerritoryId, "The Winged Terror", new(10.4f, 9.7f)),
        new(1966, PotCandidateCatalog.SouthHornTerritoryId, "An Unending Duty", new(17.0f, 22.3f)),
        new(1967, PotCandidateCatalog.SouthHornTerritoryId, "Brain Drain", new(20.6f, 15.1f)),
        new(1968, PotCandidateCatalog.SouthHornTerritoryId, "A Delicate Balance", new(14.1f, 34.5f)),
        new(1969, PotCandidateCatalog.SouthHornTerritoryId, "Sworn to Soil", new(9.5f, 27.9f)),
        new(1970, PotCandidateCatalog.SouthHornTerritoryId, "A Prying Eye", new(20.1f, 32.7f)),
        new(1971, PotCandidateCatalog.SouthHornTerritoryId, "Fatal Allure", new(22.9f, 27.0f)),
        new(1972, PotCandidateCatalog.SouthHornTerritoryId, "Serving Darkness", new(29.6f, 21.0f)),
        new(1976, PotCandidateCatalog.SouthHornTerritoryId, "Persistent Pots", new(25.6f, 17.1f)),
        new(1977, PotCandidateCatalog.SouthHornTerritoryId, "Pleading Pots", new(11.9f, 32.0f)),

        new(2072, PotCandidateCatalog.NorthHornTerritoryId, "Daylight Pottery", new(26.2f, 11.6f)),
        new(2073, PotCandidateCatalog.NorthHornTerritoryId, "In a Pot of Bother", new(11.0f, 25.8f)),
        new(2074, PotCandidateCatalog.NorthHornTerritoryId, "Raging Thrall", new(35.8f, 25.7f)),
        new(2075, PotCandidateCatalog.NorthHornTerritoryId, "Eye to Eye", new(31.7f, 20.8f)),
        new(2076, PotCandidateCatalog.NorthHornTerritoryId, "Shoreline Showdown", new(23.3f, 30.8f)),
        new(2077, PotCandidateCatalog.NorthHornTerritoryId, "Waved Away", new(28.0f, 16.4f)),
        new(2078, PotCandidateCatalog.NorthHornTerritoryId, "Allure of the Occult", new(13.4f, 16.3f)),
        new(2079, PotCandidateCatalog.NorthHornTerritoryId, "Inconstant Gardener", new(18.0f, 11.7f)),
        new(2080, PotCandidateCatalog.NorthHornTerritoryId, "Territorial Dispute", new(19.6f, 38.7f)),
        new(2081, PotCandidateCatalog.NorthHornTerritoryId, "A Rotten Affair", new(12.5f, 5.4f)),
        new(2082, PotCandidateCatalog.NorthHornTerritoryId, "Gale-force Encounter", new(4.2f, 31.0f)),
        new(2083, PotCandidateCatalog.NorthHornTerritoryId, "Scale Model", new(8.2f, 20.1f)),
        new(2084, PotCandidateCatalog.NorthHornTerritoryId, "Thunderregnum", new(24.2f, 7.3f))
    ];
}
