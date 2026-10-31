namespace SeaPowerAIAdvisor.Crew;

internal sealed class CrewUsageSnapshot
{
    internal int ApiRequests { get; }
    internal long KnownTokens { get; }
    internal int UnknownRequests { get; }
    internal long? LastTokens { get; }
    internal CrewUsageSnapshot(int requests, long knownTokens, int unknownRequests, long? lastTokens)
    { ApiRequests = requests; KnownTokens = knownTokens; UnknownRequests = unknownRequests; LastTokens = lastTokens; }
}

// A mission owns these totals. Cancelled work can finish on a worker after speaking has paused.
internal sealed class CrewUsage
{
    private readonly object sync = new();
    private CrewUsageSnapshot snapshot = new(0, 0, 0, null);
    internal CrewUsageSnapshot Snapshot { get { lock (sync) return snapshot; } }
    internal void Record(long? tokens)
    {
        lock (sync)
            snapshot = new CrewUsageSnapshot(snapshot.ApiRequests + 1, snapshot.KnownTokens + (tokens ?? 0),
                snapshot.UnknownRequests + (tokens.HasValue ? 0 : 1), tokens);
    }
}
