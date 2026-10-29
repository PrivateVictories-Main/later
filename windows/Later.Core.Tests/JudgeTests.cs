using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

public class JudgeTests
{
    [Fact]
    public void ResearchingSavesNothing()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true));
        foreach (var product in new[] { ProductA, ProductB })
        {
            h.Wait(1); h.Show(T(product, active: true));
            h.Wait(20);
        }
        h.Show(T(ShopHome, "Shop", active: true));
        h.AssertNothingSaved();
    }

    [Fact]
    public void SpendingTimeSavesAtTheNextSnapshot()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true));
        h.Wait(120); h.Show(T(ProductA, active: true));   // the browser's next write: same tabs
        h.AssertSaved(ProductA);
        h.Wait(5); h.Show(T(ShopHome, "Shop", active: true));   // the same tab goes back to the homepage
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void ANightAsleepIsNotReadingTime()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true));
        h.Wait(5); h.Judge.SetAway(true, h.Now);   // the lid closes with the page in front
        h.Wait(8 * 3600); h.Judge.SetAway(false, h.Now);
        h.Show(T(ProductA, active: true));
        h.AssertNothingSaved();
        h.Show(T(ProductA));   // closed after a look of five seconds
        h.Show();
        h.AssertNothingSaved();
        Assert.Equal(new List<double> { 5 }, h.Library.Data.DwellSamples);
    }

    [Fact]
    public void ANightAsleepIsNotTimeLeftOpenEither()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(10); h.Judge.SetAway(true, h.Now);
        h.Wait(8 * 3600); h.Judge.SetAway(false, h.Now);
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertNothingSaved();
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // a minute awake and off screen
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void ReadsWhileAwayStartNoClocks()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(10); h.Judge.SetAway(true, h.Now);   // locked, but the browser keeps writing its session files
        h.Wait(30); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertNothingSaved();
        h.Wait(3600); h.Judge.SetAway(false, h.Now);
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertNothingSaved();
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // a minute awake and off screen
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void TimeAwakeAroundASleepStillCounts()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true));
        h.Wait(100); h.Judge.SetAway(true, h.Now);
        h.Wait(3600); h.Judge.SetAway(false, h.Now);
        h.Show(T(ProductA, active: true));
        h.AssertNothingSaved();
        h.Wait(30); h.Show(T(ProductA, active: true));
        h.AssertSaved(ProductA);   // the 100 s before the sleep and 30 s after it make the 120
    }

    [Fact]
    public void BackgroundTabLeftOpenIsSaved()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void BackgroundTabClosedAfterAMinuteIsStillSaved()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void QuickLookThenCloseIsNotSaved()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true));
        h.Wait(1); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.Wait(15); h.Show(T(ShopHome, "Shop", active: true));
        h.AssertNothingSaved();
        Assert.Equal(new List<double> { 15 }, h.Library.Data.DwellSamples);
    }

    [Fact]
    public void ComingBackSavesImmediately()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true));
        h.Wait(1); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.Wait(10); h.Show(T(ShopHome, "Shop", active: true));
        h.Wait(3600); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void HomepagesOpenAllDayAreNeverSaved()
    {
        var h = new Harness();
        var amazon = T("https://www.amazon.com/", "Amazon.com. Spend less. Smile more.", active: true);
        var feed = T("https://x.com/home", "Home / X");
        h.Show(amazon, feed);
        h.Wait(86_400); h.Show(amazon, feed);
        h.AssertNothingSaved();
    }

    [Fact]
    public void LinksWithSecretsAreNeverSaved()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA + "?token=abc"));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA + "?token=abc"));
        h.AssertNothingSaved();
    }

    [Fact]
    public void PagesLeftOpenWhileUsingAnotherAppAreSaved()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true));
        h.Judge.SetFrontBrowser(null, h.Now);
        h.Wait(61); h.Judge.SetFrontBrowser(Browser.Helium, h.Now);
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void VisitsCountReturnsButNotTheFirstReadAfterLaunch()
    {
        var library = new Library();
        var h = new Harness(library);
        h.Show(T(ProductA, active: true));
        h.Wait(120); h.Show(T(ProductA, active: true));
        var relaunched = new Harness(library) { Now = h.Now.AddSeconds(10) };
        relaunched.Show(T(ProductA, active: true));
        Assert.Equal(1, library.Data.Items[0].Visits);
        relaunched.Wait(5); relaunched.Show(T(ShopHome, "Shop", active: true));
        relaunched.Wait(5); relaunched.Show(T(ProductA, active: true));
        Assert.Equal(2, library.Data.Items[0].Visits);
    }

    [Fact]
    public void DoneItemsAreNotSavedAgainAutomatically()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true)); h.Wait(120); h.Show(T(ProductA, active: true));
        h.Library.SetDone(h.Library.Data.Items[0].Id, true, h.Now);
        h.Wait(1); h.Show(T(ShopHome, "Shop", active: true));
        h.Wait(1); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.Wait(200); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        Assert.Single(h.Library.Data.Items);
        Assert.Equal(ItemStatus.Done, h.Library.Data.Items[0].Status);
    }

    [Fact]
    public void NotAnIdeaStopsThatKindOfPage()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true));
        h.Library.NotAnIdea("shop.example.com/products/*", null);
        h.Wait(1); h.Show(T(ProductB, active: true));
        h.Wait(300); h.Show(T(ProductB, active: true));
        h.AssertNothingSaved();
    }

    [Fact]
    public void APageOpenInTwoTabsCountsOnce()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA), T(ProductA, window: 1));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA), T(ProductA, window: 1));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void ATitleThatIsStillLoadingDoesNotResetThePage()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(30); h.Show(T(ShopHome, "Shop", active: true), T(ProductA, ""));   // reload: empty title
        h.Wait(31); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertSaved(ProductA);
    }

    /// An unchanged tab isn't judged again, but the user's corrections still apply to it on every read: here, undoing
    /// Not an Idea while its tab stays open.
    [Fact]
    public void ACorrectionMadeAfterTheTabWasJudgedStillApplies()
    {
        var h = new Harness();
        h.Library.NotAnIdea("shop.example.com/products/*", null);
        h.Show(T(ProductA, active: true));
        h.Wait(130); h.Show(T(ProductA, active: true));
        h.AssertNothingSaved();
        h.Library.SetCorrection(new Correction(Kind: Kind.School), "shop.example.com/products/*");
        h.Show(T(ProductA, active: true));
        h.Wait(130); h.Show(T(ProductA, active: true));
        h.AssertSaved(ProductA);
        Assert.Equal(Kind.School, h.Library.Data.Items[0].Kind);
    }

    [Fact]
    public void NotAnIdeaWinsOverAPageAlreadyBeingTimed()
    {
        var h = new Harness();
        h.Show(T(ProductB, active: true)); h.Wait(120); h.Show(T(ProductB, active: true));
        h.Show(T(ProductB, active: true), T(ProductA));
        h.Wait(30); h.Library.NotAnIdea(h.Library.Data.Items[0].Id);
        h.Wait(31); h.Show(T(ProductB, active: true), T(ProductA));
        h.AssertNothingSaved();
    }

    [Fact]
    public void ComingBackCountsWhenTheTitleArrivesLate()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true));
        h.Wait(1); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.Wait(10); h.Show(T(ShopHome, "Shop", active: true));
        h.Wait(3600); h.Show(T(ShopHome, "Shop"), T(ProductA, "", active: true));
        h.Wait(1); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void LeftOpenTimeCountsFromBeforeTheTitleArrived()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA, ""));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void AMinuteLeftOpenCountsWhenThePageComesOnScreenAsItsTitleArrives()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA, ""));
        h.Wait(61); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.Wait(1); h.Show(T(ShopHome, "Shop", active: true));
        h.AssertSaved(ProductA);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMinuteLeftOpenCountsWhenThePageComesOnScreenBeforeItsTitle(bool returnsToBrowser)
    {
        var h = new Harness();
        if (returnsToBrowser)
        {
            h.Judge.SetFrontBrowser(null, h.Now);
            h.Show(T(ProductA, "", active: true));
            h.Wait(61); h.Judge.SetFrontBrowser(Browser.Helium, h.Now);
        }
        else
        {
            h.Show(T(ShopHome, "Shop", active: true), T(ProductA, ""));
            h.Wait(61); h.Show(T(ShopHome, "Shop"), T(ProductA, "", active: true));
        }
        h.AssertNothingSaved();
        h.Wait(5); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void HalfAMinuteInTheBackgroundBeforeItsTitleIsNotSaved()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA, ""));
        h.Wait(30); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.Wait(10); h.Show(T(ShopHome, "Shop", active: true));
        h.AssertNothingSaved();
    }

    [Fact]
    public void NotAnIdeaWinsOverALeftOpenMinuteBeforeTheTitle()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA, ""));
        h.Wait(61); h.Show(T(ShopHome, "Shop"), T(ProductA, "", active: true));
        h.Library.NotAnIdea("shop.example.com/products/*", null);
        h.Wait(5); h.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        h.Wait(1); h.Show(T(ShopHome, "Shop", active: true));
        h.AssertNothingSaved();
    }

    [Fact]
    public void ComingBackCountsOnTheFirstReadAfterLaunch()
    {
        var library = new Library();
        library.MarkSeen(ProductA, Unix(1_800_000_000 - 3600));
        var h = new Harness(library);
        h.Show(T(ProductA, active: true));
        h.AssertSaved(ProductA);
        Assert.Equal(1, library.Data.Items[0].Visits);
    }

    [Fact]
    public void TimeWhileTheBrowserIsClosedDoesNotCount()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(30); h.Judge.BrowserQuit(Browser.Helium, h.Now);
        h.Wait(7200); h.Judge.SetFrontBrowser(Browser.Helium, h.Now);
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // relaunch restores the same tabs
        h.AssertNothingSaved();
        h.Wait(59); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertNothingSaved();
        h.Wait(2); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void AMinuteLeftOpenBeforeQuittingIsKept()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(61); h.Judge.BrowserQuit(Browser.Helium, h.Now);
        h.AssertSaved(ProductA);
    }

    [Fact]
    public void ARestoredSessionIsNotAReturnVisit()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true));
        h.Wait(120); h.Show(T(ProductA, active: true), T(ProductB));   // A saved; B opened 0 s ago
        h.Wait(10); h.Judge.BrowserQuit(Browser.Helium, h.Now);
        h.Wait(7200); h.Judge.SetFrontBrowser(Browser.Helium, h.Now);
        h.Show(T(ProductA, active: true), T(ProductB));
        h.AssertSaved(ProductA);
        Assert.Equal(1, h.Library.Data.Items[0].Visits);
        Assert.False(h.Library.WasSeen(ProductB, h.Now));
    }

    [Fact]
    public void QuittingOneBrowserLeavesTheOtherAlone()
    {
        var h = new Harness();
        h.Read(Browser.Chrome, T(ProductB));
        h.Show(T(ProductA, active: true));
        h.Wait(30); h.Judge.BrowserQuit(Browser.Helium, h.Now);
        h.Wait(31); h.Read(Browser.Chrome, T(ProductB));
        h.AssertSaved(ProductB);
        Assert.Null(h.Judge.FrontBrowser);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagesClosedTogetherAreJudgedByTheSameThreshold(bool reversed)
    {
        var h = new Harness();
        for (int i = 0; i < 19; i++) h.Library.AddDwellSample(10);
        void Show(bool aActive)
        {
            var tabs = new[] { T(ProductA, active: aActive), T(ProductB, active: !aActive) };
            h.Read(Browser.Helium, reversed ? Enumerable.Reverse(tabs).ToArray() : tabs);
        }
        for (int i = 0; i < 4; i++) { Show(i % 2 == 0); h.Wait(35); }
        h.Show(T(ShopHome, "Shop", active: true));
        h.AssertNothingSaved();
    }
}

