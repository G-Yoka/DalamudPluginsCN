using System.Numerics;
using System.Text.RegularExpressions;

namespace CrescentCompass.Core;

public enum PotDirection
{
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    NorthWest
}

public enum PotDistanceBand
{
    VeryNear,
    Near,
    Far,
    VeryFar
}

public enum PotSessionStage
{
    Waiting,
    Predicting,
    AwaitingTreasure,
    Finished
}

public enum PotHintApplyResult
{
    NotHint,
    Duplicate,
    Accepted,
    Conflict,
    NoKnownCandidate
}

public readonly record struct PotCandidate(uint Id, Vector3 Position);

public readonly record struct PotHint(
    Vector3 Origin,
    PotDirection Direction,
    PotDistanceBand DistanceBand,
    float MinimumDistance,
    float MaximumDistance,
    string SourceText,
    long ReceivedAtMilliseconds)
{
    public const float SectorHalfAngleDegrees = 22.5f;
    public const float DistanceTolerance = 3f;
    public const float AngleToleranceDegrees = 2f;

    public Vector2 DirectionVector => Direction switch
    {
        PotDirection.North     => new(0f, -1f),
        PotDirection.NorthEast => Vector2.Normalize(new(1f, -1f)),
        PotDirection.East      => new(1f, 0f),
        PotDirection.SouthEast => Vector2.Normalize(new(1f, 1f)),
        PotDirection.South     => new(0f, 1f),
        PotDirection.SouthWest => Vector2.Normalize(new(-1f, 1f)),
        PotDirection.West      => new(-1f, 0f),
        PotDirection.NorthWest => Vector2.Normalize(new(-1f, -1f)),
        _                      => Vector2.Zero
    };

    public bool Contains(Vector3 candidate)
    {
        if (!PotPredictionSession.IsFinite(candidate)) return false;

        var offset = new Vector2(candidate.X - Origin.X, candidate.Z - Origin.Z);
        var distance = offset.Length();
        var minimum = Math.Max(0f, MinimumDistance - DistanceTolerance);
        var maximum = float.IsPositiveInfinity(MaximumDistance)
                          ? float.PositiveInfinity
                          : MaximumDistance + DistanceTolerance;

        if (distance < minimum || distance > maximum) return false;
        if (distance < 0.001f) return minimum <= 0f;

        var minimumDot = MathF.Cos(
            (SectorHalfAngleDegrees + AngleToleranceDegrees) * MathF.PI / 180f);
        return Vector2.Dot(offset / distance, DirectionVector) >= minimumDot;
    }
}

