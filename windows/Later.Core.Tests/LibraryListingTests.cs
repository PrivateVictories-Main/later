using System.Globalization;
using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

/// The port of what the Mac's `LibraryModel.listing` and `ItemRow` work out, at a fixed clock, zone and culture.
public class LibraryListingTests
{
    static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo EnUs = new("en-US");
    static readonly IReadOnlyDictionary<string, int> NoCopies = new Dictionary<string, int>();
    static readonly HashSet<string> NoneFolded = [];

    readonly Library library;
    readonly LibraryData data;

    public LibraryListingTests()
    {
        library = SampleLibrary.Build(Now);
        data = library.Data;
    }

    Listing Build(LibrarySection section, string search = "", LibraryGrouping grouping = LibraryGrouping.Date, LibrarySort sort = LibrarySort.LastSeen,
                  ISet<string>? collapsed = null, IReadOnlyDictionary<string, int>? copies = null) =>
        LibraryListing.Build(data, copies ?? NoCopies, library.PageKey, section, search, Now, grouping, sort, false, collapsed ?? NoneFolded, Utc, EnUs);

    [Fact]
    public void AllHasTheSidebarsPagesUnderTheDateHeadingsInOrderWithTheSummary()
    {
        var listing = Build(LibrarySection.All);
        Assert.Equal(LibraryQuery.Counts(data, Now)[LibrarySection.All], listing.Items.Count);
        Assert.Equal(["Today", "Yesterday", "Previous 7 Days", "Previous 30 Days"], listing.Groups.Select(g => g.Title).ToArray());
        Assert.Equal(listing.Items.Count, listing.Groups.Sum(g => g.Items.Count));
        Assert.Equal(listing.Items.Select(i => i.Id), listing.RowIds);   // nothing folded: the rows are the items, in order
        Assert.Equal(new Summary(22, 4, 18), listing.Summary);   // the Mac's sample: "22 pages · 4 new today · 18 sites"
    }

    [Fact]
    public void NewTodayCountsFromTheStartOfTheLocalDayAndNotFromTwentyFourHoursAgo()
    {
        var late = new LibraryData { Items = [Entry("Late", "a.com", Now.AddHours(-11)), Entry("Last night", "b.com", Now.AddHours(-13))] };   // 01:00 today, 23:00 yesterday
        Summary SummaryIn(TimeZoneInfo zone) =>
            LibraryListing.Build(late, NoCopies, library.PageKey, LibrarySection.All, "", Now, LibraryGrouping.Date, LibrarySort.LastSeen, false, NoneFolded, zone, EnUs).Summary;
        Assert.Equal(new Summary(2, 1, 2), SummaryIn(Utc));
        // Ten hours west of UTC the day began at 10:00 UTC, after both pages were saved.
        Assert.Equal(new Summary(2, 0, 2), SummaryIn(TimeZoneInfo.CreateCustomTimeZone("west", TimeSpan.FromHours(-10), "west", "west")));
    }

    static Item Entry(string title, string host, DateTime saved) => MakeItem($"https://{host}/p/1", title, host, saved: saved, seen: saved);

    [Fact]
    public void AFoldedGroupGivesNoRowsAndItsFoldKeyIsGroupingBarTitle()
    {
        var all = Build(LibrarySection.All);
        var yesterday = all.Groups.Single(g => g.Title == "Yesterday");
        var folded = Build(LibrarySection.All, collapsed: new HashSet<string> { "date|Yesterday" });
        Assert.Equal(all.Items.Count - yesterday.Items.Count, folded.RowIds.Count);
        Assert.DoesNotContain(folded.RowIds, id => yesterday.Items.Any(i => i.Id == id));
        Assert.Equal(all.Items.Count, folded.Items.Count);   // folding hides rows, not pages: the summary is unchanged
        Assert.Equal(all.Summary, folded.Summary);
        Assert.Equal("date|Today", LibraryListing.FoldKey(LibraryGrouping.Date, LibrarySort.LastSeen, false, "Today"));
        Assert.Equal("site|rei.com", LibraryListing.FoldKey(LibraryGrouping.Site, LibrarySort.LastSeen, false, "rei.com"));
        Assert.Equal("kind|Reading", LibraryListing.FoldKey(LibraryGrouping.Kind, LibrarySort.Title, false, "Reading"));
        Assert.Equal("search|Saved", LibraryListing.FoldKey(LibraryGrouping.Site, LibrarySort.LastSeen, true, "Saved"));
        Assert.Equal("tabs|Closed", LibraryListing.FoldKey(LibraryGrouping.Date, LibrarySort.TabOrder, false, "Closed"));
        Assert.Equal("tabs|Open", LibraryListing.FoldKey(LibraryGrouping.Kind, LibrarySort.TabOrderReversed, false, "Open"));
    }