public class DismissalTests
{
    /// Saves ProductA as a background tab left open, then the user deletes it with the tab still open.
    static Harness SavedThenDeletedWhileOpen(ISet<Browser>? watched = null)
    {
        var h = new Harness(watched: watched);
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertSaved(ProductA);
        h.Library.Delete(h.Library.Data.Items[0].Id, h.Now);
        return h;
    }

    static HashSet<Browser> Both() => new HashSet<Browser> { Browser.Helium, Browser.Chrome };

    /// Later restarts: a fresh judge on the same list, whose first read finds the tab still open.
    static Harness Restarted(Harness h) => new(h.Library, h.Watched) { Now = h.Now.AddSeconds(30) };

    static void Chrome(Harness h, params Tab[] tabs) => h.Read(Browser.Chrome, tabs);

    [Fact]
    public void DeletedPageIsNotSavedAgainWhileItsTabStaysOpen()
    {
        var h = SavedThenDeletedWhileOpen();
        h.Wait(120); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertNothingSaved();
        var r = Restarted(h);
        r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.Wait(120); r.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // left open
        r.Wait(1); r.Show(T(ShopHome, "Shop"), T(ProductA, active: true));
        r.Wait(300); r.Show(T(ShopHome, "Shop"), T(ProductA, active: true));   // spent time
        r.AssertNothingSaved();
    }