public sealed class PotPredictionSession
{
    private static readonly Regex HintPattern = new(
        "^财宝好像是在(?<direction>正北|东北|正东|东南|正南|西南|正西|西北)方向(?<distance>很近|不远|稍远|很远)的地方！$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const long DuplicateWindowMilliseconds = 750;
    private const float DuplicatePositionToleranceSquared = 0.01f;

    private readonly List<PotCandidate> universe = [];
    private readonly List<PotHint> acceptedHints = [];
    private readonly List<PotCandidate> candidates = [];

    public IReadOnlyList<PotCandidate> Universe => universe;
    public IReadOnlyList<PotHint> AcceptedHints => acceptedHints;
    public IReadOnlyList<PotCandidate> Candidates => candidates;
    public PotHint? ConflictingHint { get; private set; }
    public PotSessionStage Stage { get; private set; } = PotSessionStage.Waiting;
    public int Round { get; private set; } = 1;

    public void SetUniverse(IEnumerable<PotCandidate> source)
    {
        universe.Clear();
        universe.AddRange(source
            .Where(candidate => candidate.Id != 0 && IsFinite(candidate.Position))
            .DistinctBy(candidate => candidate.Id)
            .OrderBy(candidate => candidate.Id));
        Recalculate();
    }

    public PotHintApplyResult TryApply(
        string message,
        Vector3 origin,
        long receivedAtMilliseconds)
    {
        if (!TryParseHint(message, origin, receivedAtMilliseconds, out var hint))
            return PotHintApplyResult.NotHint;

        if (IsDuplicate(hint)) return PotHintApplyResult.Duplicate;

        var filtered = Filter(acceptedHints.Append(hint));
        if (universe.Count > 0 && filtered.Count == 0)
        {
            ConflictingHint = hint;
            Stage = PotSessionStage.Predicting;
            if (acceptedHints.Count == 0)
            {
                candidates.Clear();
                return PotHintApplyResult.NoKnownCandidate;
            }

            return PotHintApplyResult.Conflict;
        }

        acceptedHints.Add(hint);
        ConflictingHint = null;
        candidates.Clear();
        candidates.AddRange(filtered);
        Stage = PotSessionStage.Predicting;
        return PotHintApplyResult.Accepted;
    }

    public bool UndoLast()
    {
        if (ConflictingHint != null)
        {
            ConflictingHint = null;
            Recalculate();
            Stage = acceptedHints.Count == 0 ? PotSessionStage.Waiting : PotSessionStage.Predicting;
            return true;
        }

        if (acceptedHints.Count == 0) return false;
        acceptedHints.RemoveAt(acceptedHints.Count - 1);
        Recalculate();
        Stage = acceptedHints.Count == 0 ? PotSessionStage.Waiting : PotSessionStage.Predicting;
        return true;
    }

    public void MarkTreasureReported() => Stage = PotSessionStage.AwaitingTreasure;

    public void Finish() => Stage = PotSessionStage.Finished;

    public void StartNextRound()
    {
        Round++;
        ResetEvidence();
    }

    public void Reset()
    {
        Round = 1;
        ResetEvidence();
    }

    public static bool TryParseHint(
        string message,
        Vector3 origin,
        long receivedAtMilliseconds,
        out PotHint hint)
    {
        hint = default;
        if (string.IsNullOrWhiteSpace(message) || !IsFinite(origin)) return false;

        var match = HintPattern.Match(message.Trim());
        if (!match.Success ||
            !TryParseDirection(match.Groups["direction"].Value, out var direction) ||
            !TryParseDistance(match.Groups["distance"].Value, out var band, out var minimum, out var maximum))
            return false;

        hint = new(origin, direction, band, minimum, maximum, message.Trim(), receivedAtMilliseconds);
        return true;
    }

    public static bool IsSecondTreasureMessage(string text) =>
        text.Trim().Contains("似乎能够告知第二处财宝所在地", StringComparison.Ordinal);

    public static bool IsTreasureReportedMessage(string text) =>
        text.Trim().Contains("发现了财宝", StringComparison.Ordinal) &&
        !text.Trim().Contains("已经发现的财宝消失了", StringComparison.Ordinal);

    public static bool IsTerminalMessage(string text)
    {
        var value = text.Trim();
        return value.Contains("已经发现的财宝消失了", StringComparison.Ordinal) ||
               value.Contains("谢谢你的圣灵药", StringComparison.Ordinal) ||
               string.Equals(value, "不见了……", StringComparison.Ordinal) ||
               string.Equals(value, "耗尽了力量……", StringComparison.Ordinal);
    }

    public static bool IsFinite(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);

    private bool IsDuplicate(PotHint hint)
    {
        var latest = ConflictingHint ?? acceptedHints.LastOrDefault();
        if (latest.SourceText == null) return false;

        return string.Equals(latest.SourceText, hint.SourceText, StringComparison.Ordinal) &&
               Math.Abs(latest.ReceivedAtMilliseconds - hint.ReceivedAtMilliseconds) <= DuplicateWindowMilliseconds &&
               Vector3.DistanceSquared(latest.Origin, hint.Origin) <= DuplicatePositionToleranceSquared;
    }

    private List<PotCandidate> Filter(IEnumerable<PotHint> hints)
    {
        var evidence = hints.ToArray();
        return universe.Where(candidate => evidence.All(hint => hint.Contains(candidate.Position))).ToList();
    }

    private void Recalculate()
    {
        candidates.Clear();
        candidates.AddRange(Filter(acceptedHints));
    }

    private void ResetEvidence()
    {
        acceptedHints.Clear();
        candidates.Clear();
        candidates.AddRange(universe);
        ConflictingHint = null;
        Stage = PotSessionStage.Waiting;
    }

    private static bool TryParseDirection(string value, out PotDirection direction)
    {
        direction = value switch
        {
            "正北" => PotDirection.North,
            "东北" => PotDirection.NorthEast,
            "正东" => PotDirection.East,
            "东南" => PotDirection.SouthEast,
            "正南" => PotDirection.South,
            "西南" => PotDirection.SouthWest,
            "正西" => PotDirection.West,
            "西北" => PotDirection.NorthWest,
            _      => default
        };
        return value is "正北" or "东北" or "正东" or "东南" or "正南" or "西南" or "正西" or "西北";
    }

    private static bool TryParseDistance(
        string value,
        out PotDistanceBand band,
        out float minimum,
        out float maximum)
    {
        (band, minimum, maximum) = value switch
        {
            "很近" => (PotDistanceBand.VeryNear, 0f, 20f),
            "不远" => (PotDistanceBand.Near, 20f, 100f),
            "稍远" => (PotDistanceBand.Far, 100f, 200f),
            "很远" => (PotDistanceBand.VeryFar, 200f, float.PositiveInfinity),
            _      => default
        };
        return value is "很近" or "不远" or "稍远" or "很远";
    }
}

