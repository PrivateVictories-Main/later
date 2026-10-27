using System.Globalization;
using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

public class SortTests
{
    static readonly DateTime Now = T0;
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static DateTime Ago(double days) => Now.AddSeconds(-days * 86_400);

    /// `saved` and `seen` are days ago; `opened` days ago, null: not known. `place` is the page's window rank and tab.
    static Item Entry(string title, string host, double saved, double? seen = null, double? opened = null,
                      (int Window, int Tab)? place = null, Browser browser = Browser.Helium) =>
        new(Guid.NewGuid(), $"https://{host}/{title}", title, host, Kind.Reading, $"{host}/*", browser, Ago(saved), Ago(seen ?? saved), 1,
            ItemStatus.Active, null, opened is { } o ? Ago(o) : null, null, place?.Tab, place?.Window);

    // Open: Zeta (window 0, tab 0), alpha (0, 2), Mid (1, 0). Closed: Beta, Gamma.
    readonly Item zeta, alpha, mid, beta, gamma;
    readonly LibraryData data;
    readonly HashSet<Guid> open;

    public SortTests()
    {
        zeta = Entry("Zeta", "a.com", saved: 5, opened: 30, place: (0, 0));
        alpha = Entry("alpha", "b.com", saved: 6, seen: 1, place: (0, 2));
        mid = Entry("Mid", "c.com", saved: 3, opened: 10, place: (1, 0));
        beta = Entry("Beta", "a.com", saved: 2, opened: 20);
        gamma = Entry("Gamma", "b.com", saved: 4, opened: 4.5);
        data = new LibraryData { Items = [zeta, alpha, mid, beta, gamma] };   // not in any order the tests expect
        open = [zeta.Id, alpha.Id, mid.Id];
    }

    List<string> Titles(LibrarySort sort, bool reversed = false) =>
        LibraryQuery.Items(data, LibrarySection.All, "", Now, sort, reversed, open).Select(i => i.Title).ToList();

    [Fact]
    public void TabOrderListsOpenPagesByWindowThenTabThenClosedNewestFirst() =>
        Assert.Equal(new List<string> { "Zeta", "alpha", "Mid", "Beta", "Gamma" }, Titles(LibrarySort.TabOrder));

    [Fact]
    public void RightToLeftReversesWindowsAndTabsButNotTheClosedPages() =>
        Assert.Equal(new List<string> { "Mid", "alpha", "Zeta", "Beta", "Gamma" }, Titles(LibrarySort.TabOrderReversed));

    [Fact]
    public void TabOrderKeepsBrowsersTogether()
    {
        var chrome = Entry("Chrome low", "a.com", saved: 1, place: (0, 0), browser: Browser.Chrome);
        var helium = Entry("Helium high", "a.com", saved: 1, place: (5, 9));
        var both = new LibraryData { Items = [chrome, helium] };
        var inTabs = new HashSet<Guid> { chrome.Id, helium.Id };
        foreach (var sort in new List<LibrarySort> { LibrarySort.TabOrder, LibrarySort.TabOrderReversed })
            Assert.Equal(new List<string> { "Helium high", "Chrome low" },
                         LibraryQuery.Items(both, LibrarySection.All, "", Now, sort, open: inTabs).Select(i => i.Title).ToList());
    }

    [Fact]
    public void DateOrdersRunNewestFirstAndFlip()
    {
        // Opened, or saved where that isn't known: Gamma 4.5, alpha 6 (saved), Mid 10, Beta 20, Zeta 30.
        Assert.Equal(new List<string> { "Gamma", "alpha", "Mid", "Beta", "Zeta" }, Titles(LibrarySort.DateOpened));
        Assert.Equal(new List<string> { "Zeta", "Beta", "Mid", "alpha", "Gamma" }, Titles(LibrarySort.DateOpened, reversed: true));
        // Saved: Beta 2, Mid 3, Gamma 4, Zeta 5, alpha 6.
        Assert.Equal(new List<string> { "Beta", "Mid", "Gamma", "Zeta", "alpha" }, Titles(LibrarySort.DateSaved));
        Assert.Equal(new List<string> { "alpha", "Zeta", "Gamma", "Mid", "Beta" }, Titles(LibrarySort.DateSaved, reversed: true));
        // Last seen: alpha 1, Beta 2, Mid 3, Gamma 4, Zeta 5.
        Assert.Equal(new List<string> { "alpha", "Beta", "Mid", "Gamma", "Zeta" }, Titles(LibrarySort.LastSeen));
        Assert.Equal(new List<string> { "Zeta", "Gamma", "Mid", "Beta", "alpha" }, Titles(LibrarySort.LastSeen, reversed: true));
    }