    [Fact]
    public void ClosingTheTabEndsTheDismissal()
    {
        var r = Restarted(SavedThenDeletedWhileOpen());
        r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.Wait(10); r.Show(T(ShopHome, "Shop", active: true));   // closed
        Assert.False(r.Library.IsDismissed(ProductA));
        r.Wait(3600); r.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // came back
        r.AssertSaved(ProductA);
    }

    [Fact]
    public void PageDeletedAfterItsTabClosedCountsNormallyWhenOpenedAgain()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true));
        var item = h.Library.Data.Items[0];
        h.Library.SetDone(item.Id, true, h.Now);
        h.Library.EmptyDone(h.Now);
        Assert.True(h.Library.IsDismissed(ProductA));
        h.Wait(3600); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // came back
        h.AssertSaved(ProductA);
        Assert.False(h.Library.IsDismissed(ProductA));
    }

    [Fact]
    public void ACopyInAnotherBrowserIsNotSavedAfterTheDelete()
    {
        var h = new Harness(watched: Both());
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(30); Chrome(h, T(ProductA));   // its own record, not yet left open long enough
        h.Wait(31); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertSaved(ProductA);
        h.Library.Delete(h.Library.Data.Items[0].Id, h.Now);
        h.Wait(120); Chrome(h, T(ProductA));   // Chrome's copy has now been left open
        h.AssertNothingSaved();
        h.Wait(1); Chrome(h);   // and closing it doesn't save it either
        h.AssertNothingSaved();
    }

    [Fact]
    public void ClosingOneBrowsersCopyKeepsTheDismissal()
    {
        var h = SavedThenDeletedWhileOpen(Both());
        Chrome(h, T(ProductA));
        h.Wait(10); h.Show(T(ShopHome, "Shop", active: true));   // Helium's copy closed
        Assert.True(h.Library.IsDismissed(ProductA));
        h.Wait(10); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // opened anew in Helium
        Assert.True(h.Library.IsDismissed(ProductA));
        h.Wait(10); h.Show(T(ShopHome, "Shop", active: true));
        var r = Restarted(h);
        r.Show(T(ShopHome, "Shop", active: true));
        r.Read(Browser.Chrome, T(ProductA));
        r.Wait(120); r.Read(Browser.Chrome, T(ProductA));
        r.AssertNothingSaved();
        r.Wait(1); r.Read(Browser.Chrome);   // the last copy closed
        Assert.False(r.Library.IsDismissed(ProductA));
    }

    /// After a restart Helium's copy closes before Chrome's first read, which still has it open.
    [Fact]
    public void ABrowserNotYetReadKeepsTheDismissal()
    {
        var r = Restarted(SavedThenDeletedWhileOpen(Both()));
        r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.Wait(10); r.Show(T(ShopHome, "Shop", active: true));   // Helium's copy closed
        Assert.True(r.Library.IsDismissed(ProductA));
        Chrome(r, T(ProductA));
        r.Wait(120); Chrome(r, T(ProductA));
        r.Wait(1); Chrome(r);
        r.AssertNothingSaved();
    }

    [Fact]
    public void OnlyHeliumWatchedClosingAndReopeningSavesAsBefore()
    {
        var r = Restarted(SavedThenDeletedWhileOpen());
        r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.Wait(10); r.Show(T(ShopHome, "Shop", active: true));
        Assert.False(r.Library.IsDismissed(ProductA));
        r.Wait(3600); r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.AssertSaved(ProductA);
    }

    [Fact]
    public void AQuitBrowserDoesNotBlockTheDismissalEnding()
    {
        var r = Restarted(SavedThenDeletedWhileOpen(Both()));
        r.Judge.BrowserQuit(Browser.Chrome, r.Now);   // or never running: its read says so
        r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.Wait(10); r.Show(T(ShopHome, "Shop", active: true));
        Assert.False(r.Library.IsDismissed(ProductA));
        r.Wait(3600); r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.AssertSaved(ProductA);
    }

    [Fact]
    public void ARelaunchedBrowserIsUnknownUntilItsFirstRead()
    {
        var r = Restarted(SavedThenDeletedWhileOpen(Both()));
        r.Judge.BrowserQuit(Browser.Chrome, r.Now);
        r.Judge.BrowserLaunched(Browser.Chrome);   // it may be restoring a copy
        r.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        r.Wait(10); r.Show(T(ShopHome, "Shop", active: true));
        Assert.True(r.Library.IsDismissed(ProductA));
        Chrome(r);   // its first read: nothing open
        r.Wait(3600); r.Show(T(ShopHome, "Shop", active: true), T(ProductA));   // opened anew
        r.AssertSaved(ProductA);
    }

    [Fact]
    public void IngestPrunesOldEntries()
    {
        var h = new Harness();
        h.Library.MarkSeen(ProductA, h.Now);
        h.Wait(31 * 86_400); h.Show(T(ShopHome, "Shop", active: true));
        Assert.Empty(h.Library.Data.Seen);
    }
}

