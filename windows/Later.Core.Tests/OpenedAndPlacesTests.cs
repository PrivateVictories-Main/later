using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

/// When a page was opened (from the browser's history) and where its tab is.
public class OpenedAndPlacesTests
{
    static readonly Verdict Reading = new(true, Kind.Reading, "a.com/*");

    static HistoryResult Result(double days, int visits) => new(T0.AddSeconds(-days * 86_400), visits);

    static Dictionary<string, HistoryResult> Answers(params (string Url, HistoryResult Result)[] answers) =>
        answers.ToDictionary(a => a.Url, a => a.Result);

    [Fact]
    public void HistoryFillsOpenedAtOnlyWhenItIsMissing()
    {
        var library = new Library();
        library.Save("https://a.com/1", "One", Reading, Browser.Helium, T0);
        Assert.Equal(new List<string> { "https://a.com/1" }, library.LinksMissingHistory().ToList());
        Assert.Equal(1, library.ApplyHistory(Answers(("https://a.com/1", Result(30, 5)))));
        var first = library.Data.Items[0];
        Assert.True(first.OpenedAt == T0.AddSeconds(-30 * 86_400) && first.HistoryVisits == 5);
        Assert.Empty(library.LinksMissingHistory());
        // A later answer never moves the opening time; the visit count follows the history.
        Assert.Equal(1, library.ApplyHistory(Answers(("https://a.com/1", Result(2, 9)))));
        var second = library.Data.Items[0];
        Assert.True(second.OpenedAt == first.OpenedAt && second.HistoryVisits == 9);
        // The same answer again changes nothing, and nothing is announced.
        int revision = library.Revision;
        Assert.Equal(0, library.ApplyHistory(Answers(("https://a.com/1", Result(2, 9)))));
        Assert.Equal(revision, library.Revision);
    }

    [Fact]
    public void AnswersAreMatchedByPageKeyAndUnknownLinksAreIgnored()
    {
        var library = new Library();
        library.Save("https://a.com/1", "One", Reading, Browser.Helium, T0);
        Assert.Equal(1, library.ApplyHistory(Answers(("https://www.a.com/1/", Result(3, 2)), ("https://zzz.example/", Result(1, 1)))));
        Assert.Equal(2, library.Data.Items[0].HistoryVisits);
    }

    [Fact]
    public void EveryBrowsersPagesAreAsked()   // the Mac asks about Helium only: its Chrome data folder is protected
    {
        var library = new Library();
        library.Save("https://a.com/1", "One", Reading, Browser.Chrome, T0);
        library.Save("https://a.com/2", "Two", Reading, Browser.Helium, T0);
        var both = new List<string> { "https://a.com/1", "https://a.com/2" };
        Assert.Equal(both, library.LinksMissingHistory().ToList());
        Assert.Equal(both, library.TakeAwaitingHistory().ToList());
        Assert.Empty(library.TakeAwaitingHistory());   // each new page is asked once
    }

    [Fact]
    public void TotalVisitsAreTheLargerCountAndOpenedFallsBackToFirstSaved()
    {
        var library = new Library();
        library.Save("https://a.com/1", "One", Reading, Browser.Helium, T0);
        var item = library.Data.Items[0];
        Assert.True(item.Opened == item.FirstSaved && item.TotalVisits == 1);
        item = item with { Visits = 3, HistoryVisits = 12, OpenedAt = T0.AddDays(-1) };
        Assert.True(item.Opened == item.OpenedAt && item.TotalVisits == 12);
        item = item with { HistoryVisits = 2 };
        Assert.Equal(3, item.TotalVisits);
    }

    [Fact]
    public void PlacesAreRecordedAndOnlyARealMoveIsAnnounced()
    {
        var library = new Library();
        library.Save("https://a.com/1", "One", Reading, Browser.Helium, T0);
        library.SetPlaces([("https://a.com/1", 3, 1), ("https://unsaved.example/", 0, 0)]);
        Assert.True(library.Data.Items[0].TabIndex == 3 && library.Data.Items[0].WindowRank == 1);
        int revision = library.Revision;
        library.SetPlaces([("https://a.com/1", 3, 1)]);
        Assert.Equal(revision, library.Revision);
        library.SetPlaces([("https://a.com/1", 0, 1)]);
        Assert.True(library.Data.Items[0].TabIndex == 0 && library.Revision == revision + 1);
    }

    [Fact]
    public void MergingKeepsTheEarliestOpeningAndTheLargestHistoryCount()
    {
        var baseItem = new Item(Guid.NewGuid(), "https://a.com/1", "One", "a.com", Kind.Reading, "a.com/*", Browser.Helium, T0, T0, 1, ItemStatus.Active);
        var www = baseItem with { Url = "https://www.a.com/1", Id = Guid.NewGuid(), OpenedAt = T0.AddSeconds(-500), HistoryVisits = 4 };
        var bare = baseItem with { OpenedAt = T0.AddSeconds(-900), HistoryVisits = 2 };
        var library = new Library(new LibraryData { Items = [www, bare] });
        Assert.Equal(1, library.MergeDuplicates());
        Assert.True(library.Data.Items[0].OpenedAt == T0.AddSeconds(-900) && library.Data.Items[0].HistoryVisits == 4);
    }

    // MARK: Tab places on ingest

