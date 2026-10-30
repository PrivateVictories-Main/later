using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Platform;
using Later.Windows.Reader;
using Xunit;
using LaterEngine = Later.Windows.Engine.Engine;

namespace Later.Windows.Tests;

public class EngineTests
{
    const string Product = "https://shop.example.com/products/1000001";
    const string ShopHome = "https://www.amazon.com/";
    static readonly DateTime T0 = new(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc);

    static Tab T(string url, string title = "Walnut desk organizer", bool active = false) => new(url, title, active, 0);
    static ReadResult Ok(params Tab[] tabs) => new(ReadStatus.Ok, new Snapshot(Browser.Helium, tabs), 1, 0, 0);
    static ReadResult Ended(ReadStatus status) => new(status, null, 0, 0, 0);

    sealed class FakeHost : IEngineHost
    {
        public sealed class Shot(TimeSpan delay, Action action, List<Shot> list) : IDisposable
        {
            public TimeSpan Delay => delay;
            public Action Action => action;
            public void Dispose() => list.Remove(this);
        }

        public readonly List<Shot> Shots = [];
        /// With `Defer`, background work waits until `RunDeferred`: a slow read or write that ends later.
        public bool Defer;
        readonly List<Action> background = [];
        public void Post(Action a) => a();
        public IDisposable After(TimeSpan delay, Action a) { var s = new Shot(delay, a, Shots); Shots.Add(s); return s; }
        public void RunBackground(Action work) { if (Defer) lock (background) background.Add(work); else work(); }
        public void RunDeferred()
        {
            while (true)
            {
                Action work;
                lock (background) { if (background.Count == 0) return; work = background[0]; background.RemoveAt(0); }
                work();
            }
        }
        public void Fire() { foreach (var s in Shots.ToList()) { Shots.Remove(s); s.Action(); } }
    }

    sealed class FakeIO : IBrowserIO
    {
        public readonly HashSet<Browser> Installed = [Browser.Helium];
        public readonly List<Browser> Reads = [];
        public readonly List<IReadOnlyList<string>> HistoryAsks = [];
        public readonly List<DateTime> VisitDayAsks = [];
        public Func<DateTime, Dictionary<string, HashSet<int>>> OnVisitDays = _ => [];
        public readonly Dictionary<nint, Browser> Owners = [];
        public readonly Dictionary<Browser, HashSet<nint>> Known = [];
        public Func<Browser, ReadResult> OnRead = _ => Ended(ReadStatus.NotRunning);
        public Func<IReadOnlyList<string>, Dictionary<string, HistoryResult>> OnHistory = _ => [];
        public Browser? Front;

        public bool IsInstalled(Browser b) => Installed.Contains(b);
        public ReadResult Read(Browser b) { Reads.Add(b); return OnRead(b); }
        public Dictionary<string, HistoryResult> History(IReadOnlyList<string> links) { HistoryAsks.Add(links); return OnHistory(links); }
        public Dictionary<string, HashSet<int>> VisitDays(DateTime since) { VisitDayAsks.Add(since); return OnVisitDays(since); }
        public IReadOnlyCollection<nint> KnownWindows(Browser b) => Known.TryGetValue(b, out var k) ? k : [];
        public void BrowserQuit(Browser b) { }
        public Browser? FrontBrowser() => Front;
        public Browser? BrowserOfWindow(nint hwnd) => Owners.TryGetValue(hwnd, out var b) ? b : null;
    }

    sealed class Rig
    {
        public readonly FakeIO IO = new() { Front = Browser.Helium };
        public readonly FakeHost Host = new();
        public readonly Library Library = new();
        public DateTime Now = T0;
        public readonly LaterEngine Engine;

        public Rig(Settings? settings = null, Action<LibraryData>? save = null, string? loadNotice = null, Library? library = null, VisitDaysCache? daysCache = null)
        {
            Library = library ?? Library;
            Engine = new LaterEngine(Library, new Judge(Library), IO, Host, save, settings ?? new Settings(), () => Now, loadNotice) { DaysCache = daysCache ?? new VisitDaysCache() };
        }

        /// A second engine on the same fakes, as the agent builds one at each handoff.
        public (LaterEngine Engine, Library Library) NextEngine(VisitDaysCache daysCache)
        {
            var library = new Library();
            return (new LaterEngine(library, new Judge(library), IO, Host, (Action<LibraryData>?)null, new Settings(), () => Now, null) { DaysCache = daysCache }, library);
        }

