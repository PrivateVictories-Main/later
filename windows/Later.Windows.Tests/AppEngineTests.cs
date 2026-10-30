using System.Diagnostics;
using Later.App;
using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Native;
using Later.Windows.Platform;
using Xunit;
using LaterEngine = Later.Windows.Engine.Engine;

namespace Later.Windows.Tests;

/// The window app's engine host (the lock handoff, the snapshots, the exit), compiled into the tests from the app project, with a
/// quiet engine in place of the browser hooks and a thread playing the background agent.
public sealed class AppEngineTests : IDisposable
{
    sealed class QuietIO : IBrowserIO
    {
        public bool IsInstalled(Browser b) => false;
        public ReadResult Read(Browser b) => new(ReadStatus.NotRunning, null, 0, 0, 0);
        public Dictionary<string, HistoryResult> History(IReadOnlyList<string> links) => [];
        public Dictionary<string, HashSet<int>> VisitDays(DateTime since) => [];
        public IReadOnlyCollection<nint> KnownWindows(Browser b) => [];
        public void BrowserQuit(Browser b) { }
        public Browser? FrontBrowser() => null;
        public Browser? BrowserOfWindow(nint hwnd) => null;
    }

    static readonly Verdict Reading = new(true, Kind.Reading, "a.example.com/*");

    readonly TempDir dir = new();
    readonly AppPaths paths;
    readonly LibraryViewModel model = new();
    readonly object uiGate = new();
    readonly List<long> snapshotTimes = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<AppEngine> apps = [];
    int uiCalls, forced;