    [Fact]
    public void SearchGroupsByStatusWhateverTheGroupingAndFoldsUnderItsOwnKeys()
    {
        var listing = Build(LibrarySection.OfKind(Kind.Tools), "youtube", LibraryGrouping.Site);
        Assert.Equal(["Saved", "Done"], listing.Groups.Select(g => g.Title).ToArray());
        Assert.All(listing.Groups, g => Assert.Single(g.Items));
        Assert.Equal(listing.Items.Select(i => i.Id), listing.RowIds);
        var folded = Build(LibrarySection.All, "youtube", collapsed: new HashSet<string> { "search|Saved" });
        Assert.Single(folded.RowIds);
        Assert.Equal(ItemStatus.Done, listing.Items.Single(i => i.Id == folded.RowIds[0]).Status);
    }

    [Fact]
    public void GroupingByKindInsideOneKindFallsBackToDates()
    {
        Assert.Equal(LibraryGrouping.Date, LibraryListing.EffectiveGrouping(LibrarySection.OfKind(Kind.Reading), LibraryGrouping.Kind));
        Assert.Equal(LibraryGrouping.Kind, LibraryListing.EffectiveGrouping(LibrarySection.All, LibraryGrouping.Kind));
        Assert.Equal(LibraryGrouping.Site, LibraryListing.EffectiveGrouping(LibrarySection.OfKind(Kind.Reading), LibraryGrouping.Site));
        var inKind = Build(LibrarySection.OfKind(Kind.School), grouping: LibraryGrouping.Kind);
        Assert.DoesNotContain(inKind.Groups, g => g.Title == "School");   // dates stand in: a single "School" heading would say nothing
        Assert.False(LibraryListing.GroupingActive(true, LibrarySort.LastSeen));
        Assert.False(LibraryListing.GroupingActive(false, LibrarySort.TabOrder));
        Assert.True(LibraryListing.GroupingActive(false, LibrarySort.Title));
    }

    [Fact]
    public void OpenPagesAreCountedBySortingAndTabOrderListsThemFirstThenClosed()
    {
        var copies = SampleLibrary.OpenCopies(library);
        var listing = Build(LibrarySection.All, sort: LibrarySort.TabOrder, copies: copies);
        Assert.Equal(["Open", "Closed"], listing.Groups.Select(g => g.Title).ToArray());
        Assert.NotEmpty(listing.Groups[0].Items);
        Assert.All(listing.Groups[0].Items, i => Assert.True(listing.Copies.ContainsKey(i.Id)));
        Assert.All(listing.Groups[1].Items, i => Assert.False(listing.Copies.ContainsKey(i.Id)));
        var twice = listing.Items.Single(i => i.Host == "linkvault.example.net");   // the sample holds this page in two tabs
        Assert.Equal(2, listing.Copies[twice.Id]);
        Assert.Equal("tabs|Closed", LibraryListing.FoldKey(LibraryGrouping.Date, LibrarySort.TabOrder, false, "Closed"));
        // Without tabs open nothing is counted and every page is closed.
        var closed = Build(LibrarySection.All, sort: LibrarySort.TabOrder);
        Assert.Empty(closed.Copies);
        Assert.Equal(["Closed"], closed.Groups.Select(g => g.Title).ToArray());
    }