/// v1.1: Helium restores a session as background tabs with their real links and titles.
public class RelaunchTests
{
    static readonly string[] KeptSites =
    [
        "https://brightlane.example.com/", "https://linkvault.example.net/video", "https://acmehq.example.com/", "https://quill.example.com/",
        "https://search-lab.example.com/", "https://github.com/octocat", "https://pinecone.example.com/", "https://example.org/",
        "https://github.com/login", "https://chatgpt.com/", "https://console.acme.example.com/login?returnTo=%2F",
    ];
    static readonly string[] Ignored =
    [
        "https://www.google.com/search?q=swift", "https://mail.google.com/mail/u/0/", "http://localhost:3000/",
        "https://www.youtube.com/", "https://www.reddit.com/", "https://x.com/home",
        "chrome://newtab/", "chrome://settings/", "https://accounts.google.com/signin",
    ];

    [Fact]
    public void ARestoredSessionSavesTheKeptSitesAfterAMinuteOffScreen()
    {
        var h = new Harness();
        var tabs = KeptSites.Select(u => T(u, "Title of " + u)).Concat(Ignored.Select(u => T(u, "Other")))
            .Append(T("https://x.com/home", "Home / X", active: true)).ToArray();
        Assert.Equal(21, tabs.Length);
        h.Judge.BrowserLaunched(Browser.Helium);
        h.Read(Browser.Helium, tabs);
        h.Wait(30); h.Read(Browser.Helium, tabs);
        h.AssertNothingSaved();
        h.Wait(35); h.Read(Browser.Helium, tabs);
        Assert.Equal(KeptSites.ToHashSet(), h.Saved.ToHashSet());
        Assert.All(h.Library.Data.Items, i => Assert.Equal(Kind.Tools, i.Kind));
    }

