using System.Numerics;

namespace CrescentCompass.Core;

/// <summary>
/// Dedicated Occult Crescent Magic Pot coffer locations. These are intentionally
/// separate from LVD_FLD_treasure, which contains ordinary bronze/silver coffers.
/// Source baseline: zhui-zi/OccultOverlay 9a472535d56f891d8c218b512ecefd791f3c2304.
/// </summary>
public static class PotCandidateCatalog
{
    public const uint NorthHornTerritoryId = 1346;
    public const uint SouthHornTerritoryId = 1252;
    public const int ExpectedInitialCandidateCount = 60;
    public const int ExpectedSecondChanceCandidateCount = 20;

    private static readonly (float X, float Z)[] NorthHornNorthPotPoints =
    [
        (714.698f, 262.6901f), (-455.989f, -365.5418f), (593f, 34f),
        (-251.781f, -864.3828f), (151.9998f, -842.0175f), (385f, -177f),
        (452.6f, -310.3f), (-223.8233f, -353.9438f), (1.768392f, -872.2798f),
        (-252.1626f, -879.5855f), (440.298f, -926.5872f), (782.4979f, -56.4099f),
        (-190f, -763f), (939.2178f, -273.1175f), (912.2978f, -461.5099f),
        (889.2178f, 155.9825f), (32.4f, -777.3f), (-530f, -58f),
        (948.5978f, -567.0099f), (830.0979f, -148.9099f), (928.8978f, -332.8099f),
        (-498.7f, 128.9f), (546.56f, 143.3104f), (927.0178f, -155.2175f),
        (929.4178f, -1.817501f), (-86f, -737f), (321.198f, -889.8872f),
        (-536.1014f, 149.8447f), (-596f, -285f), (810.8979f, -278.8099f)
    ];

    private static readonly (float X, float Z)[] NorthHornSouthPotPoints =
    [
        (47.6f, -218.3f), (-172.6f, 103.2f), (-330f, -628f),
        (-184.5137f, 667.8036f), (-747.4032f, -492.1095f), (-512f, -389f),
        (52f, 552f), (-127f, 808.4f), (28.10088f, -16.69861f),
        (-109.5452f, -210.1855f), (-975.4507f, -526.2878f), (-834f, -587.4f),
        (190.3622f, -204.7095f), (-259.6f, 56.9f), (210f, 916f),
        (-628.4385f, -449.5009f), (-88.43135f, 4.891054f), (-15.89468f, -20.29277f),
        (-586.3f, -715.2f), (237.9156f, 309.4334f), (194.2296f, 352.9844f),
        (0.9425046f, 623.2599f), (-339.8588f, 861.5197f), (71.10001f, 942.3f),
        (11.98766f, 795.707f), (93.4f, -114.3f), (-113.4943f, -74.15943f),
        (-853.493f, -323.8983f), (-960f, -425.8f), (-269.6122f, 875.6997f)
    ];

    private static readonly (float X, float Z)[] NorthHornSecondChancePoints =
    [
        (782.8808f, -611.7695f), (925.6533f, -906.2195f), (909f, -961.8f),
        (-661f, 937f), (-527f, 834f), (-631.9453f, 808.8979f),
        (-809f, -879f), (671.2f, -550.1f), (701f, -945f),
        (-623f, 883f), (-585f, 842f), (-656.9f, -799.3f),
        (-839.9977f, 740f), (-487.8f, -953.2f), (-603f, -869f),
        (-637.2283f, -950.4841f), (-866f, -775f), (626.3f, -844.9f),
        (943.4631f, -879.5159f), (-449.6f, -967.0001f)
    ];

    private static readonly (float X, float Z)[] SouthHornNorthPotPoints =
    [
        (571.58f, -813.16f), (662.44f, 161.13f), (606.46f, 184.85f),
        (-312.28f, -35.25f), (587.7f, -545.82f), (891.26f, -20.67f),
        (878.11f, -91.11f), (803.66f, -354.18f), (341.44f, 194.75f),
        (570.24f, 272.17f), (-216.37f, -510.14f), (684.42f, -165.48f),
        (-188.17f, -717.2f), (-476.3f, -86.7f), (80.2f, 391.23f),
        (-534.7f, -651.62f), (-165.24f, 437.45f), (330.87f, -654.53f),
        (-333.34f, -861.17f), (-313.29f, 70.76f), (-459.17f, 5.05f),
        (-54.7f, 405.03f), (-382.44f, -378.35f), (263.26f, 326.68f),
        (224.72f, 518.67f), (19.74f, -420.98f), (705.27f, 358.67f),
        (-660.53f, -216.77f), (-324.27f, 203.2f), (-386.59f, -461.1f)
    ];