    [Fact]
    public void IngestRecordsTheTabIndexAndRanksWindowsByAscendingId()
    {
        var h = new Harness();
        // Window 40 is in front (index 0) but older ids rank first: 12 is rank 0, 40 is rank 1.
        h.Show(Place(ShopHome, window: 0, tab: 0, id: 40, active: true), Place(ProductA, window: 1, tab: 3, id: 12));
        h.Wait(61);
        h.Show(Place(ShopHome, window: 0, tab: 0, id: 40, active: true), Place(ProductA, window: 1, tab: 3, id: 12));
        var item = h.Library.Data.Items[0];
        Assert.True(item.TabIndex == 3 && item.WindowRank == 0);
        // Moved to the second tab of the newer window; with the older window gone, it is the first.
        h.Show(Place(ShopHome, window: 0, tab: 0, id: 40, active: true), Place(ProductA, window: 0, tab: 1, id: 40));
        Assert.True(h.Library.Data.Items[0].TabIndex == 1 && h.Library.Data.Items[0].WindowRank == 0);
    }

    [Fact]
    public void APageOpenTwiceKeepsItsLeftmostTabInTheOldestWindow()
    {
        var h = new Harness();
        void Read() => h.Show(Place(ShopHome, window: 0, tab: 0, id: 40, active: true), Place(ProductA, window: 0, tab: 1, id: 40),
                              Place(ProductA, window: 1, tab: 5, id: 12));
        Read(); h.Wait(61); Read();
        var item = h.Library.Data.Items[0];
        Assert.True(item.WindowRank == 0 && item.TabIndex == 5);   // window 12 is the older, whatever its tab
        Assert.Equal(2, h.Judge.OpenCopies.Values.Max());
    }

    // MARK: Window ranks follow the windows' ids, not which window is in front

    [Fact]
    public void WindowRanksStayPutWhenTheFrontWindowChanges()
    {
        var h = new Harness();
        // Two windows with ids 40 and 12; the front one is listed first (index 0), as the browser lists them by focus.
        void Read(bool window40InFront) => h.Show(
            Place(ShopHome, window: window40InFront ? 0 : 1, tab: 0, id: 40, active: window40InFront),
            Place(ProductA, window: window40InFront ? 1 : 0, tab: 0, id: 12));
        Read(true); h.Wait(61); Read(true);
        Assert.Equal(0, h.Library.Data.Items[0].WindowRank);   // window 12 is the older one
        int revision = h.Library.Revision;
        h.Wait(5); Read(false);   // The user clicks the other window: the list order flips, nothing else does
        Assert.True(h.Library.Data.Items[0].WindowRank == 0 && h.Library.Revision == revision);
    }

    // MARK: A page open in both browsers

    [Fact]
    public void APageInBothBrowsersTakesItsPlaceFromTheBrowserItWasSavedFrom()
    {
        var h = new Harness(watched: new HashSet<Browser> { Browser.Helium, Browser.Chrome });
        void Helium(int tabNumber) => h.Read(Browser.Helium, Place(ShopHome, window: 0, tab: 0, id: 1, active: true),
                                             Place(ProductA, window: 0, tab: tabNumber, id: 1));
        void Chrome(int tabNumber) => h.Read(Browser.Chrome, Place(ProductA, window: 0, tab: tabNumber, id: 9));
        Helium(1); h.Wait(61); Helium(1);
        Assert.True(h.Library.Data.Items[0].Browser == Browser.Helium && h.Library.Data.Items[0].TabIndex == 1);
        Chrome(7);   // Chrome's tab is elsewhere; Helium's read still stands
        Assert.Equal(1, h.Library.Data.Items[0].TabIndex);
        Helium(2); Chrome(7); Helium(2); Chrome(8);
        Assert.Equal(2, h.Library.Data.Items[0].TabIndex);   // follows Helium, never flip-flops
        h.Read(Browser.Helium, Place(ShopHome, window: 0, tab: 0, id: 1, active: true));
        Chrome(8);   // Helium's copy closed: Chrome's is the only one
        Assert.Equal(8, h.Library.Data.Items[0].TabIndex);
    }

    // MARK: History backfill

    [Fact]
    public void AMissIsRememberedSoTheNextLaunchDoesNotAskAgain()
    {
        var library = new Library();
        library.Save("https://a.com/1", "One", Reading, Browser.Helium, T0);
        library.Save("https://a.com/2", "Two", Reading, Browser.Helium, T0);
        library.ApplyHistory(Answers(("https://a.com/2", Result(5, 1))));
        Assert.Equal(new List<string> { "https://a.com/1" }, library.LinksMissingHistory().ToList());
        library.MarkHistoryChecked(["https://a.com/1", "https://a.com/2"]);
        Assert.Empty(library.LinksMissingHistory());
        // Persisted: a list loaded from the file still knows.
        var reloaded = new Library(library.CopyData());
        Assert.Empty(reloaded.LinksMissingHistory());
        // Only when the item's link is no longer the one asked about is it asked again.
        var data = library.CopyData();
        data.Items[0] = data.Items[0] with { Url = "https://a.com/1-moved" };
        Assert.Equal(new List<string> { "https://a.com/1-moved" }, new Library(data).LinksMissingHistory().ToList());
        int revision = library.Revision;
        library.MarkHistoryChecked(["https://a.com/1"]);
        Assert.Equal(revision, library.Revision);   // nothing new to remember: nothing saved
    }
}