    [Fact]
    public void AnEmptyTitleSitePageIsSavedUnderItsHostAndRetitledWhenTheTitleArrives()
    {
        var h = new Harness();
        h.Show(T("https://acmehq.example.com/", "", active: false), T("https://x.com/home", "Home / X", active: true));
        h.Wait(65); h.Show(T("https://acmehq.example.com/", ""), T("https://x.com/home", "Home / X", active: true));
        Assert.Equal(new List<string> { "acmehq.example.com" }, h.Library.Data.Items.Select(i => i.Title).ToList());
        h.Wait(5); h.Show(T("https://acmehq.example.com/", "Acme HQ"), T("https://x.com/home", "Home / X", active: true));
        Assert.Equal(new List<string> { "Acme HQ" }, h.Library.Data.Items.Select(i => i.Title).ToList());
    }

    [Fact]
    public void NotAnIdeaOnASiteLevelPageStopsThatHostsShallowPagesFromBeingSaved()
    {
        var h = new Harness();
        h.Show(T("https://linkvault.example.net/", "Link Vault", active: true));
        h.Wait(30); h.Show(T("https://linkvault.example.net/", "Link Vault"), T("https://x.com/home", "Home / X", active: true));
        h.Wait(65); h.Show(T("https://linkvault.example.net/", "Link Vault"), T("https://x.com/home", "Home / X", active: true));
        var item = h.Library.Data.Items[0];
        Assert.Equal("linkvault.example.net/~", item.PageType);
        h.Library.NotAnIdea(item.Id);
        Assert.Empty(h.Library.Data.Items);
        h.Wait(1); h.Show(T("https://linkvault.example.net/video", "Link Vault Video"), T("https://x.com/home", "Home / X", active: true));
        h.Wait(65); h.Show(T("https://linkvault.example.net/video", "Link Vault Video"), T("https://x.com/home", "Home / X", active: true));
        h.AssertNothingSaved();
        // Other hosts are unaffected.
        h.Show(T("https://acmehq.example.com/", "Acme"), T("https://x.com/home", "Home / X", active: true));
        h.Wait(65); h.Show(T("https://acmehq.example.com/", "Acme"), T("https://x.com/home", "Home / X", active: true));
        h.AssertSaved("https://acmehq.example.com/");
    }

