using Later.Core;
using Later.Windows.Native;

namespace Later.Windows.Reader;

/// When a page was first opened and how often, from a Chromium browser's own `History` file: each profile's, opened
/// read-only and immutable (no locks, no journal, nothing written), only to ask about saved pages' links and the exact
/// titles of open tabs, never to list the history. Opened for one batch and closed right after.
public static class HistoryLookupStore
{
    const string VisitsSql = @"SELECT u.visit_count, COALESCE((SELECT MIN(v.visit_time) FROM visits v WHERE v.url = u.id), u.last_visit_time)
        FROM urls u WHERE u.url = ?1";
    const string TitleSql = "SELECT url, last_visit_time FROM urls WHERE title = ?1 AND last_visit_time >= ?2 ORDER BY last_visit_time DESC LIMIT 1";
    const long EpochMicros = 11_644_473_600L * 1_000_000;   // 1601-01-01 to 1970-01-01

    /// The `History` files of a Chromium data folder's profiles.
    public static IReadOnlyList<string> Databases(string dataFolder) => ProfileFiles.In(dataFolder, "History");

    /// For each link, what the history says about it; links it has never seen are left out. The link itself is looked up
    /// first, then its `www.`/bare and trailing-slash spellings. A file that can't be read (missing, locked while the
    /// browser writes, another schema) adds nothing.
    public static Dictionary<string, HistoryResult> Lookup(IEnumerable<string> links, IReadOnlyList<string> databases)
    {
        var wanted = links.Distinct(StringComparer.Ordinal).ToList();
        var results = new Dictionary<string, HistoryResult>(StringComparer.Ordinal);
        foreach (var path in databases)
        {
            try
            {
                using var db = new SqliteDb(path);
                using var page = db.Prepare(VisitsSql);
                foreach (var link in wanted)
                {
                    HistoryResult? found;
                    try { found = FirstOf(page, Variants(link)); }
                    catch (SqliteException) { continue; }
                    if (found is null) continue;
                    results[link] = results.TryGetValue(link, out var known)
                        ? new HistoryResult(known.FirstVisit < found.Value.FirstVisit ? known.FirstVisit : found.Value.FirstVisit, known.VisitCount + found.Value.VisitCount)
                        : found.Value;
                }
            }
            catch (SqliteException) { }
        }
        return results;
    }

    /// For each title an open tab carries, the most recently visited link with exactly that title since `since`, across profiles.
    public static Dictionary<string, string> LinksForTitles(IReadOnlyCollection<string> titles, IReadOnlyList<string> databases, DateTime since)
    {
        var distinct = titles.Distinct(StringComparer.Ordinal).ToList();
        var best = new Dictionary<string, (string Url, long Time)>(StringComparer.Ordinal);
        long sinceMicros = (since.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10 + EpochMicros;
        foreach (var path in databases)
        {
            try
            {
                using var db = new SqliteDb(path);
                using var q = db.Prepare(TitleSql);
                foreach (var title in distinct)
                {
                    try
                    {
                        q.Bind(1, title).Bind(2, sinceMicros);
                        if (q.Step())
                        {
                            long time = q.Long(1);
                            if (!best.TryGetValue(title, out var known) || time > known.Time) best[title] = (q.Text(0), time);
                        }
                    }
                    finally { q.Reset(); }
                }
            }
            catch (SqliteException) { }
        }
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Url, StringComparer.Ordinal);
    }

    /// The title History holds for each link (exact spelling), first profile with a non-empty one winning. Session files
    /// do not keep titles, so a previous session's tabs get theirs from here.
    public static Dictionary<string, string> TitlesForLinks(IEnumerable<string> links, IReadOnlyList<string> databases)
    {
        var wanted = links.Distinct(StringComparer.Ordinal).ToList();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in databases)
        {
            try
            {
                using var db = new SqliteDb(path);
                using var q = db.Prepare("SELECT title FROM urls WHERE url = ?1");
                foreach (var link in wanted.Where(l => !titles.ContainsKey(l)))
                {
                    try
                    {
                        q.Bind(1, link);
                        if (q.Step() && q.Text(0) is { Length: > 0 } title) titles[link] = title;
                    }
                    catch (SqliteException) { }
                    finally { q.Reset(); }
                }
            }
            catch (SqliteException) { }
        }
        return titles;
    }

    static HistoryResult? FirstOf(SqliteStatement page, IEnumerable<string> spellings)
    {
        foreach (var spelling in spellings)
        {
            try
            {
                page.Bind(1, spelling);
                if (page.Step()) return new HistoryResult(DateOf(page.Long(1)), page.Int(0));
            }
            finally { page.Reset(); }
        }
        return null;
    }

    /// The link as written, then with the slash toggled, then the same two on the other `www.` spelling.
    internal static List<string> Variants(string link)
    {
        var parsed = ParsedUrl.Parse(link);
        if (parsed?.Host is not { } host) return [link];
        var spellings = new List<string> { link };
        static string ToggledSlash(string text) => text.EndsWith('/') ? text[..^1] : text + "/";
        bool plain = parsed.EncodedQuery is null && parsed.EncodedFragment is null;
        if (plain) spellings.Add(ToggledSlash(link));
        var other = (parsed with { Host = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : "www." + host }).Absolute;
        spellings.Add(other);
        if (plain) spellings.Add(ToggledSlash(other));
        return spellings;
    }

    /// Chromium counts microseconds from 1601-01-01.
    internal static DateTime DateOf(long chromiumMicros)
    {
        try { return DateTime.UnixEpoch.AddTicks(chromiumMicros * 10 - EpochMicros * 10); }
        catch (ArgumentOutOfRangeException) { return DateTime.UnixEpoch; }
    }
}