    [Fact]
    public void TitleAndSiteOrderAToZ()
    {
        Assert.Equal(new List<string> { "alpha", "Beta", "Gamma", "Mid", "Zeta" }, Titles(LibrarySort.Title));
        Assert.Equal(new List<string> { "Zeta", "Mid", "Gamma", "Beta", "alpha" }, Titles(LibrarySort.Title, reversed: true));
        // a.com: Beta (2) then Zeta (5); b.com: alpha (1) then Gamma (4); c.com: Mid.
        Assert.Equal(new List<string> { "Beta", "Zeta", "alpha", "Gamma", "Mid" }, Titles(LibrarySort.Site));
        Assert.Equal(new List<string> { "Mid", "alpha", "Gamma", "Beta", "Zeta" }, Titles(LibrarySort.Site, reversed: true));
    }

    [Fact]
    public void TabOrderGroupsAreOpenThenClosed()
    {
        var items = new[] { zeta, beta };
        var groups = LibraryQuery.Groups(items, LibraryGrouping.Date, false, Now, LibrarySort.TabOrder, open: new HashSet<Guid> { zeta.Id });
        Assert.Equal(new List<string> { "Open", "Closed" }, groups.Select(g => g.Title).ToList());
        Assert.Equal(new List<string> { "Zeta" }, groups[0].Items.Select(i => i.Title).ToList());
        Assert.Equal(new List<string> { "Beta" }, groups[1].Items.Select(i => i.Title).ToList());
        Assert.Equal(new List<string> { "Closed" },
                     LibraryQuery.Groups(new[] { beta }, LibraryGrouping.Date, false, Now, LibrarySort.TabOrder).Select(g => g.Title).ToList());
        // A search still groups by status.
        Assert.Equal(new List<string> { "Saved" },
                     LibraryQuery.Groups(items, LibraryGrouping.Date, true, Now, LibrarySort.TabOrder, open: new HashSet<Guid> { zeta.Id })
                         .Select(g => g.Title).ToList());
    }

    [Fact]
    public void DateHeadingsGoByTheDateTheSortIsBy()
    {
        var page = Entry("Old but seen today", "a.com", saved: 0.1, opened: 40);
        string Heading(LibrarySort sort) => LibraryQuery.Groups(new[] { page }, LibraryGrouping.Date, false, Now, sort, tz: Utc)[0].Title;
        Assert.Equal("Today", Heading(LibrarySort.LastSeen));
        Assert.Equal("Today", Heading(LibrarySort.DateSaved));
        Assert.NotEqual("Today", Heading(LibrarySort.DateOpened));   // opened 40 days ago: a month heading
        Assert.Equal(page.OpenedAt, LibraryQuery.GroupDate(page, LibrarySort.DateOpened));
        Assert.Equal(page.LastSeen, LibraryQuery.GroupDate(page, LibrarySort.Title));
    }

    static Item School(string path, string title = "Discussion", string host = "learn.school.edu") =>
        new(Guid.NewGuid(), $"https://{host}{path}", title, host, Kind.School, "x", Browser.Helium, Ago(2), Ago(2), 1, ItemStatus.Active);

    [Fact]
    public void IdsAreNeverShownAndAWordIsHumanized()
    {
        var a = School("/ultra/courses/_48213_1/discussion"); var b = School("/ultra/courses/_51877_1/discussion");
        var other = School("/ultra/courses/_1_1/outline", "Outline");
        var hints = LibraryQuery.Differentiators([a, b, other], Now, Utc, new CultureInfo("en-US"));
        // Only course ids tell these apart, so each says when it was opened instead.
        Assert.StartsWith("Opened ", hints[a.Id], StringComparison.Ordinal);
        Assert.StartsWith("Opened ", hints[b.Id], StringComparison.Ordinal);
        Assert.DoesNotContain("_48213_1", hints[a.Id], StringComparison.Ordinal);
        Assert.False(hints.ContainsKey(other.Id));   // alone with its title

        var bio = School("/webapps/blackboard/execute/launcher?type=Course&id=_48213_1&course=intro-to_biology");
        var chem = School("/webapps/blackboard/execute/launcher?type=Course&id=_51877_1&course=organic-chemistry");
        var named = LibraryQuery.Differentiators([bio, chem], Now);
        Assert.True(named[bio.Id] == "Intro To Biology" && named[chem.Id] == "Organic Chemistry");

        // Hex, UUIDs and long ids are dropped like the numbered ones; a long name is cut to 24 characters.
        var uuid = School("/c/9f1c2d3e-1111-4222-8333-444455556666/home"); var hex = School("/c/deadbeefcafe/home");
        var longName = School("/c/an-unreasonably-long-course-name-for-a-hint/home");
        var all = LibraryQuery.Differentiators([uuid, hex, longName], Now);
        Assert.StartsWith("Opened ", all[uuid.Id], StringComparison.Ordinal);
        Assert.StartsWith("Opened ", all[hex.Id], StringComparison.Ordinal);
        Assert.Equal("An Unreasonably Long Co\u2026", all[longName.Id]);
        Assert.True(Text.Count(all[longName.Id]) <= 24);
        Assert.Empty(LibraryQuery.Differentiators([a, School("/ultra/courses/_48213_1/discussion", host: "other.edu")], Now));
    }

