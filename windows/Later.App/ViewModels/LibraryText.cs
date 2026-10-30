using Later.Core;
using Later.Windows.Engine;

namespace Later.App.ViewModels;

/// The words the Mac's `Style.swift`, `LibraryWindow.swift` and `LibraryView.swift` put on sections, orders and empty lists.
public static class LibraryText
{
    public static string Title(this LibraryGrouping g) => g switch { LibraryGrouping.Date => "Date", LibraryGrouping.Site => "Site", _ => "Kind" };

    public static string Title(this LibrarySort s) => s switch
    {
        LibrarySort.TabOrder => "Tab Order (Left to Right)",
        LibrarySort.TabOrderReversed => "Tab Order (Right to Left)",
        LibrarySort.DateOpened => "Date Opened",
        LibrarySort.DateSaved => "Date Saved",
        LibrarySort.LastSeen => "Last Seen",
        LibrarySort.Title => "Title",
        _ => "Site",
    };

    /// The two directions' names: dates run newest first and text A to Z by default. The tab orders are their own directions.
    public static (string Forward, string Reversed)? DirectionTitles(this LibrarySort s) => s switch
    {
        LibrarySort.TabOrder or LibrarySort.TabOrderReversed => null,
        LibrarySort.Title or LibrarySort.Site => ("A to Z", "Z to A"),
        _ => ("Newest First", "Oldest First"),
    };

    /// Segoe Fluent Icons code points, one per section.
    public static string Glyph(this LibrarySection s) => s.Kind switch
    {
        SectionKind.All => "\uE8F1",
        SectionKind.OfKind => s.ItemKind!.Value switch
        {
            Kind.Shopping => "\uE719", Kind.Posts => "\uE8F2", Kind.School => "\uE7BE", Kind.Reading => "\uE736",
            Kind.Videos => "\uE714", Kind.Tools => "\uE90F", _ => "\uE80A",
        },
        SectionKind.Older => "\uE81C",
        SectionKind.Done => "\uE73E",
        _ => "\uE8EE",
    };

    /// Segoe Fluent Icons code points, one per kind (the same as its section's).
    public static string Glyph(this Kind k) => Glyph(LibrarySection.OfKind(k));

    public static string EmptyTitle(this LibrarySection s) => s.Kind switch
    {
        SectionKind.All => "All caught up",
        SectionKind.OfKind => s.ItemKind!.Value switch
        {
            Kind.Shopping => "No shopping yet", Kind.Posts => "No posts yet", Kind.School => "No schoolwork yet", Kind.Reading => "Nothing to read yet",
            Kind.Videos => "No videos yet", Kind.Tools => "No tools yet", _ => "Nothing else yet",
        },
        SectionKind.Older => "Nothing older",
        SectionKind.Done => "Nothing done yet",
        _ => "No regulars yet",
    };

    public static string EmptyMessage(this LibrarySection s, bool anythingOlder) => s.Kind switch
    {
        SectionKind.All => anythingOlder
            ? "Nothing from the last two weeks. Pages you haven't been back to wait in Older."
            : "Nothing from the last two weeks.",
        SectionKind.OfKind => s.ItemKind!.Value switch
        {
            Kind.Shopping => "Product pages you spend a little time on show up here.",
            Kind.Posts => "Posts and threads you stop to read show up here.",
            Kind.School => "Assignments and course pages you work on show up here.",
            Kind.Reading => "Articles and docs you spend time with show up here.",
            Kind.Videos => "Videos you watch for a while show up here.",
            Kind.Tools => "Sites, apps and projects you keep open show up here.",
            _ => "Specific pages that aren't shopping, posts, school, reading, videos or tools show up here.",
        },
        SectionKind.Older => "Pages you haven't been back to in two weeks move here. Later never deletes anything on its own.",
        SectionKind.Done => "Mark a page Done once you've dealt with it. It waits here until you empty Done.",
        _ => "The home pages and dashboards of sites you visit all the time wait here, out of your list. Keep any of them in your list whenever you like.",
    };
}

/// The Mac's `BrowserStatus` words, for the sidebar's foot and (Task 9) Settings.
public static class BrowserStatusText
{
    public static bool IsProblem(BrowserStatus s) => s == BrowserStatus.ReadFailed;

    /// "Helium", "Helium and Chrome".
    public static string Names(IReadOnlyList<Browser> browsers) => string.Join(" and ", browsers.Select(b => b.DisplayName()));

    public static string Text(BrowserStatus s, Browser b) => s switch
    {
        BrowserStatus.Watching => $"Watching {b.DisplayName()}",
        BrowserStatus.NotRunning => $"{b.DisplayName()} isn't open",
        BrowserStatus.ReadFailed => $"Can't read {b.DisplayName()}'s tabs. A browser update may have changed something.",
        _ => $"{b.DisplayName()} isn't installed",
    };
}