        public void Wait(double seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void TheFirstStartRingsEveryInstalledBrowserOnce()
    {
        var r = new Rig();
        r.IO.Installed.UnionWith([Browser.Helium, Browser.Chrome]);
        var engine = new LaterEngine(r.Library, new Judge(r.Library), r.IO, r.Host, (Action<LibraryData>?)null, new Settings(), () => r.Now, null);
        engine.Start();
        Assert.Equal([Browser.Helium, Browser.Chrome], r.IO.Reads.Order().ToArray());   // Helium comes first in the enum
        Assert.Equal(2, r.IO.Reads.Count);
    }

    [Fact]
    public void ABrowserThatIsNotInstalledIsNeverRead()
    {
        var r = new Rig();
        r.Engine.Start();
        Assert.Equal([Browser.Helium], r.IO.Reads.ToArray());
        Assert.Equal(BrowserStatus.NotInstalled, r.Engine.Status[Browser.Chrome]);
        r.Engine.Ring(Browser.Chrome);
        Assert.Single(r.IO.Reads);
    }

    [Fact]
    public void ARingDuringAReadCausesExactlyOneMoreRead()
    {
        var r = new Rig();
        int calls = 0;
        r.IO.OnRead = _ => { if (++calls == 1) { r.Engine.Ring(Browser.Helium); r.Engine.Ring(Browser.Helium); } return Ended(ReadStatus.NotRunning); };
        r.Engine.Start();
        Assert.Equal(2, r.IO.Reads.Count);
    }

    [Fact]
    public void PausedMakesRingANoOpAndAReadBegunBeforePausingIsDropped()
    {
        var r = new Rig();
        r.IO.OnRead = _ => { r.Engine.SetPaused(true); return Ok(T(Product, active: true)); };
        r.Engine.Start();
        Assert.True(r.Engine.Paused);
        Assert.Equal(BrowserStatus.NotRunning, r.Engine.Status[Browser.Helium]);   // the Ok was dropped
        Assert.Empty(r.Engine.OpenCopies);
        int reads = r.IO.Reads.Count;
        r.Engine.Ring(Browser.Helium); r.Engine.RingAll();
        Assert.Equal(reads, r.IO.Reads.Count);
    }

    [Fact]
    public void OkIngestsAndSetsWatching()
    {
        var r = new Rig();
        ReadResult current = Ok(T(Product, active: true));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        Assert.Equal(BrowserStatus.Watching, r.Engine.Status[Browser.Helium]);
        Assert.Empty(r.Library.Data.Items);
        r.Wait(130); r.Engine.Ring(Browser.Helium);
        Assert.Equal([Product], r.Library.Data.Items.Select(i => i.Url).ToArray());
    }

    [Fact]
    public void NotRunningEndsRecordsAndAPageLeftOpenAMinuteIsSaved()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        r.Wait(61); current = Ended(ReadStatus.NotRunning);
        r.Engine.Ring(Browser.Helium);
        Assert.Equal([Product], r.Library.Data.Items.Select(i => i.Url).ToArray());
        Assert.Equal(BrowserStatus.NotRunning, r.Engine.Status[Browser.Helium]);
        Assert.Empty(r.Engine.OpenCopies);
    }

    [Fact]
    public void TransientChangesNoStatusAndNothingElse()
    {
        var r = new Rig();
        ReadResult current = Ok(T(Product, active: true));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        int changes = 0; r.Engine.StateChanged += () => changes++;
        r.Wait(500); current = Ended(ReadStatus.Transient);
        r.Engine.Ring(Browser.Helium);
        Assert.Equal(BrowserStatus.Watching, r.Engine.Status[Browser.Helium]);
        Assert.Equal(0, changes);
        Assert.Empty(r.Library.Data.Items);          // the page was not judged as closed, nor as read for 500 s
        Assert.Single(r.Engine.OpenCopies);
    }

    [Fact]
    public void FailedSetsReadFailed()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ended(ReadStatus.Failed);
        r.Engine.Start();
        Assert.Equal(BrowserStatus.ReadFailed, r.Engine.Status[Browser.Helium]);
    }

    [Fact]
    public void AReaderThatThrowsIsAFailedReadNotACrash()
    {
        var r = new Rig();
        r.IO.OnRead = _ => throw new InvalidOperationException("boom");
        r.Engine.Start();
        Assert.Equal(BrowserStatus.ReadFailed, r.Engine.Status[Browser.Helium]);
    }

