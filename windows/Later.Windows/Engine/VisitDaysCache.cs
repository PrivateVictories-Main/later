namespace Later.Windows.Engine;

/// The last History answer to "which days was each site visited", kept for the process: an engine started again (each handoff
/// between the app and the agent) reuses it within the same day instead of reading the History again. A failed or empty
/// answer is never kept, and while it is all there is, the last good one stands in for it.
public sealed class VisitDaysCache
{
    public static readonly VisitDaysCache Shared = new();

    readonly object gate = new();
    (DateTime Since, Dictionary<string, HashSet<int>> Days)? cached;

    /// The days since `since` (the start of a local day, so the key is the date): from the cache, or by asking.
    public Dictionary<string, HashSet<int>> Days(DateTime since, Func<DateTime, Dictionary<string, HashSet<int>>> ask)
    {
        lock (gate)
        {
            if (cached is { } c && c.Since == since) return c.Days;
            Dictionary<string, HashSet<int>> days;
            try { days = ask(since); }
            catch (Exception e) when (e is not OutOfMemoryException) { days = []; }
            if (days.Count > 0) { cached = (since, days); return days; }
            return cached?.Days ?? days;
        }
    }
}
