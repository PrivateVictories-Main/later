namespace Later.Windows.Reader;

public sealed record RawTab(string RuntimeId, string Title, bool IsSelected, int Index);

public sealed class TabLinkResolver
{
    readonly Dictionary<string, (string Url, string Title)> remembered = new(StringComparer.Ordinal);

    /// The selected tab's URL is exact. Remember it, keyed by the tab's RuntimeId, with the title it had.
    public void Remember(RawTab tab, string url) => remembered[tab.RuntimeId] = (url, tab.Title);

    /// A background tab's link seen earlier, valid only while its title is unchanged.
    public string? Recall(RawTab tab) =>
        remembered.TryGetValue(tab.RuntimeId, out var r) && string.Equals(r.Title, tab.Title, StringComparison.Ordinal) ? r.Url : null;

    /// Titles that more than one open tab carries: their links stay unresolved unless exact.
    public static HashSet<string> AmbiguousTitles(IEnumerable<RawTab> openTabs) =>
        openTabs.GroupBy(t => t.Title, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

    /// From the previous session: a title with exactly one distinct URL, on a title no other open tab shares.
    public static string? FromSession(string title, IReadOnlyList<SessionTab> session, HashSet<string> ambiguous)
    {
        if (ambiguous.Contains(title)) return null;
        var urls = session.Where(s => string.Equals(TabNames.Clean(s.Title), title, StringComparison.Ordinal)).Select(s => s.Url).Distinct(StringComparer.Ordinal).Take(2).ToList();
        return urls.Count == 1 ? urls[0] : null;
    }

    /// From History (most recent URL for the exact title), on a title no other open tab shares.
    public static string? FromHistory(string title, IReadOnlyDictionary<string, string> linksByTitle, HashSet<string> ambiguous) =>
        !ambiguous.Contains(title) && linksByTitle.TryGetValue(title, out var url) ? url : null;

    public void Forget() => remembered.Clear();

    /// Drop remembered tabs that are no longer open (RuntimeIds are not reused while a tab lives).
    public void Prune(IReadOnlySet<string> openRuntimeIds)
    {
        foreach (var k in remembered.Keys.Where(k => !openRuntimeIds.Contains(k)).ToList()) remembered.Remove(k);
    }
}