    [Fact]
    public void TenLibraryChangesInOneSecondMakeOneSave()
    {
        var saves = new List<LibraryData>();
        var r = new Rig(save: saves.Add);
        var verdict = new Verdict(true, Kind.Reading, "a.com/*");
        for (int i = 0; i < 10; i++) r.Library.Save($"https://a.com/{i}", $"Page {i}", verdict, Browser.Helium, T0);
        Assert.Single(r.Host.Shots);
        Assert.Equal(TimeSpan.FromSeconds(1), r.Host.Shots[0].Delay);
        r.Host.Fire();
        Assert.Single(saves);
        Assert.Equal(10, saves[0].Items.Count);
        r.Library.Save("https://a.com/more", "More", verdict, Browser.Helium, T0);   // the next change arms a new shot
        Assert.Single(r.Host.Shots);
    }

    [Fact]
    public void FlushSavesAtOnceAndThrowsWhenTheStoreFails()
    {
        var saves = 0; bool fail = false;
        var r = new Rig(save: _ => { if (fail) throw new IOException("disk"); saves++; });
        r.Library.Save("https://a.com/1", "One", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Helium, T0);
        r.Engine.Flush();
        Assert.Equal(1, saves);
        fail = true;
        Assert.Throws<IOException>(r.Engine.Flush);
        Assert.NotNull(r.Engine.Notice);
    }

    [Fact]
    public void AFailedSaveSetsTheNoticeAndTheNextSuccessfulSaveClearsIt()
    {
        bool fail = true;
        var r = new Rig(save: _ => { if (fail) throw new IOException("disk"); });
        var verdict = new Verdict(true, Kind.Reading, "a.com/*");
        r.Library.Save("https://a.com/1", "One", verdict, Browser.Helium, T0);
        r.Host.Fire();
        Assert.Equal("Later couldn't save its list. It'll try again with your next change.", r.Engine.Notice);
        fail = false;
        r.Library.Save("https://a.com/2", "Two", verdict, Browser.Helium, T0);
        r.Host.Fire();
        Assert.Null(r.Engine.Notice);
    }

    [Fact]
    public void TheLoadNoticeShowsUntilASaveFailsAndComesBack()
    {
        var r = new Rig(save: _ => { }, loadNotice: "Your list was restored from its backup copy.");
        Assert.Equal("Your list was restored from its backup copy.", r.Engine.Notice);
    }