    private static readonly (float X, float Z)[] SouthHornSouthPotPoints =
    [
        (-195.44f, -287.89f), (74.73f, -394.13f), (-386.44f, -221.78f),
        (-554.61f, -309.12f), (107.06f, 146.71f), (825.95f, 772.41f),
        (-836.76f, 597.29f), (67.45f, 745.87f), (69.71f, -239.06f),
        (301.87f, 70.6f), (-38.98f, -175.46f), (-60.73f, 828.5f),
        (17.6f, 674.62f), (393.27f, 844.69f), (393.02f, -124.17f),
        (-798.79f, -4.82f), (440.84f, 876.41f), (-734.14f, 683.72f),
        (423.35f, 578.9f), (200.12f, 624.23f), (-603.35f, 858.68f),
        (-829.6f, 66.83f), (-645.3f, -73.55f), (-836.16f, 770.28f),
        (-676.62f, 1.53f), (-713.68f, 710.08f), (781.25f, 560.07f),
        (-746.13f, 828.88f), (-730.54f, -371.48f), (-810.83f, -226.83f)
    ];

    private static readonly (float X, float Z)[] SouthHornSecondChancePoints =
    [
        (-676.46f, -769.8f), (-823.92f, 677.69f), (-886.47f, 712.5f),
        (-625.78f, 810.87f), (-813.99f, -663.36f), (-842.9f, -125.06f),
        (-680.03f, 739.91f), (-793.06f, -777.31f), (-708.68f, 669.57f),
        (-718.04f, -633.88f), (-868.85f, -59.45f), (-803.52f, -602.75f),
        (-732.2f, 828.85f), (-659.12f, -508.8f), (-786f, 790.59f),
        (-840.88f, -250.27f), (-708.69f, -139.33f), (-796.66f, -228.93f),
        (-776.63f, -486.98f), (-758.81f, -183.16f)
    ];

    public static PotCandidateCatalogResult Read(uint territoryId, float fallbackY = 0f)
    {
        var (northPoints, southPoints, secondChancePoints, idOffset) = territoryId switch
        {
            NorthHornTerritoryId => (NorthHornNorthPotPoints, NorthHornSouthPotPoints, NorthHornSecondChancePoints, 0u),
            SouthHornTerritoryId => (SouthHornNorthPotPoints, SouthHornSouthPotPoints, SouthHornSecondChancePoints, 10_000u),
            _ => ([], [], [], 0u)
        };
        if (northPoints.Length == 0)
            return new([], [], "当前仅支持新月岛北部与南部。", string.Empty);

        var initial = CreateCandidates(northPoints, idOffset + 1_000, fallbackY)
            .Concat(CreateCandidates(southPoints, idOffset + 2_000, fallbackY))
            .ToArray();
        var secondChance = CreateCandidates(secondChancePoints, idOffset + 3_000, fallbackY);
        var warning = initial.Length == ExpectedInitialCandidateCount &&
                      secondChance.Length == ExpectedSecondChanceCandidateCount
                          ? string.Empty
                          : $"魔法罐候选数据数量异常：首次 {initial.Length}，第二次机会 {secondChance.Length}。";

        return new(
            initial,
            secondChance,
            warning,
            "OccultOverlay mapPoints.js @ 9a472535d56f891d8c218b512ecefd791f3c2304");
    }

    private static PotCandidate[] CreateCandidates(
        IReadOnlyList<(float X, float Z)> points,
        uint idBase,
        float fallbackY) => points
        .Select((point, index) => new PotCandidate(
            idBase + (uint)index,
            new Vector3(point.X, fallbackY, point.Z)))
        .ToArray();
}

public sealed record PotCandidateCatalogResult(
    IReadOnlyList<PotCandidate> InitialCandidates,
    IReadOnlyList<PotCandidate> SecondChanceCandidates,
    string Warning,
    string Source)
{
    public bool IsAvailable => InitialCandidates.Count > 0 && SecondChanceCandidates.Count > 0;
}