    public AppEngineTests()
    {
        paths = new AppPaths(dir.Path);
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LibraryViewModel.Current)) snapshotTimes.Add(clock.ElapsedMilliseconds); };
    }

    public void Dispose()
    {
        foreach (var app in apps) { app.Shutdown(); app.WaitForExit(TimeSpan.FromSeconds(10)); }
        dir.Dispose();
    }

    bool Ui(Action work)
    {
        Interlocked.Increment(ref uiCalls);
        lock (uiGate) work();
        return true;
    }

    int Snapshots { get { lock (uiGate) return snapshotTimes.Count; } }

    AppEngine NewApp(AppEngine.StartEngine start, double handoffSeconds = 5, double flushSeconds = 5, double exitSeconds = 60)
    {
        var app = new AppEngine(paths, model, Ui, () => { }, start)
        {
            HandoffTimeout = TimeSpan.FromSeconds(handoffSeconds),
            FlushTimeout = TimeSpan.FromSeconds(flushSeconds),
            ExitLimit = TimeSpan.FromSeconds(exitSeconds),
            ForceExit = () => Interlocked.Increment(ref forced),
        };
        apps.Add(app);
        return app;
    }

    void SetUp() => File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");

    static AppEngine.Started Quiet(IEngineHost host, Library library, Action<LibraryData>? save = null)
    {
        var engine = new LaterEngine(library, new Judge(library), new QuietIO(), host, save, new Settings(), null, null);
        engine.Start();
        return new AppEngine.Started(engine, () => engine.Stop(flush: false));
    }

    static bool WaitFor(Func<bool> condition, int ms = 10_000)
    {
        var end = Environment.TickCount64 + ms;
        while (!condition()) { if (Environment.TickCount64 > end) return false; Thread.Sleep(10); }
        return true;
    }

    /// A thread that holds the engine lock as the background agent does, until told to let go. The lock is the app's to ask for.
    sealed class FakeAgent : IDisposable
    {
        readonly ManualResetEventSlim held = new(), letGo = new();
        readonly Thread thread;

        public FakeAgent(string suffix)
        {
            thread = new Thread(() =>
            {
                using var gate = EngineLock.Open(suffix);
                gate.TryAcquire(TimeSpan.Zero);
                held.Set(); letGo.Wait();
                gate.Release();
            });
            thread.Start();
            Assert.True(held.Wait(10_000));
        }

        public void LetGo() => letGo.Set();

        public void Dispose() { letGo.Set(); thread.Join(10_000); held.Dispose(); letGo.Dispose(); }
    }

    // MARK: Closing

    [Fact]
    public void ClosingWhileWaitingForTheLockStartsNoEngineAndDoesNotWaitItOut()
    {
        SetUp();
        using var agent = new FakeAgent(paths.InstanceSuffix);
        using var watcher = EngineLock.Open(paths.InstanceSuffix);
        int starts = 0;
        var app = NewApp((_, h, _) => { Interlocked.Increment(ref starts); return Quiet(h, new Library()); }, handoffSeconds: 30);
        app.Start();
        Assert.True(WaitFor(() => NativeApi.WaitForSingleObject(watcher.HandoffHandle, 0) == NativeApi.WaitObject0), "the app never asked for the lock");   // it is in the wait
        var timer = Stopwatch.StartNew();
        app.Shutdown();
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(10)), "the app's thread is still waiting for the lock");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.ElapsedMilliseconds} ms");
        Assert.Equal(0, starts);
    }

    [Fact]
    public void ALockWonJustAsTheWindowClosesIsGivenBackWithoutStartingTheEngine()
    {
        SetUp();
        using var agent = new FakeAgent(paths.InstanceSuffix);
        using var watcher = EngineLock.Open(paths.InstanceSuffix);
        int starts = 0;
        var app = NewApp((_, h, _) => { Interlocked.Increment(ref starts); return Quiet(h, new Library()); }, handoffSeconds: 30);
        app.Start();
        Assert.True(WaitFor(() => NativeApi.WaitForSingleObject(watcher.HandoffHandle, 0) == NativeApi.WaitObject0));
        app.Shutdown();
        agent.LetGo();
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, starts);
        Assert.True(watcher.TryAcquire(TimeSpan.FromSeconds(5), out var abandoned), "the lock was never given back");
        Assert.False(abandoned);
        watcher.Release();
    }

    [Fact]
    public void AFinalSaveThatHangsDoesNotHoldTheLockAndTheThreadStillEnds()
    {
        SetUp();
        var hang = new ManualResetEventSlim();   // not disposed: the abandoned save thread may still be leaving its wait when the test ends
        using var saving = new ManualResetEventSlim(); using var started = new ManualResetEventSlim();
        using var watcher = EngineLock.Open(paths.InstanceSuffix);
        var app = NewApp((_, h, _) =>
        {
            var engine = Quiet(h, new Library(), save: _ => { saving.Set(); hang.Wait(60_000); });
            started.Set();
            return engine;
        }, flushSeconds: 0.4);
        app.Start();
        Assert.True(started.Wait(10_000));
        var timer = Stopwatch.StartNew();
        app.Shutdown();
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(10)), "the exit waits on the hung save for ever");
        Assert.True(saving.IsSet, "the final save never ran, so this proved nothing");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.ElapsedMilliseconds} ms");
        Assert.True(watcher.TryAcquire(TimeSpan.FromSeconds(5), out var abandoned), "the lock is still held");
        Assert.False(abandoned);
        watcher.Release();
        hang.Set();
    }

    [Fact]
    public void AChangeMadeInTheWindowIsOnDiskOnceItIsClosedAndAnOrdinaryExitIsNotForced()
    {
        SetUp();
        using var started = new ManualResetEventSlim();
        Library? library = null;
        var app = NewApp((_, h, _) =>
        {
            var own = new Library();
            var engine = new LaterEngine(own, new Judge(own), new QuietIO(), h, new LibraryStore(paths.Library), new Settings());
            engine.Start();
            library = own;
            started.Set();
            return new AppEngine.Started(engine, () => engine.Stop(flush: false));
        }, exitSeconds: 1);
        app.Start();
        Assert.True(started.Wait(10_000));
        app.Post(() => library!.Save("https://a.example.com/articles/slow-web", "Slow web", Reading, Browser.Helium, DateTime.UtcNow));
        app.Shutdown();   // before the engine's own save timer (a second) could have written it
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Equal("Slow web", Assert.Single(new LibraryStore(paths.Library).Peek("").Data.Items).Title);
        Thread.Sleep(1500);
        Assert.Equal(0, Volatile.Read(ref forced));   // the watchdog went with the thread
    }

    [Fact]
    public void AThreadStuckAnywhereIsEndedByTheWatchdogAfterTheLimit()
    {
        SetUp();
        using var inStart = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var app = NewApp((_, h, _) => { inStart.Set(); release.Wait(60_000); return Quiet(h, new Library()); }, exitSeconds: 0.5);
        app.Start();
        Assert.True(inStart.Wait(10_000));
        app.Shutdown();
        Assert.True(WaitFor(() => Volatile.Read(ref forced) == 1, 5_000), "the process was not ended");
        release.Set();
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(10)));
    }

    // MARK: What the window sees

    [Fact]
    public void APopulatedListBehindAHeldLockIsNeverTakenForAnEmptyOne()
    {
        SetUp();
        var library = new Library();
        for (int i = 0; i < 3; i++) library.Save($"https://a.example.com/articles/{i}", $"Page {i}", Reading, Browser.Helium, DateTime.UtcNow);
        new LibraryStore(paths.Library).Save(library.Data);
        using var agent = new FakeAgent(paths.InstanceSuffix);   // never lets go in time
        var app = NewApp((_, h, _) => Quiet(h, new Library()), handoffSeconds: 1.5);
        lock (uiGate) model.Wake(DateTime.UtcNow);
        app.SetVisible(true);
        app.Start();
        var until = Stopwatch.StartNew();
        bool readOnly = false;
        while (until.Elapsed < TimeSpan.FromSeconds(10) && !readOnly)
        {
            lock (uiGate)
            {
                // What the list pane shows for "nothing saved at all": loaded, and no counts.
                Assert.False(model.IsLoaded && model.Counts.Count == 0, "the first-run welcome would show for a populated library");
                readOnly = model.IsLoaded && model.Engine.Notice is { } notice && notice.StartsWith("Later is busy saving", StringComparison.Ordinal);
            }
            Thread.Sleep(5);
        }
        Assert.True(readOnly, "the read-only list never came");
        lock (uiGate) Assert.Equal(3, model.Counts[LibrarySection.All]);
    }

    /// A quiet engine running with the window shown and the start settled; `Add` saves pages on the engine thread.
    (AppEngine App, Action<int> Add, Func<int> Pages) Running()
    {
        SetUp();
        var library = new Library();
        int added = 0;
        var app = NewApp((_, h, _) => Quiet(h, library));
        lock (uiGate) model.Wake(DateTime.UtcNow);
        app.SetVisible(true);
        app.Start();
        Assert.True(WaitFor(() => Snapshots >= 1));
        Thread.Sleep(500);   // whatever the start itself changed has been sent
        void Add(int n) => app.Post(() => { for (int i = 0; i < n; i++) library.Save($"https://a.example.com/articles/{added++}", "Page", Reading, Browser.Helium, DateTime.UtcNow); });
        return (app, Add, () => { lock (uiGate) return model.Counts.GetValueOrDefault(LibrarySection.All); });
    }

    [Fact]
    public void ABurstOfChangesIsOneSnapshot()
    {
        var (_, add, pages) = Running();
        int before = Snapshots;
        add(20);
        Assert.True(WaitFor(() => Snapshots > before));
        Thread.Sleep(400);
        Assert.Equal(before + 1, Snapshots);
        Assert.Equal(20, pages());   // and it has all of them
    }

    [Fact]
    public void ChangesSpreadOverTimeGoOutAtMostOncePerHundredMilliseconds()
    {
        var (_, add, pages) = Running();
        int before = Snapshots;
        for (int i = 0; i < 8; i++) { add(1); Thread.Sleep(30); }
        Assert.True(WaitFor(() => pages() == 8));
        Thread.Sleep(300);
        List<long> times;
        lock (uiGate) times = snapshotTimes.Skip(before).ToList();
        Assert.InRange(times.Count, 1, 5);   // 8 changes in about 250 ms
        for (int i = 1; i < times.Count; i++) Assert.True(times[i] - times[i - 1] >= 85, $"snapshots {times[i] - times[i - 1]} ms apart");
    }

    [Fact]
    public void NothingIsSentWhileTheWindowIsHiddenAndShowingSendsTheListAsItIsThen()
    {
        var (app, add, pages) = Running();
        app.SetVisible(false);
        lock (uiGate) model.Hide();
        int calls = Volatile.Read(ref uiCalls), snapshots = Snapshots;
        add(5);
        Thread.Sleep(600);
        Assert.Equal(calls, Volatile.Read(ref uiCalls));   // not even a state update
        Assert.Equal(snapshots, Snapshots);
        Assert.Equal(0, pages());
        app.SetVisible(true);
        lock (uiGate) model.Wake(DateTime.UtcNow);
        Assert.True(WaitFor(() => pages() == 5), "showing did not bring the list up to date");
    }
}
