namespace Later.Windows.Reader;

public sealed record RawTab(string RuntimeId, string Title, bool IsSelected, int Index);

public sealed class TabLinkResolver
{
    readonly Dictionary<string, (string Url, string Title, bool Exact)> remembered = new(StringComparer.Ordinal);

    /// Remember a tab's link, keyed by the tab's RuntimeId, with the title it had. The selected tab's URL is exact, and an exact
    /// sighting replaces a guess; a link taken from the session or History (`exact: false`) is a guess that is checked again
    /// whenever its title becomes ambiguous.
    public void Remember(RawTab tab, string url, bool exact = true) => remembered[tab.RuntimeId] = (url, tab.Title, exact);

    /// A background tab's link seen earlier, valid only while its title is unchanged, and, for a guess, unshared.
    public string? Recall(RawTab tab, HashSet<string>? ambiguous = null) =>
        remembered.TryGetValue(tab.RuntimeId, out var r) && string.Equals(r.Title, tab.Title, StringComparison.Ordinal)
            && (r.Exact || ambiguous is null || !ambiguous.Contains(tab.Title)) ? r.Url : null;

    /// Titles that more than one open tab carries: their links stay unresolved unless exact.
    public static HashSet<string> AmbiguousTitles(IEnumerable<RawTab> openTabs) =>
        openTabs.GroupBy(t => t.Title, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

    /// From the previous session: a title with exactly one distinct URL, on a title no other open tab shares.
    public static string? FromSession(string title, IReadOnlyList<SessionTab> session, HashSet<string> ambiguous)
    {
        if (ambiguous.Contains(title)) return null;
        var urls = session.Where(s => string.Equals(TabNames.Clean(s.Title), title, StringComparison.Ordinal) || IsAddressName(title, s.Url))
            .Select(s => s.Url).Distinct(StringComparer.Ordinal).Take(2).ToList();
        return urls.Count == 1 ? urls[0] : null;
    }

    /// A restored tab the browser has not loaded yet is named by its address without the scheme ("example.com/p/2").
    static bool IsAddressName(string name, string url)
    {
        static string Bare(string text) => (text.StartsWith("https://", StringComparison.Ordinal) ? text[8..] : text.StartsWith("http://", StringComparison.Ordinal) ? text[7..] : text).TrimEnd('/');
        return name.Length > 0 && string.Equals(Bare(name), Bare(url), StringComparison.Ordinal);
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
