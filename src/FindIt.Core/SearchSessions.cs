using FindIt.Shared;

namespace FindIt.Core;

// Only IDs and bounded ranking provenance live here. Text, thumbnails, and hydrated
// metadata are read for the requested page and are never retained by this cache.
internal sealed record FrozenSearchFile(Guid Id, double Score, bool Exact, bool Semantic, long[] Segments)
{
    public long EstimatedBytes => 192L + Segments.Length * sizeof(long);
}

internal sealed class SearchSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Binding { get; init; }
    public required FrozenSearchFile[] Files { get; init; }
    public required QueryTerm[] Tokens { get; init; }
    public required PersonChip[] People { get; init; }
    public required Guid[] PersonIds { get; init; }
    public required string Notice { get; init; }
    public bool SemanticUsed { get; init; }
    public bool CanSearchDeeper { get; init; }
    public int RetrievalBudget { get; init; }
    public long LastUsed { get; set; }
    public long EstimatedBytes => 512L + Files.Sum(f => f.EstimatedBytes) + Tokens.Sum(t => 32L + t.Text.Length * 2L)
        + People.Sum(p => 64L + p.Name.Length * 2L) + PersonIds.Length * 16L + Notice.Length * 2L;
}

internal sealed class SearchSessionCache(TimeProvider clock, int capacity = 2, long byteBudget = 8 * 1024 * 1024)
{
    internal const long MaximumBytes = 8 * 1024 * 1024;
    private readonly Dictionary<Guid, SearchSession> sessions = [];
    internal int Count => sessions.Count;
    internal long RetainedBytes => sessions.Values.Sum(s => s.EstimatedBytes);

    public SearchSession? Find(Guid id, string binding)
    {
        Expire();
        if (!sessions.TryGetValue(id, out var session)) return null;
        if (session.Binding != binding) { sessions.Remove(id); return null; }
        session.LastUsed = clock.GetTimestamp();
        return session;
    }

    public void Add(SearchSession session)
    {
        Expire();
        if (session.EstimatedBytes > byteBudget) throw new InvalidOperationException("Search window exceeds the session memory budget.");
        while (sessions.Count >= capacity || RetainedBytes + session.EstimatedBytes > byteBudget)
            sessions.Remove(sessions.Values.MinBy(s => s.LastUsed)!.Id);
        session.LastUsed = clock.GetTimestamp(); sessions.Add(session.Id, session);
    }

    private void Expire()
    {
        long now = clock.GetTimestamp();
        foreach (var session in sessions.Values.Where(s => clock.GetElapsedTime(s.LastUsed, now) >= TimeSpan.FromMinutes(10)).ToArray()) sessions.Remove(session.Id);
    }
    public void Clear() => sessions.Clear();
}
