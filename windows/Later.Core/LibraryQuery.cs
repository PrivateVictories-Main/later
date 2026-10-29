using System.Globalization;
using System.Text.RegularExpressions;

namespace Later.Core;

public enum SectionKind { All, OfKind, Older, Done, Regulars }

/// A sidebar section: everything recent, one kind, the older pages, the done ones, or the regulars (places on sites visited all the time).
public readonly record struct LibrarySection(SectionKind Kind, Kind? ItemKind = null)
{
    public static LibrarySection All => new(SectionKind.All);
    public static LibrarySection OfKind(Kind kind) => new(SectionKind.OfKind, kind);
    public static LibrarySection Older => new(SectionKind.Older);
    public static LibrarySection Done => new(SectionKind.Done);
    public static LibrarySection Regulars => new(SectionKind.Regulars);

    public string Title => Kind switch
    {
        SectionKind.All => "All",
        SectionKind.OfKind => ItemKind!.Value.Title(),
        SectionKind.Older => "Older",
        SectionKind.Regulars => "Regulars",
        _ => "Done",
    };

    /// The sidebar's order, also the order of the number shortcuts.
    public static IReadOnlyList<LibrarySection> Ordered { get; } =
        [All, .. KindInfo.All.Select(OfKind), Older, Done, Regulars];

    internal bool Contains(Item item, DateTime cutoff) => Kind switch
    {
        SectionKind.All => item.Status == ItemStatus.Active && item.LastSeen >= cutoff && item.Regular != true,
        SectionKind.OfKind => item.Status == ItemStatus.Active && item.Kind == ItemKind && item.LastSeen >= cutoff && item.Regular != true,
        SectionKind.Older => item.Status == ItemStatus.Active && item.LastSeen < cutoff && item.Regular != true,
        // Done shows every Done page, regular or not: Empty Done deletes exactly this. Regular is where an active page lives.
        SectionKind.Regulars => item.IsActiveRegular,
        _ => item.Status == ItemStatus.Done,
    };
}

/// How a list is grouped under its headings: by day (the default), by site or by kind.
public enum LibraryGrouping { Date, Site, Kind }

/// What the list is ordered by. Dates run newest first and text A to Z unless the order is reversed; the two tab orders
/// are each other's reverse and ignore that.
public enum LibrarySort { TabOrder, TabOrderReversed, DateOpened, DateSaved, LastSeen, Title, Site }

/// A heading in the list and the items under it.
public sealed record ItemGroup(string Title, List<Item> Items);

public static partial class LibraryQuery
{
    public static readonly TimeSpan OlderAfter = TimeSpan.FromDays(14);

    public static bool IsTabOrder(this LibrarySort sort) => sort is LibrarySort.TabOrder or LibrarySort.TabOrderReversed;

    static readonly HashSet<Guid> NoPages = [];

    /// A section's items in `sort` order (newest first by default; `reversed` flips dates and text). A non-empty search
    /// looks across everything, active items first. `open` holds the pages that are in a tab right now, for the tab orders.
    // shortcut: linear search over every item per keystroke; precompute lowercased text if measuring shows it over 16 ms.
    public static List<Item> Items(LibraryData data, LibrarySection section, string search, DateTime now,
                                   LibrarySort sort = LibrarySort.LastSeen, bool reversed = false, ISet<Guid>? open = null)
    {
        var query = search.Trim();
        var cutoff = now - OlderAfter;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Has(string text) => compare.IndexOf(text, query, CompareOptions.IgnoreCase) >= 0;
        var picked = query.Length == 0
            ? data.Items.Where(i => section.Contains(i, cutoff))
            : data.Items.Where(i => Has(i.Title) || Has(i.Host) || Has(i.Url));
        var precedes = Ordering(sort, reversed, open ?? NoPages);
        return Text.StableSort(picked, (a, b) =>
        {
            if (a.Status != b.Status) return a.Status == ItemStatus.Active ? -1 : 1;
            return precedes(a, b) ? -1 : precedes(b, a) ? 1 : 0;
        });
    }

