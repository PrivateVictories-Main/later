using System.Globalization;
using Later.Core;

namespace Later.App.ViewModels;

/// What a row of the list says about one page, ready to draw and to read aloud. Equal rows are not redrawn.
public sealed record RowModel(Guid Id, string Title, string Site, Kind Kind, string Hint, string OpenedText, string Visits, string OpenLabel,
                              int Copies, string When, bool Done, bool Keeps, bool ShowsKind, bool ShowsHost, string Name)
{
    /// "Open now" says more than a dot to assistive technology; "2 open" is the label past one tab.
    public bool IsOpen => Copies > 0;

    /// The row's second button: Keep Pages Like This for a regular that is in the list, else Mark Done, or Move Back for a finished page.
    public bool ShowsMarkDone => !Keeps && !Done;
    public bool ShowsMoveBack => !Keeps && Done;

    /// The line after the site: what sets the page apart from another with the same title, and when it was opened.
    public string SiteLine => string.Join(" · ", new[] { ShowsHost ? Site : "", Hint }.Where(s => s.Length > 0));

    /// Site, when it was opened and the visits as one line, so a narrow list shortens it with an ellipsis instead of cutting a word off.
    public string Meta => string.Join(" · ", new[] { SiteLine, OpenedText, Visits }.Where(s => s.Length > 0));

    /// What assistive technology reads for the row.
    public override string ToString() => Name;
}

public enum HeaderStyle { Plain, Site, Kind }

/// A heading of the list: a date, a site or a kind, with its count and a chevron that folds it.
public sealed record GroupHeaderRow(string Title, int Count, bool Folded, HeaderStyle Style, Kind? Kind)
{
    public string CountText => Count.ToString(CultureInfo.InvariantCulture);
    public bool IsSite => Style == HeaderStyle.Site;
    public bool IsKind => Style == HeaderStyle.Kind;
    public bool ShowsTitle => Style != HeaderStyle.Kind;   // a kind's chip says its name
    public Kind KindValue => Kind ?? Core.Kind.Other;

    /// Down while open, right while folded.
    public string Chevron => Folded ? "" : "";

    /// The button's name: what pressing it does.
    public string ActionName => (Folded ? "Expand " : "Collapse ") + Title;

    /// "Today, 4 pages": what the item itself is called.
    public string Name => $"{Title}, {(Count == 1 ? "1 page" : CountText + " pages")}";

    public override string ToString() => Name;
}

/// The list as one flat sequence of headings and rows (folded groups give only their heading), the way the list control shows it.
public static class ListRows
{
    public static List<object> Build(Listing listing, LibrarySection section, bool searching, LibraryGrouping effective, bool groupingActive, LibrarySort sort,
                                     DateTime now, Func<string, bool> isFolded, TimeZoneInfo? tz = null, CultureInfo? culture = null)
    {
        var grouping = groupingActive ? effective : (LibraryGrouping?)null;
        bool showsKind = grouping != LibraryGrouping.Kind && (searching || section.Kind != SectionKind.OfKind);
        bool showsHost = grouping != LibraryGrouping.Site;
        var rows = new List<object>();
        foreach (var group in listing.Groups)
        {
            bool folded = isFolded(group.Title);
            var style = grouping switch { LibraryGrouping.Site => HeaderStyle.Site, LibraryGrouping.Kind => HeaderStyle.Kind, _ => HeaderStyle.Plain };
            rows.Add(new GroupHeaderRow(group.Title, group.Items.Count, folded, style, style == HeaderStyle.Kind ? group.Items.FirstOrDefault()?.Kind : null));
            if (folded) continue;
            foreach (var item in group.Items)
                rows.Add(Row(item, listing.Copies.GetValueOrDefault(item.Id), listing.Hints.GetValueOrDefault(item.Id), sort, now, showsKind, showsHost, tz, culture));
        }
        return rows;
    }

    public static RowModel Row(Item item, int copies, string? hint, LibrarySort sort, DateTime now, bool showsKind, bool showsHost, TimeZoneInfo? tz = null, CultureInfo? culture = null)
    {
        var title = LibraryListing.DisplayTitle(item);
        bool done = item.Status == ItemStatus.Done;
        var saved = LibraryQuery.ShortDate(item.FirstSaved, now, tz, culture);
        var name = $"{title}, {item.Host}, {item.Kind.Title()}, saved {saved}" + (copies > 0 ? ", open now" : "") + (done ? ", done" : "");
        return new RowModel(item.Id, title, item.Host, item.Kind, hint ?? "", LibraryListing.OpenedText(item, sort, hint, now, tz) ?? "",
                            LibraryListing.VisitsLabel(item) ?? "", LibraryListing.OpenLabel(copies) ?? "", copies,
                            LibraryQuery.ShortDate(LibraryQuery.GroupDate(item, sort), now, tz, culture), done, item.IsActiveRegular, showsKind, showsHost, name);
    }
}