    [Fact]
    public void RowsThatLookAlikeGetAHintAndNoOtherRowDoes()
    {
        var listing = Build(LibrarySection.All);
        var alike = listing.Items.Where(i => i.Host == "learn.northgate.edu").ToList();
        Assert.Equal(5, alike.Count);   // three "Discussion" and two "Gradebook"
        Assert.Equal(alike.Select(i => i.Id).Order(), listing.Hints.Keys.Order());
        Assert.All(listing.Hints.Values, h => Assert.NotEmpty(h));
    }

    [Fact]
    public void LabelsForVisitsAndOpenCopies()
    {
        var item = MakeItem("https://a.com/1", "One", saved: Now);
        Assert.Null(LibraryListing.VisitsLabel(item));
        Assert.Equal("3 visits", LibraryListing.VisitsLabel(item with { Visits = 3 }));
        Assert.Equal("40 visits", LibraryListing.VisitsLabel(item with { Visits = 3, HistoryVisits = 40 }));
        Assert.Null(LibraryListing.OpenLabel(0));
        Assert.Null(LibraryListing.OpenLabel(1));
        Assert.Equal("2 open", LibraryListing.OpenLabel(2));
    }

    [Fact]
    public void TheOpenedTextAppearsOnlyWhenItAddsSomething()
    {
        var saved = MakeItem("https://a.com/1", "One", saved: Now.AddDays(-10));
        Assert.Null(LibraryListing.OpenedText(saved, LibrarySort.LastSeen, null, Now, Utc));   // no history: nothing to add
        Assert.Equal("First seen 1 week ago", LibraryListing.OpenedText(saved, LibrarySort.DateOpened, null, Now, Utc));
        var opened = saved with { OpenedAt = Now.AddDays(-30) };
        Assert.Equal("Opened 1 month ago", LibraryListing.OpenedText(opened, LibrarySort.LastSeen, null, Now, Utc));
        Assert.Null(LibraryListing.OpenedText(opened, LibrarySort.LastSeen, "Opened Thu 3:04 PM", Now, Utc));   // the hint already says when
        var same = saved with { OpenedAt = saved.FirstSaved.AddHours(-2) };
        Assert.Null(LibraryListing.OpenedText(same, LibrarySort.LastSeen, null, Now, Utc));   // within a day of the save
        Assert.Equal("Opened 1 week ago", LibraryListing.OpenedText(same, LibrarySort.DateOpened, null, Now, Utc));
    }

    [Theory]
    [InlineData("amazon.co.uk", "A")]
    [InlineData("en.wikipedia.org", "W")]
    [InlineData("x.com", "X")]
    [InlineData("localhost", "L")]
    [InlineData("learn.northgate.edu", "N")]
    [InlineData("", "?")]
    public void TheSiteTileShowsTheFirstLetterOfTheSitesName(string host, string letter) => Assert.Equal(letter, LibraryListing.SiteLetter(host));

    [Theory]
    [InlineData("https://www.rei.com/product/229153/jacket/", "rei.com/product/229153/jacket")]
    [InlineData("https://example.com/a%20b", "example.com/a b")]
    [InlineData("https://example.com/bad%zz", "example.com/bad%zz")]
    [InlineData("http://example.com", "example.com")]
    public void TheLinkIsReadableOnOneLine(string url, string expected) => Assert.Equal(expected, LibraryListing.Readable(url));