    [Fact]
    public void UnresolvedTabsScheduleOneRetryInFifteenSecondsAtMostTwice()
    {
        var r = new Rig();
        r.IO.OnRead = _ => new ReadResult(ReadStatus.Ok, new Snapshot(Browser.Helium, []), 1, 0, 1);
        r.Engine.Start();
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(r.Host.Shots).Delay);
        r.Host.Fire();
        Assert.Equal(2, r.IO.Reads.Count);
        Assert.Single(r.Host.Shots);
        r.Host.Fire();
        Assert.Equal(3, r.IO.Reads.Count);
        Assert.Empty(r.Host.Shots);
        r.Engine.Ring(Browser.Helium);   // a new wake-up gets two new tries
        Assert.Single(r.Host.Shots);
    }

    [Fact]
    public void AnUnrecognizedWindowIsNotJudgedAsTabsClosedUntilTheRetriesRunOut()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true));   // a general page: nothing waits on the clock
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        Assert.Single(r.Engine.OpenCopies);
        current = new ReadResult(ReadStatus.Ok, new Snapshot(Browser.Helium, []), 0, 0, 0, UnrecognizedWindows: 1);   // a window the read could not see into
        r.Engine.Ring(Browser.Helium);
        Assert.Single(r.Engine.OpenCopies);          // not applied
        Assert.Single(r.Host.Shots);
        r.Host.Fire();
        Assert.Single(r.Engine.OpenCopies);
        r.Host.Fire();                               // the last try: this is the best view there is
        Assert.Empty(r.Engine.OpenCopies);
        Assert.Empty(r.Host.Shots);
    }

    static ReadResult Unrecognized() => new(ReadStatus.Ok, new Snapshot(Browser.Helium, []), 0, 0, 0, UnrecognizedWindows: 1);

    [Fact]
    public void UnrecognizedReadsAreCountedAcrossWakeUpsSoTheJudgeIsNotStarved()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        current = Unrecognized();
        r.Engine.Ring(Browser.Helium);                 // 1: not applied
        Assert.Single(r.Engine.OpenCopies);
        r.Engine.Ring(Browser.Helium);                 // 2: a title change in between does not start the count again
        Assert.Single(r.Engine.OpenCopies);
        r.Engine.Ring(Browser.Helium);                 // 3: the best view there is
        Assert.Empty(r.Engine.OpenCopies);
        Assert.Empty(r.Host.Shots);
    }

    [Fact]
    public void AReadWithNothingUnrecognizedStartsTheCountAgain()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        var unrecognized = Unrecognized();
        current = unrecognized;
        r.Engine.Ring(Browser.Helium); r.Engine.Ring(Browser.Helium);   // 1, 2
        current = Ok(T(ShopHome, "Shop", active: true));
        r.Engine.Ring(Browser.Helium);                                  // a clean read
        current = unrecognized;
        r.Engine.Ring(Browser.Helium); r.Engine.Ring(Browser.Helium);   // 1, 2 again
        Assert.Single(r.Engine.OpenCopies);
        r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Engine.OpenCopies);
    }

    // An armed read that comes back unreadable (a minimized browser exposes no tabs, a UI Automation call fails) must not end the wait.

    [Fact]
    public void AnArmedReadThatComesBackTransientIsTriedAgainTwiceAndThenLeftToTheNextRealEvent()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        r.Wait(61); current = Ended(ReadStatus.Transient);
        r.Host.Fire();                                                              // the armed read
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(r.Host.Shots).Delay);   // the same bound as for unresolved tabs
        r.Wait(15); r.Host.Fire();
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(r.Host.Shots).Delay);
        r.Wait(15); r.Host.Fire();
        Assert.Empty(r.Host.Shots);                                                 // no third try and no timer: a minimized browser is never polled
        Assert.Equal(4, r.IO.Reads.Count);                                          // the start, the armed read and the two tries
    }

    [Fact]
    public void ARetryAfterATransientArmedReadSavesThePageWhenTheBrowserAnswers()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        r.Wait(61); current = Ended(ReadStatus.Transient);
        r.Host.Fire();
        Assert.Empty(r.Library.Data.Items);
        r.Wait(15); current = Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.Host.Fire();
        Assert.Equal([Product], r.Library.Data.Items.Select(i => i.Url).ToArray());
    }

    [Fact]
    public void ATransientReadWithNothingWaitingOnTheClockArmsNothing()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        current = Ended(ReadStatus.Transient);
        r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Host.Shots);
    }

    [Fact]
    public void APageSeenBeforeTheBrowserBecameUnreadableIsStillSavedWhenTheBrowserQuits()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        r.Wait(61); current = Ended(ReadStatus.Transient);
        r.Host.Fire();
        current = Ended(ReadStatus.NotRunning); r.Wait(5);
        r.Engine.Ring(Browser.Helium);                                              // the window was destroyed: BrowserQuit ends the records
        Assert.Equal([Product], r.Library.Data.Items.Select(i => i.Url).ToArray());
    }

    // A page that time alone could still save: one read is armed for its moment, and only while there is such a page.

    [Fact]
    public void APageLeftOpenArmsOneReadForTheMomentItQualifiesPlusASecond()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.Engine.Start();
        Assert.Equal(TimeSpan.FromSeconds(61), Assert.Single(r.Host.Shots).Delay);
    }

    [Fact]
    public void AnythingNotWaitingOnTheClockArmsNothing()
    {
        var r = new Rig();
        ReadResult current = Ok();
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        Assert.Empty(r.Host.Shots);
        current = Ok(T(ShopHome, "Shop", active: true));   // a general page is never tracked
        r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Host.Shots);
    }

    [Fact]
    public void EachReadReplacesTheArmedReadInsteadOfStackingAnother()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.Engine.Start();
        r.Wait(10); r.Engine.Ring(Browser.Helium);
        r.Wait(10); r.Engine.Ring(Browser.Helium);
        Assert.Equal(TimeSpan.FromSeconds(41), Assert.Single(r.Host.Shots).Delay);   // 60 s from the first sighting, 20 s gone, plus a second
    }

    [Fact]
    public void TheArmedReadFindsThePageLeftOpenAndSavesItWithNoOtherWakeUp()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.Engine.Start();
        r.Wait(61); r.Host.Fire();
        Assert.Equal([Product], r.Library.Data.Items.Select(i => i.Url).ToArray());
        Assert.DoesNotContain(r.Host.Shots, s => s.Delay > TimeSpan.FromSeconds(2));   // nothing waits any more (only the save coalescer is armed)
    }

    [Fact]
    public void APageReadOnScreenForLongEnoughIsSavedByTheArmedReadToo()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.Engine.Start();
        Assert.Equal(TimeSpan.FromSeconds(121), Assert.Single(r.Host.Shots).Delay);
        r.Wait(121); r.Host.Fire();
        Assert.Equal([Product], r.Library.Data.Items.Select(i => i.Url).ToArray());
    }

    [Fact]
    public void PauseAwayAndStopCancelTheArmedRead()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.Engine.Start();
        Assert.Single(r.Host.Shots);
        r.Engine.SetPaused(true);
        Assert.Empty(r.Host.Shots);
        r.Engine.SetPaused(false);
        Assert.Single(r.Host.Shots);
        r.Engine.SetAway("screen", true);
        Assert.Empty(r.Host.Shots);
        r.Engine.SetAway("screen", false);
        r.Engine.Ring(Browser.Helium);
        Assert.Single(r.Host.Shots);
        r.Engine.Stop(flush: false);
        Assert.Empty(r.Host.Shots);
    }

    [Fact]
    public void ABrowserThatQuitHasNothingArmed()
    {
        var r = new Rig();
        ReadResult current = Ok(T(ShopHome, "Shop", active: true), T(Product));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        Assert.Single(r.Host.Shots);
        current = Ended(ReadStatus.NotRunning);
        r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Host.Shots);
    }

    // Letting go of the list: a read in flight or a queued write must not outlive the stop.

    [Fact]
    public void ASlowReadThatEndsAfterTheStopTouchesNothing()
    {
        var saves = 0;
        var r = new Rig(save: _ => saves++);
        r.Host.Defer = true;
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.Engine.Start();                                   // the read has begun and is slow
        r.Library.Save("https://a.com/1", "One", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Helium, T0);
        r.Engine.Stop(flush: true);                         // returns at once, with the list written
        Assert.Equal(1, saves);
        r.Wait(130);
        r.Host.RunDeferred();                               // now the read ends
        r.Host.Fire();
        Assert.Equal(1, saves);
        Assert.Single(r.Library.Data.Items);                // nothing was judged or saved from it
        Assert.Empty(r.Engine.OpenCopies);
        Assert.Equal(BrowserStatus.NotRunning, r.Engine.Status[Browser.Helium]);
    }

    [Fact]
    public void AWriteQueuedBeforeAFailedFinalFlushNeverLandsAfterTheStop()
    {
        int writes = 0; bool failFirst = true;
        var r = new Rig(save: _ => { if (failFirst) { failFirst = false; throw new IOException("disk"); } writes++; });
        r.Host.Defer = true;
        r.Library.Save("https://a.com/1", "One", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Helium, T0);
        r.Host.Fire();                                      // the save coalescer fires: the write loop is queued
        using var taken = new ManualResetEventSlim(); using var go = new ManualResetEventSlim();
        r.Engine.BeforeDiskWrite = () => { taken.Set(); go.Wait(10_000); };
        var writer = new Thread(r.Host.RunDeferred);        // it has its data and waits before the disk
        writer.Start();
        Assert.True(taken.Wait(10_000));
        r.Engine.Stop(flush: true);                         // the final flush fails
        go.Set();
        Assert.True(writer.Join(10_000));
        Assert.Equal(0, writes);                            // and the older write does not land over whatever the app writes next
        Assert.True(r.Engine.SaveFailed);
    }

    // Regulars: the history's days, once a day, and what they do to pages saved before.

    static readonly Verdict OldPlace = new(true, Kind.Tools, "dash.example.com/~");   // as 1.3 saved it, no flag

    static Dictionary<string, HashSet<int>> DashDays(DateTime now, int count) =>
        new() { ["dash.example.com"] = Enumerable.Range(1, count).Select(d => Familiarity.Today(now) - d).ToHashSet() };

    [Fact]
    public void TheDaysAreAskedAtStartFromAWindowBackAndOldPlacesOnRegularSitesMove()
    {
        var library = new Library();
        library.Save("https://dash.example.com/", "Dash", OldPlace, Browser.Helium, T0);
        var r = new Rig(library: library);
        r.IO.OnVisitDays = _ => DashDays(T0, 5);
        r.Engine.Start();
        Assert.True(library.Data.Items[0].Regular);
        Assert.Equal(T0.ToLocalTime().Date.AddDays(-Familiarity.Window), Assert.Single(r.IO.VisitDayAsks));
        Assert.Equal(5, library.VisitDays("dash.example.com", Familiarity.Today(T0)));
    }

    [Fact]
    public void ItIsNotAskedAgainTheSameDayButIsAtTheFirstReadOnTheNextOne()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(ShopHome, "Shop", active: true));
        r.Engine.Start();
        r.Wait(3_600); r.Engine.Ring(Browser.Helium);
        Assert.Single(r.IO.VisitDayAsks);
        r.Wait(86_400); r.Engine.Ring(Browser.Helium);
        Assert.Equal(2, r.IO.VisitDayAsks.Count);
        r.Engine.Ring(Browser.Helium);
        Assert.Equal(2, r.IO.VisitDayAsks.Count);
    }

    [Fact]
    public void TwoEnginesOnTheSameDayMakeOneHistoryQuery()
    {
        var cache = new VisitDaysCache();
        var r = new Rig(daysCache: cache);
        r.IO.OnVisitDays = _ => DashDays(T0, 5);
        r.Engine.Start();
        r.Engine.Stop(flush: false);
        var (second, library) = r.NextEngine(cache);   // the handoff to the app and back
        second.Start();
        Assert.Single(r.IO.VisitDayAsks);
        Assert.Equal(5, library.VisitDays("dash.example.com", Familiarity.Today(T0)));
    }

    [Fact]
    public void ANewDayAsksAgainAndAFailedAnswerFallsBackToTheLastGoodOneInTheNextEngine()
    {
        var cache = new VisitDaysCache();
        var r = new Rig(daysCache: cache);
        r.IO.OnVisitDays = _ => DashDays(T0, 5);
        r.Engine.Start();
        r.Engine.Stop(flush: false);
        r.Wait(86_400);
        r.IO.OnVisitDays = _ => throw new InvalidOperationException("locked");
        var (second, library) = r.NextEngine(cache);
        second.Start();
        Assert.Equal(2, r.IO.VisitDayAsks.Count);
        Assert.Equal(5, library.VisitDays("dash.example.com", Familiarity.Today(T0)));
    }

    [Fact]
    public void WithNoDaysToGoByNothingIsMigrated()
    {
        var library = new Library();
        library.Save("https://dash.example.com/", "Dash", OldPlace, Browser.Helium, T0);
        var r = new Rig(library: library);
        r.Engine.Start();
        Assert.Null(library.Data.Items[0].Regular);   // not marked "kept": it is looked at again when there are days
    }

    [Fact]
    public void AFailedOrEmptyAnswerKeepsTheLastGoodOne()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(ShopHome, "Shop", active: true));
        r.IO.OnVisitDays = _ => DashDays(T0, 5);
        r.Engine.Start();
        r.IO.OnVisitDays = _ => throw new InvalidOperationException("locked");
        r.Wait(86_400); r.Engine.Ring(Browser.Helium);
        r.IO.OnVisitDays = _ => [];
        r.Wait(86_400); r.Engine.Ring(Browser.Helium);
        Assert.Equal(3, r.IO.VisitDayAsks.Count);
        Assert.Equal(5, r.Library.VisitDays("dash.example.com", Familiarity.Today(T0)));
    }

    [Fact]
    public void APlaceOnARegularSiteIsSavedAsARegularThroughTheEngine()
    {
        var r = new Rig();
        r.IO.OnVisitDays = _ => DashDays(T0, 6);
        r.IO.OnRead = _ => Ok(T("https://dash.example.com/", "Dash", active: true));
        r.Engine.Start();
        r.Wait(130); r.Engine.Ring(Browser.Helium);
        Assert.Equal(new List<bool?> { true }, r.Library.Data.Items.Select(i => i.Regular).ToList());
    }

    [Fact]
    public void ADisplayOffAllNightIsNotReadingTime()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.Engine.Start();
        r.Wait(5); r.Engine.SetAway("screen", true);
        r.Wait(8 * 3600); r.Engine.SetAway("screen", false);
        r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Library.Data.Items);
    }

    [Fact]
    public void TheDisplayGoingOffThroughTheSessionWindowIsAwayForTheScreenAndReadsTheBrowsers()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.Engine.Start();
        int readsBefore = r.IO.Reads.Count;
        using var thread = new EngineThread();
        SessionNotifier? notifier = null;
        using var up = new ManualResetEventSlim();
        var runner = new Thread(() => thread.Run(
            init: () => { notifier = new SessionNotifier(); EngineHost.ConnectSession(notifier, r.Engine, () => { }, () => { }); up.Set(); },
            waitHandles: () => [],
            signaled: _ => { },
            onExit: () => notifier!.Dispose()));
        runner.Start();
        try
        {
            Assert.True(up.Wait(10_000));
            r.Wait(5);
            SendTo.PowerSetting(notifier!.Hwnd, SessionNotifier.SessionDisplayStatus, 4, 0);   // display off
            Assert.True(r.IO.Reads.Count > readsBefore);                                       // going away reads what is open
            r.Wait(8 * 3600);
            SendTo.PowerSetting(notifier.Hwnd, SessionNotifier.SessionDisplayStatus, 4, 1);    // display on
            r.Engine.Ring(Browser.Helium);
            Assert.Empty(r.Library.Data.Items);                                                // the night was not reading time
        }
        finally { thread.Quit(); runner.Join(10_000); }
    }

    [Fact]
    public void SetSavesFeedHomesRingsTheWatchedBrowsersOnlyOnAChange()
    {
        var r = new Rig();
        r.Engine.Start();
        int reads = r.IO.Reads.Count;
        r.Engine.SetSavesFeedHomes(true);
        Assert.True(r.Engine.SavesFeedHomes);
        Assert.Equal(reads + 1, r.IO.Reads.Count);
        r.Engine.SetSavesFeedHomes(true);
        Assert.Equal(reads + 1, r.IO.Reads.Count);
    }

    [Fact]
    public void SettingsChangesAreReportedWithTheWholeRecord()
    {
        var r = new Rig(new Settings(Onboarded: true, Sort: LibrarySort.Title));
        var seen = new List<Settings>();
        r.Engine.SettingsChanged += seen.Add;
        r.Engine.SetPaused(true);
        r.Engine.SetSavesFeedHomes(true);
        Assert.Equal(new Settings(Onboarded: true, Paused: true, Sort: LibrarySort.Title), seen[0]);
        Assert.Equal(new Settings(Onboarded: true, Paused: true, SavesFeedHomes: true, Sort: LibrarySort.Title), seen[1]);
    }

    [Fact]
    public void OpenCopiesFollowTheJudge()
    {
        var r = new Rig();
        ReadResult current = Ok(T(Product, active: true), T(Product));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        Assert.Equal(2, Assert.Single(r.Engine.OpenCopies).Value);
        current = Ok(T(Product, active: true));
        r.Engine.Ring(Browser.Helium);
        Assert.Equal(1, Assert.Single(r.Engine.OpenCopies).Value);
    }

    [Fact]
    public void AWindowOfABrowserNotFoundAtStartMakesItInstalledAndReadsIt()
    {
        var r = new Rig();
        r.Engine.Start();
        r.IO.Owners[77] = Browser.Chrome;
        r.Engine.OnEvent(WatchEventKind.WindowShown, 77);
        Assert.Equal(BrowserStatus.NotRunning, r.Engine.Status[Browser.Chrome]);
        Assert.Contains(Browser.Chrome, r.IO.Reads);
    }

    [Fact]
    public void NameChangedAndWindowDestroyedRingTheOwningBrowser()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.IO.Known[Browser.Helium] = [100];
        r.IO.Owners[100] = Browser.Helium;
        r.Engine.Start();
        int reads = r.IO.Reads.Count;
        r.Engine.OnEvent(WatchEventKind.NameChanged, 100);
        Assert.Equal(reads + 1, r.IO.Reads.Count);
        r.IO.Known[Browser.Helium] = [];   // the window is gone: the next read forgets it
        r.Engine.OnEvent(WatchEventKind.WindowDestroyed, 100);
        Assert.Equal(reads + 2, r.IO.Reads.Count);
        r.Engine.OnEvent(WatchEventKind.WindowDestroyed, 100);   // forgotten once reported
        Assert.Equal(reads + 2, r.IO.Reads.Count);
    }

    [Fact]
    public void WakeIsWhatTheHostDebounces()
    {
        var r = new Rig();
        r.IO.Owners[100] = Browser.Helium;
        r.Engine.Start();
        var woken = new List<Browser>();
        r.Engine.Wake = woken.Add;
        int reads = r.IO.Reads.Count;
        r.Engine.OnEvent(WatchEventKind.NameChanged, 100);
        Assert.Equal([Browser.Helium], woken.ToArray());
        Assert.Equal(reads, r.IO.Reads.Count);
    }

    [Fact]
    public void StopFlushesAndAfterwardsNothingIsReadOrSaved()
    {
        var saves = 0;
        var r = new Rig(save: _ => saves++);
        r.Engine.Start();
        r.Library.Save("https://a.com/1", "One", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Helium, T0);
        r.Engine.Stop(flush: true);
        Assert.Equal(1, saves);
        int reads = r.IO.Reads.Count;
        r.Engine.RingAll();
        r.Library.Save("https://a.com/2", "Two", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Helium, T0);
        r.Host.Fire();
        Assert.Equal(reads, r.IO.Reads.Count);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void HistoryIsAskedForNewlySavedPagesAndTheAnswersAreApplied()
    {
        var r = new Rig();
        r.IO.OnHistory = links => links.ToDictionary(l => l, _ => new HistoryResult(T0.AddDays(-3), 4));
        ReadResult current = Ok(T(Product, active: true));
        r.IO.OnRead = _ => current;
        r.Engine.Start();
        r.Wait(130); r.Engine.Ring(Browser.Helium);
        Assert.Equal([Product], r.IO.HistoryAsks.Single().ToArray());
        var item = Assert.Single(r.Library.Data.Items);
        Assert.Equal(T0.AddDays(-3), item.OpenedAt);
        Assert.Equal(4, item.HistoryVisits);
    }

    [Fact]
    public void TheStartBackfillRemembersMissesSoTheNextLaunchDoesNotAskAgain()
    {
        var library = new Library();
        library.Save("https://a.com/1", "One", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Chrome, T0);
        var r = new Rig(library: library);
        r.Engine.Start();
        Assert.Equal(["https://a.com/1"], r.IO.HistoryAsks.Single().ToArray());   // Chrome's pages are asked about too
        Assert.Empty(library.LinksMissingHistory());
    }

    // The sleep and lock reasons: the engine is away until every reason has ended, and a judge made while away starts away.

    [Fact]
    public void ANightAsleepIsNotReadingTime()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.Engine.Start();
        r.Wait(5); r.Engine.SetAway("sleep", true);
        r.Wait(8 * 3600); r.Engine.SetAway("sleep", false);
        r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Library.Data.Items);
        r.Wait(130); r.Engine.Ring(Browser.Helium);
        Assert.Single(r.Library.Data.Items);
    }

    [Fact]
    public void AwayLastsUntilEveryReasonHasEnded()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.Engine.Start();
        r.Wait(5); r.Engine.SetAway("lock", true); r.Engine.SetAway("sleep", true);
        r.Wait(3600); r.Engine.SetAway("sleep", false);   // woken, still locked
        r.Engine.Ring(Browser.Helium);
        r.Wait(3600); r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Library.Data.Items);
        r.Engine.SetAway("lock", false);
        r.Engine.Ring(Browser.Helium);
        r.Wait(130); r.Engine.Ring(Browser.Helium);
        Assert.Single(r.Library.Data.Items);
    }

    [Fact]
    public void AJudgeMadeWhileAwayStartsAway()
    {
        var r = new Rig();
        r.IO.OnRead = _ => Ok(T(Product, active: true));
        r.Engine.Start();
        r.Wait(5); r.Engine.SetAway("lock", true);
        r.Engine.SetPaused(true);
        r.Engine.SetPaused(false);   // a new judge, made while locked
        r.Wait(8 * 3600); r.Engine.SetAway("lock", false);
        r.Engine.Ring(Browser.Helium);
        Assert.Empty(r.Library.Data.Items);   // the night is not reading time
    }

    // The list on disk: what the engine may and may not write.

    [Fact]
    public void AListThatCouldNotBeReadIsNeverWrittenOver()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var original = LibraryJson.Encode(new LibraryData());
        File.WriteAllBytes(paths.Library, original);
        using var lockedByAnother = new FileStream(paths.Library, FileMode.Open, FileAccess.Read, FileShare.None);

        var opened = EngineHost.Open(paths);
        Assert.Null(opened.Store);                       // Loaded.Writable was false
        Assert.False(string.IsNullOrEmpty(opened.Notice));

        var r = new Rig(library: opened.Library);
        var engine = new LaterEngine(opened.Library, new Judge(opened.Library), r.IO, r.Host, opened.Store, opened.Settings, () => r.Now, opened.Notice);
        opened.Library.Save("https://a.com/1", "One", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Helium, T0);
        Assert.Empty(r.Host.Shots);                      // no save is even scheduled
        engine.Flush();                                  // and a flush does nothing
        engine.Stop(flush: true);
        Assert.Equal(opened.Notice, engine.Notice);

        lockedByAnother.Dispose();
        Assert.Equal(original, File.ReadAllBytes(paths.Library));
        Assert.Equal([Path.GetFileName(paths.Library)], Directory.GetFiles(dir.Path).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public void AReadableListIsSavedByTheEngine()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllBytes(paths.Library, LibraryJson.Encode(new LibraryData()));
        var opened = EngineHost.Open(paths);
        Assert.NotNull(opened.Store);
        Assert.Null(opened.Notice);
        var r = new Rig(library: opened.Library);
        var engine = new LaterEngine(opened.Library, new Judge(opened.Library), r.IO, r.Host, opened.Store, opened.Settings, () => r.Now, opened.Notice);
        opened.Library.Save("https://a.com/1", "One", new Verdict(true, Kind.Reading, "a.com/*"), Browser.Helium, T0);
        r.Host.Fire();
        engine.Stop(flush: true);
        var reloaded = new LibraryStore(paths.Library).Load();
        Assert.Equal(["https://a.com/1"], reloaded.Data.Items.Select(i => i.Url).ToArray());
    }
}