    [Fact]
    public void AHintTwoRowsShareIsReplacedByTheOpenedTime()
    {
        var a = School("/c/intro-biology/home?tab=x"); var b = School("/c/intro-biology/home?tab=y"); var c = School("/c/organic-chem/home");
        var hints = LibraryQuery.Differentiators([a, b, c], Now);
        Assert.Equal("Organic Chem", hints[c.Id]);
        Assert.StartsWith("Opened ", hints[a.Id], StringComparison.Ordinal);
    }

    [Fact]
    public void LinksThatDifferOnlyInTheirQueryValueAreToldApartByIt()
    {
        static Item Video(string id) =>
            new(Guid.NewGuid(), $"https://www.youtube.com/watch?v={id}", "YouTube TV", "youtube.com", Kind.Videos, "x", Browser.Helium, Ago(2), Ago(2), 1,
                ItemStatus.Active);
        var a = Video("news-live"); var b = Video("dQw4w9WgXcQ");   // a channel name; a video id, which is never shown
        var hints = LibraryQuery.Differentiators([a, b], Now);
        Assert.Equal("News Live", hints[a.Id]);
        Assert.StartsWith("Opened ", hints[b.Id], StringComparison.Ordinal);
    }

    [Fact]
    public void WithNothingInTheLinkToTellThemApartTheOpenedDateDoes()
    {
        static Item Page(double opened) =>
            new(Guid.NewGuid(), "https://a.com/same", "Same", "a.com", Kind.Other, "x", Browser.Helium, Ago(1), Ago(1), 1, ItemStatus.Active,
                null, Ago(opened));
        var a = Page(40); var b = Page(3);
        var hints = LibraryQuery.Differentiators([a, b], Now, Utc, new CultureInfo("en-US"));
        Assert.StartsWith("Opened ", hints[a.Id], StringComparison.Ordinal);
        Assert.NotEqual(hints[a.Id], hints[b.Id]);
    }

    [Fact]
    public void DateHeadingsStayChronologicalUnderATitleOrSiteSort()
    {
        var old = Entry("Apple", "z.com", saved: 10);     // Previous 30 Days
        var week = Entry("Banana", "y.com", saved: 3);    // Previous 7 Days
        var today = Entry("Cherry", "x.com", saved: 0.1); // Today
        foreach (var sort in new List<LibrarySort> { LibrarySort.Title, LibrarySort.Site })
        {
            var items = LibraryQuery.Items(new LibraryData { Items = [old, week, today] }, LibrarySection.All, "", Now, sort);
            var groups = LibraryQuery.Groups(items, LibraryGrouping.Date, false, Now, sort, tz: Utc);
            Assert.Equal(new List<string> { "Today", "Previous 7 Days", "Previous 30 Days" }, groups.Select(g => g.Title).ToList());
            var flipped = LibraryQuery.Items(new LibraryData { Items = [old, week, today] }, LibrarySection.All, "", Now, sort, reversed: true);
            Assert.Equal(new List<string> { "Previous 30 Days", "Previous 7 Days", "Today" },
                         LibraryQuery.Groups(flipped, LibraryGrouping.Date, false, Now, sort, reversed: true, tz: Utc).Select(g => g.Title).ToList());
        }
        // Inside a heading the chosen key still decides: A to Z by title.
        var also = Entry("Aardvark", "w.com", saved: 0.2);
        var titled = LibraryQuery.Items(new LibraryData { Items = [old, today, also] }, LibrarySection.All, "", Now, LibrarySort.Title);
        var byTitle = LibraryQuery.Groups(titled, LibraryGrouping.Date, false, Now, LibrarySort.Title, tz: Utc);
        Assert.Equal(new List<string> { "Aardvark", "Cherry" }, byTitle[0].Items.Select(i => i.Title).ToList());
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(4, "4 days ago")]
    [InlineData(7, "1 week ago")]
    [InlineData(21, "3 weeks ago")]
    [InlineData(61, "2 months ago")]
    [InlineData(800, "2 years ago")]
    public void AgoCountsWholeDays(int days, string expected) =>
        Assert.Equal(expected, LibraryQuery.Ago(Now.AddDays(-days), Now, Utc));

    [Fact]
    public void NaturalOrderComparesDigitRunsByValue()
    {
        Assert.True(NaturalComparer.Compare("file2", "file10") < 0);
        Assert.True(NaturalComparer.Compare("file10", "file2") > 0);
        Assert.Equal(0, NaturalComparer.Compare("file007", "file7"));
        Assert.True(NaturalComparer.Compare("Apple", "banana") < 0);
        Assert.True(NaturalComparer.Compare("abc", "abcd") < 0);
    }
}