    [Fact]
    public void DatesInTheInspectorAreExactAndTodayAndYesterdayAreWords()
    {
        string Stamp(DateTime d) => Text.NormalizeSpaces(LibraryListing.Stamp(d, Now, Utc, EnUs));
        Assert.Equal("Today at 10:00 AM", Stamp(Now.AddHours(-2)));
        Assert.Equal("Yesterday at 9:05 PM", Stamp(new DateTime(2026, 10, 28, 21, 5, 0, DateTimeKind.Utc)));
        Assert.Equal("Oct 17, 2026 at 3:00 PM", Stamp(new DateTime(2026, 10, 17, 15, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void TopSitesAreTheSixWithTheMostPagesThenByName()
    {
        var items = new[] { "b.com", "a.com", "b.com", "c.com", "d.com", "e.com", "f.com", "g.com", "c.com", "c.com" }.Select((h, i) => MakeItem($"https://{h}/{i}", "t", h)).ToList();
        Assert.Equal(new List<(string, int)> { ("c.com", 3), ("b.com", 2), ("a.com", 1), ("d.com", 1), ("e.com", 1), ("f.com", 1) }, LibraryListing.TopSites(items));
    }

    [Fact]
    public void TheOverviewCaptionSaysWhatToDo()
    {
        Assert.Equal("Select a page to see where it came from and what you can do with it.", LibraryListing.OverviewCaption(false, false, ""));
        Assert.Equal("Nothing to show here.", LibraryListing.OverviewCaption(false, true, ""));
        Assert.Equal("What “apple” found. Select a result to see where it came from.", LibraryListing.OverviewCaption(true, false, "apple"));
        Assert.Equal("Nothing matches “apple”.", LibraryListing.OverviewCaption(true, true, "apple"));
    }

    // MARK: The inspector's "why" line (the Mac 1.4 rule)

    string WhyOf(Func<Item, bool> which)
    {
        var days = LibraryListing.VisitDays(library, Familiarity.Today(Now));
        return LibraryListing.Why(data.Items.First(which), data, days);
    }

    [Fact]
    public void TheWhyLineSaysNewSiteRegularOrASpecificPageOnARegularSite()
    {
        Assert.Equal("Regular · visited 24 days this month", WhyOf(i => i.Url == "https://github.com/"));   // the sample's place on a site visited on 24 days
        Assert.Equal("New site", WhyOf(i => i.Host == "rei.com"));
        Assert.Equal("A specific page on a regular site", WhyOf(i => i.Url.Contains("item?id", StringComparison.Ordinal)));   // a post on a site visited on 19 days
        Assert.Equal("New site", WhyOf(i => i.Host == "tinytools.example.net"));   // a place on a site that is too new
    }

    [Fact]
    public void YourOwnChoiceForAPageTypeIsTheReasonOnceYouHaveMadeIt()
    {
        var github = data.Items.Single(i => i.Url == "https://github.com/");
        library.MarkRegular(github.Id);
        var days = LibraryListing.VisitDays(library, Familiarity.Today(Now));
        Assert.Equal("A regular because you said so", LibraryListing.Why(library.Data.Items.Single(i => i.Id == github.Id), library.Data, days));
        library.KeepInList(github.Id);
        Assert.Equal("Kept because you said so", LibraryListing.Why(library.Data.Items.Single(i => i.Id == github.Id), library.Data, days));
    }

    [Fact]
    public void VisitDaysAreWorkedOutForEverySiteOnTheList()
    {
        var days = LibraryListing.VisitDays(library, Familiarity.Today(Now));
        Assert.Equal(24, days["github.com"]);
        Assert.Equal(19, days["news.ycombinator.com"]);
        Assert.Equal(0, days["rei.com"]);
        Assert.Equal(data.Items.Select(i => i.Host).Distinct().Count(), days.Count);
    }

    // MARK: Done and Regulars (the Mac 1.4 rules)

    [Fact]
    public void DoneShowsEveryFinishedPageAndEmptyDoneDeletesExactlyWhatDoneShows()
    {
        var github = data.Items.Single(i => i.Url == "https://github.com/");   // a regular, then finished: it is under Done, and not under Regulars
        var undo = new UndoStack();
        library.SetDone(github.Id, true, Now, undo);
        var done = Build(LibrarySection.Done);
        Assert.Contains(done.Items, i => i.Id == github.Id);
        Assert.Equal(LibraryQuery.Counts(library.Data, Now)[LibrarySection.Done], done.Items.Count);
        Assert.DoesNotContain(Build(LibrarySection.Regulars).Items, i => i.Id == github.Id);
        var shown = done.Items.Select(i => i.Id).ToHashSet();
        library.EmptyDone(Now, undo);
        Assert.Empty(Build(LibrarySection.Done).Items);
        Assert.All(shown, id => Assert.DoesNotContain(library.Data.Items, i => i.Id == id));
        undo.Undo();
        Assert.Equal(shown, Build(LibrarySection.Done).Items.Select(i => i.Id).ToHashSet());   // one Undo brings back all of them
    }
}
