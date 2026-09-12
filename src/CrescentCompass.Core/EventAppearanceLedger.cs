namespace CrescentCompass.Core;

public readonly record struct EventAppearanceKey(bool IsCe, uint Id);

public sealed class EventAppearanceLedger
{
    private readonly Dictionary<EventAppearanceKey, long> lastSeen = [];

    public bool Observe(EventAppearanceKey key, long now)
    {
        var isNew = !lastSeen.ContainsKey(key);
        lastSeen[key] = now;
        return isNew;
    }

    public void Prune(long now, long missingGraceMilliseconds)
    {
        foreach (var key in lastSeen.Where(pair => now - pair.Value >= missingGraceMilliseconds)
                     .Select(pair => pair.Key).ToArray())
            lastSeen.Remove(key);
    }

    public void Reset() => lastSeen.Clear();
}
