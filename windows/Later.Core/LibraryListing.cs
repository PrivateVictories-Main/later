using System.Globalization;

namespace Later.Core;

public sealed record Summary(int Pages, int NewToday, int Sites);

/// What a section or search shows, worked out once and shared by the list and the inspector: the items, their groups, the rows
/// arrows move through (folded groups give none), the counts, the tabs open now for each open page, and a hint for rows that look alike.
public sealed record Listing(List<Item> Items, List<ItemGroup> Groups, List<Guid> RowIds, Summary Summary,
                             Dictionary<Guid, int> Copies, Dictionary<Guid, string> Hints)
{
    public static readonly Listing Empty = new([], [], [], new Summary(0, 0, 0), [], []);
}

/// The port of `LibraryModel.listing` and of the words the Mac's rows and inspector put on a page.
public static class LibraryListing
{
    /// `openCopies`: tabs open now, by page key; `pageKeyOf` turns a saved link into its key. `collapsed` holds `FoldKey`s.
    public static Listing Build(LibraryData data, IReadOnlyDictionary<string, int> openCopies, Func<string, string> pageKeyOf,
                                LibrarySection section, string search, DateTime now, LibraryGrouping grouping, LibrarySort sort,
                                bool reversed, ISet<string> collapsed, TimeZoneInfo tz, CultureInfo culture)
    {
        // Which items are open now: one lookup of each page's key, only while some page is open.
        var copies = new Dictionary<Guid, int>();
        if (openCopies.Count > 0)
            foreach (var item in data.Items) if (openCopies.TryGetValue(pageKeyOf(item.Url), out var n)) copies[item.Id] = n;
        var open = copies.Keys.ToHashSet();
        var query = search.Trim();
        bool searching = query.Length > 0;
        var items = LibraryQuery.Items(data, section, query, now, sort, reversed, open);
        var by = EffectiveGrouping(section, grouping);
        var groups = LibraryQuery.Groups(items, by, searching, now, sort, reversed, open, tz, culture);
        var rowIds = groups.Where(g => !collapsed.Contains(FoldKey(by, sort, searching, g.Title))).SelectMany(g => g.Items.Select(i => i.Id)).ToList();
        var today = LibraryQuery.StartOfDay(now, 0, tz);
        var summary = new Summary(items.Count, items.Count(i => i.FirstSaved >= today), items.Select(i => i.Host).Distinct(StringComparer.Ordinal).Count());
        return new Listing(items, groups, rowIds, summary, copies, LibraryQuery.Differentiators(items, now, tz, culture));
    }

    /// Grouping by kind inside one kind would make a single heading; dates stand in.
    public static LibraryGrouping EffectiveGrouping(LibrarySection section, LibraryGrouping grouping) =>
        section.Kind == SectionKind.OfKind && grouping == LibraryGrouping.Kind ? LibraryGrouping.Date : grouping;

    /// Whether headings follow the grouping: not in a search (which groups by status) nor in tab order (open, then closed).
    public static bool GroupingActive(bool searching, LibrarySort sort) => !searching && !sort.IsTabOrder();

    /// A folded heading's key: "search|Saved", "tabs|Closed", "date|Today". `grouping` is the effective one.
    public static string FoldKey(LibraryGrouping grouping, LibrarySort sort, bool searching, string groupTitle) =>
        (searching ? "search" : sort.IsTabOrder() ? "tabs" : grouping.ToString().ToLowerInvariant()) + "|" + groupTitle;

    /// "12 visits", counting what the browser's history knows too; a page seen once says nothing.
    public static string? VisitsLabel(Item i) => i.TotalVisits > 1 ? i.TotalVisits.ToString(CultureInfo.InvariantCulture) + " visits" : null;

    /// "2 open" for a page held by several tabs; one tab is only the dot.
    public static string? OpenLabel(int copies) => copies > 1 ? copies.ToString(CultureInfo.InvariantCulture) + " open" : null;

    /// The title a row and the inspector show: the link itself for a page without one.
    public static string DisplayTitle(Item i) => i.Title.Length == 0 ? i.Url : i.Title;