    // NextDeadline: the one moment the engine arms a read for.

    [Fact]
    public void ABackgroundPageHasADeadlineAMinuteAfterItLeftTheScreen()
    {
        var h = new Harness();
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        Assert.Equal(h.Now.AddSeconds(60), h.Judge.NextDeadline(Browser.Helium, h.Now));
        h.Wait(30);
        Assert.Equal(h.Now.AddSeconds(30), h.Judge.NextDeadline(Browser.Helium, h.Now));
    }

    [Fact]
    public void APageOnScreenHasADeadlineWhenItsTimeWouldSaveIt()
    {
        var h = new Harness();
        h.Show(T(ProductA, active: true));
        Assert.Equal(h.Now.AddSeconds(120), h.Judge.NextDeadline(Browser.Helium, h.Now));
        h.Wait(100);
        Assert.Equal(h.Now.AddSeconds(20), h.Judge.NextDeadline(Browser.Helium, h.Now));
    }

    [Fact]
    public void NothingWaitingOnTheClockMeansNoDeadline()
    {
        var h = new Harness();
        Assert.Null(h.Judge.NextDeadline(Browser.Helium, h.Now));
        h.Show(T(ShopHome, "Shop", active: true));                  // a general page is never tracked
        Assert.Null(h.Judge.NextDeadline(Browser.Helium, h.Now));
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.Wait(61); h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        h.AssertSaved(ProductA);                                     // saved: nothing left to wait for
        Assert.Null(h.Judge.NextDeadline(Browser.Helium, h.Now));
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA), T(ProductB));
        Assert.NotNull(h.Judge.NextDeadline(Browser.Helium, h.Now));
        Assert.Null(h.Judge.NextDeadline(Browser.Helium, h.Now.AddSeconds(100)));   // a moment already past is not a deadline
    }

    [Fact]
    public void ADeadlineBelongsToItsBrowserAndEndsWithAway()
    {
        var h = new Harness(watched: new HashSet<Browser> { Browser.Helium, Browser.Chrome });
        h.Show(T(ShopHome, "Shop", active: true), T(ProductA));
        Assert.Null(h.Judge.NextDeadline(Browser.Chrome, h.Now));
        Assert.NotNull(h.Judge.NextDeadline(Browser.Helium, h.Now));
        h.Judge.SetAway(true, h.Now);
        Assert.Null(h.Judge.NextDeadline(Browser.Helium, h.Now));
    }
}