    /// Whether the first item comes before the second under `sort`; equal items keep their places.
    internal static Func<Item, Item, bool> Ordering(LibrarySort sort, bool reversed, ISet<Guid> inTabs)
    {
        Func<Item, Item, bool> Newest(Func<Item, DateTime> date) =>
            (a, b) => reversed ? date(a) < date(b) : date(a) > date(b);
        switch (sort)
        {
            case LibrarySort.LastSeen: return Newest(SortDate);
            case LibrarySort.DateOpened: return Newest(i => i.Opened);
            case LibrarySort.DateSaved: return Newest(i => i.FirstSaved);
            case LibrarySort.Title:
                return (a, b) =>
                {
                    int order = NaturalComparer.Compare(a.Title, b.Title);
                    return reversed ? order > 0 : order < 0;
                };
            case LibrarySort.Site:
                return (a, b) =>
                {
                    int order = NaturalComparer.Compare(a.Host, b.Host);
                    if (order != 0) return reversed ? order > 0 : order < 0;
                    return SortDate(a) > SortDate(b);
                };
            default:
                bool flip = sort == LibrarySort.TabOrderReversed;
                int[] Place(Item item) => [Math.Max(Array.IndexOf(BrowserInfo.All, item.Browser), 0), item.WindowRank ?? int.MaxValue, item.TabIndex ?? int.MaxValue];
                return (a, b) =>
                {
                    switch (inTabs.Contains(a.Id), inTabs.Contains(b.Id))
                    {
                        case (true, false): return true;
                        case (false, true): return false;
                        case (false, false): return SortDate(a) > SortDate(b);   // closed pages: the latest first, either way
                        default:
                            var (pa, pb) = (Place(a), Place(b));
                            // Right to left reverses the windows and the tabs, not the browsers' order.
                            for (int i = 0; i < 3; i++)
                                if (pa[i] != pb[i]) return (i == 0 || !flip) ? pa[i] < pb[i] : pa[i] > pb[i];
                            return false;
                    }
                };
        }
    }

    public static int Count(LibraryData data, LibrarySection section, DateTime now)
    {
        var cutoff = now - OlderAfter;
        return data.Items.Count(i => section.Contains(i, cutoff));
    }

    /// Every section's count in one pass over the list, for the sidebar.
    public static Dictionary<LibrarySection, int> Counts(LibraryData data, DateTime now)
    {
        var cutoff = now - OlderAfter;
        var counts = new Dictionary<LibrarySection, int>();
        void Add(LibrarySection s) => counts[s] = counts.GetValueOrDefault(s) + 1;
        foreach (var item in data.Items)
        {
            if (item.Status == ItemStatus.Done) Add(LibrarySection.Done);
            else if (item.Regular == true) Add(LibrarySection.Regulars);
            else if (item.LastSeen < cutoff) Add(LibrarySection.Older);
            else
            {
                Add(LibrarySection.All);
                Add(LibrarySection.OfKind(item.Kind));
            }
        }
        return counts;
    }