    /// When the page was opened, shown when that is what the list is ordered by or it differs from when it was saved:
    /// "Opened 3 weeks ago", or "First seen" where the browser's history can't say. Null when the hint already says when.
    public static string? OpenedText(Item item, LibrarySort sort, string? hint, DateTime now, TimeZoneInfo? tz = null)
    {
        if (hint is not null && hint.StartsWith("Opened", StringComparison.Ordinal)) return null;
        if (item.OpenedAt is not null && (sort == LibrarySort.DateOpened || Math.Abs((item.Opened - item.FirstSaved).TotalSeconds) > 86_400))
            return "Opened " + LibraryQuery.Ago(item.Opened, now, tz);
        return item.OpenedAt is null && sort == LibrarySort.DateOpened ? "First seen " + LibraryQuery.Ago(item.Opened, now, tz) : null;
    }

    /// The site's name label on its tile: amazon.co.uk → A, en.wikipedia.org → W, x.com → X.
    public static string SiteLetter(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (labels.Count > 1 && Text.Count(labels[^1]) <= 3) labels.RemoveAt(labels.Count - 1);
        if (labels.Count == 0) return "?";
        var last = labels[^1];
        return last[..StringInfo.GetNextTextElementLength(last)].ToUpperInvariant();
    }

    /// The link as people read it, for one line: rei.com/product/229153/… without https://, www. or a closing slash.
    public static string Readable(string url)
    {
        var text = url;
        int scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        if (text.StartsWith("www.", StringComparison.Ordinal)) text = text[4..];
        if (text.EndsWith('/')) text = text[..^1];
        return UrlText.PercentDecode(text) ?? text;
    }

    /// "Today at 9:41 AM", "Yesterday at 9:05 PM", "Oct 17, 2026 at 3:00 PM": exact, so it never goes stale.
    public static string Stamp(DateTime date, DateTime now, TimeZoneInfo? tz = null, CultureInfo? culture = null)
    {
        tz ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(date, DateTimeKind.Utc), tz);
        string day = date >= LibraryQuery.StartOfDay(now, 0, tz) ? "Today"
            : date >= LibraryQuery.StartOfDay(now, 1, tz) ? "Yesterday"
            : local.ToString("MMM d, yyyy", culture);
        return day + " at " + local.ToString("t", culture);
    }

    /// The sites with the most pages here, then by name: at most six.
    public static List<(string Host, int Count)> TopSites(IEnumerable<Item> items) =>
        items.GroupBy(i => i.Host, StringComparer.Ordinal).Select(g => (Host: g.Key, Count: g.Count()))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Host, StringComparer.Ordinal).Take(6).ToList();

    /// The line under "Overview": what the list shows, and what to do with it.
    public static string OverviewCaption(bool searching, bool empty, string query) => (searching, empty) switch
    {
        (true, true) => $"Nothing matches “{query}”.",
        (true, false) => $"What “{query}” found. Select a result to see where it came from.",
        (false, true) => "Nothing to show here.",
        _ => "Select a page to see where it came from and what you can do with it.",
    };

    /// On how many of the last 28 days each site on the list was visited, for the inspector's "why" line.
    public static Dictionary<string, int> VisitDays(Library library, int today) =>
        library.Data.Items.Select(i => i.Host).Distinct(StringComparer.Ordinal).ToDictionary(h => h, h => library.VisitDays(h, today), StringComparer.Ordinal);

    /// One line on why the page is where it is: from the site's days and the user's own choice for its page type.
    public static string Why(Item item, LibraryData data, IReadOnlyDictionary<string, int> visitDays) =>
        Familiarity.Why(item.Regular, visitDays.GetValueOrDefault(item.Host), data.Corrections.GetValueOrDefault(item.PageType));
}

/// A color of its own for a site with no icon, the same on every launch: an FNV-1a hash of the host picks one of ten.
public static class SiteTint
{
    public const int Palette = 10;

    public static int Index(string host)
    {
        ulong hash = 0xcbf2_9ce4_8422_2325;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(host)) hash = unchecked((hash ^ b) * 0x100_0000_01b3);
        return (int)(hash % Palette);
    }
}