    /// Whether the list as drawn at `then` still looks the same at `now`: dates and headings go by the day, and the only
    /// other thing time changes is a page moving to Older, which changes the counts.
    public static bool LooksSame(LibraryData data, DateTime then, DateTime now, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        if (Local(then, tz).Date != Local(now, tz).Date) return false;
        var a = Counts(data, then); var b = Counts(data, now);
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var n) && n == kv.Value);
    }

    /// The date the list sorts, groups and shows an item by: when it was marked done, else when it was last seen.
    public static DateTime SortDate(Item item) => item.Status == ItemStatus.Done ? item.DoneAt ?? item.LastSeen : item.LastSeen;

    /// The date a date heading and a row's date go by under `sort`.
    public static DateTime GroupDate(Item item, LibrarySort sort) => sort switch
    {
        LibrarySort.DateOpened => item.Opened,
        LibrarySort.DateSaved => item.FirstSaved,
        _ => SortDate(item),
    };

    // MARK: Local days

    static DateTime Local(DateTime utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);   // a day that starts in a DST gap starts when the gap ends
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// The start of the local day `daysAgo` days before `utc`'s.
    static DateTime StartOfDay(DateTime utc, int daysAgo, TimeZoneInfo tz) => ToUtc(Local(utc, tz).Date.AddDays(-daysAgo), tz);

    // MARK: Groups

    /// Headings for items in `Items(…)` order: Today, Yesterday, Previous 7 Days, Previous 30 Days, then one per
    /// month. A search mixes sections, so it groups by status instead: Saved, then Done.
    /// Dates go by the date the sort is by: when the page was opened, saved or last seen.
    public static List<ItemGroup> Groups(IReadOnlyList<Item> items, bool searching, DateTime now, LibrarySort sort = LibrarySort.LastSeen,
                                         bool reversed = false, TimeZoneInfo? tz = null, CultureInfo? culture = null)
    {
        if (searching)
            return new List<ItemGroup>
            {
                new("Saved", items.Where(i => i.Status == ItemStatus.Active).ToList()),
                new("Done", items.Where(i => i.Status == ItemStatus.Done).ToList()),
            }.Where(g => g.Items.Count > 0).ToList();
        tz ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var named = new List<(DateTime Start, string Title)>
        {
            (StartOfDay(now, 0, tz), "Today"), (StartOfDay(now, 1, tz), "Yesterday"),
            (StartOfDay(now, 7, tz), "Previous 7 Days"), (StartOfDay(now, 30, tz), "Previous 30 Days"),
        };
        int year = Local(now, tz).Year;
        var groups = new List<ItemGroup>();
        (DateTime Start, DateTime End, string Title)? month = null;   // items come newest first, so a month is looked up once
        foreach (var item in items)
        {
            var date = GroupDate(item, sort);
            string title;
            var match = named.FirstOrDefault(n => date >= n.Start).Title;
            if (match is not null) title = match;
            else
            {
                if (month is not { } m || date < m.Start || date >= m.End)
                {
                    var local = Local(date, tz);
                    var start = new DateTime(local.Year, local.Month, 1);
                    month = (ToUtc(start, tz), ToUtc(start.AddMonths(1), tz), local.ToString(local.Year == year ? "MMMM" : "MMMM yyyy", culture));
                }
                title = month!.Value.Title;
            }
            int at = groups.FindLastIndex(g => g.Title == title);
            if (at >= 0) groups[at].Items.Add(item); else groups.Add(new ItemGroup(title, [item]));
        }
        // Chronological whatever the sort: items of a title or site sort meet the days out of order. A day's heading
        // covers one stretch of time, so its first item's date places it.
        return Text.StableSort(groups, (x, y) =>
        {
            var (a, b) = (GroupDate(x.Items[0], sort), GroupDate(y.Items[0], sort));
            return reversed ? a.CompareTo(b) : b.CompareTo(a);
        });
    }

    /// Headings for items in `Items(…)` order, by `grouping`: by date as above; by site, the first site in the order;
    /// by kind, in Kind's own order. A search always groups by status, since it mixes sections. The tab orders have no
    /// grouping: the pages open now, then the Closed ones.
    public static List<ItemGroup> Groups(IReadOnlyList<Item> items, LibraryGrouping grouping, bool searching, DateTime now,
                                         LibrarySort sort = LibrarySort.LastSeen, bool reversed = false, ISet<Guid>? open = null,
                                         TimeZoneInfo? tz = null, CultureInfo? culture = null)
    {
        if (searching) return Groups(items, true, now, tz: tz, culture: culture);
        if (sort.IsTabOrder())
        {
            var inTabs = open ?? NoPages;
            return new List<ItemGroup>
            {
                new("Open", items.Where(i => inTabs.Contains(i.Id)).ToList()),
                new("Closed", items.Where(i => !inTabs.Contains(i.Id)).ToList()),
            }.Where(g => g.Items.Count > 0).ToList();
        }
        switch (grouping)
        {
            case LibraryGrouping.Site:
                var groups = new List<ItemGroup>(); var position = new Dictionary<string, int>();
                foreach (var item in items)
                {
                    if (position.TryGetValue(item.Host, out var i)) groups[i].Items.Add(item);
                    else { position[item.Host] = groups.Count; groups.Add(new ItemGroup(item.Host, [item])); }
                }
                return groups;
            case LibraryGrouping.Kind:
                return KindInfo.All.Select(kind => new ItemGroup(kind.Title(), items.Where(i => i.Kind == kind).ToList()))
                    .Where(g => g.Items.Count > 0).ToList();
            default:
                return Groups(items, false, now, sort, reversed, tz, culture);
        }
    }

    // MARK: Hints for rows that look alike

    /// For rows that share a host and a title, a short hint at what sets each apart: the first part of its link the others
    /// don't have that reads as words (a course, a channel), in plain words; if there is none, when it was opened.
    /// Ids are never shown. Rows that stand alone get none.
    public static Dictionary<Guid, string> Differentiators(IReadOnlyList<Item> items, DateTime now, TimeZoneInfo? tz = null,
                                                           CultureInfo? culture = null)
    {
        var sameTitle = new Dictionary<string, List<Item>>();
        foreach (var item in items)
        {
            var key = item.Host + "\n" + item.Title.ToLowerInvariant();
            if (!sameTitle.TryGetValue(key, out var list)) sameTitle[key] = list = new List<Item>();
            list.Add(item);
        }
        var hints = new Dictionary<Guid, string>();
        foreach (var same in sameTitle.Values.Where(s => s.Count > 1))
        {
            var tokens = same.Select(i => LinkTokens(i.Url)).ToList();
            var common = tokens.Skip(1).Aggregate(new HashSet<string>(tokens[0]), (set, next) => { set.IntersectWith(next); return set; });
            var found = tokens.Select(list => list.Where(t => !common.Contains(t)).Select(t => PlainWords(t[2..])).FirstOrDefault(w => w is not null)).ToList();
            for (int i = 0; i < same.Count; i++)
            {
                // A hint two rows share tells nothing.
                var unique = found[i] is { } w && found.Count(x => x == w) == 1 ? w : null;
                hints[same[i].Id] = unique ?? "Opened " + OpenedStamp(same[i].Opened, now, tz ?? TimeZoneInfo.Local, culture ?? CultureInfo.CurrentCulture);
            }
        }
        return hints;
    }

    /// A link's path segments and query values, each marked "p:" or "q:" so equal text in different places differs.
    static List<string> LinkTokens(string link)
    {
        if (ParsedUrl.Parse(link) is not { } parts) return [];
        var tokens = Text.SplitOmitEmpty(parts.DecodedPath, '/').Select(s => "p:" + (UrlText.PercentDecode(s) ?? s)).ToList();
        foreach (var item in parts.EncodedQueryItems)
            if (item.Value is { } value) tokens.Add("q:" + (UrlText.PercentDecode(value) ?? value));
        return tokens;
    }

    [GeneratedRegex(@"^_?\d+(_\d+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();

    /// A link part as plain words ("intro-to_biology" → "Intro To Biology", at most 24 characters); null for an id or
    /// anything without a word of three letters or more.
    static string? PlainWords(string token)
    {
        bool isHex = Text.Count(token) >= 8 && token.All(Text.IsHexDigit);
        bool isNumber = NumberLike().IsMatch(token);
        if (isNumber || isHex || Structure.IsIDLike(token)) return null;
        var words = Text.SplitWhere(token, r => r.Value is '-' or '_' or '+' or ' ').Select(w =>
        {
            int first = StringInfo.GetNextTextElementLength(w);
            return w[..first].ToUpperInvariant() + w[first..];
        });
        var text = string.Join(' ', words);
        if (!Text.SplitWhere(text, r => !Text.IsLetter(r)).Any(piece => Text.Count(piece) >= 3)) return null;
        return Text.Count(text) <= 24 ? text : new StringInfo(text).SubstringByTextElements(0, 23) + "\u2026";
    }

    /// "Thu 3:04 PM" within the week, else "Oct 3, 3:04 PM" (with the year once it isn't this one).
    static string OpenedStamp(DateTime date, DateTime now, TimeZoneInfo tz, CultureInfo culture)
    {
        var local = Local(date, tz);
        string day = date >= StartOfDay(now, 6, tz) ? local.ToString("ddd", culture)
            : local.Year == Local(now, tz).Year ? local.ToString("MMM d", culture) + ","
            : local.ToString("MMM d, yyyy", culture) + ",";
        return day + " " + local.ToString("t", culture);
    }

    /// How long ago, by whole days: today, yesterday, 5 days ago, 3 weeks ago, 4 months ago, 2 years ago.
    public static string Ago(DateTime date, DateTime now, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        int days = (Local(now, tz).Date - Local(date, tz).Date).Days;
        var (count, unit) = days < 7 ? (days, "day") : days < 30 ? (days / 7, "week") : days < 365 ? (days / 30, "month") : (days / 365, "year");
        if (days < 1) return "today";
        if (days == 1) return "yesterday";
        return count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? "" : "s") + " ago";
    }

    /// What a row shows beside those headings: the time for today and yesterday, the weekday within the week,
    /// then the day and month, with the year once it isn't this one.
    public static string ShortDate(DateTime date, DateTime now, TimeZoneInfo? tz = null, CultureInfo? culture = null)
    {
        tz ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var local = Local(date, tz);
        string format = date >= StartOfDay(now, 1, tz) ? "t"
            : date >= StartOfDay(now, 6, tz) ? "ddd"
            : local.Year == Local(now, tz).Year ? "MMM d"
            : "MMM d, yyyy";
        return local.ToString(format, culture);
    }
}
