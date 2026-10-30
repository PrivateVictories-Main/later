using System.Diagnostics;
using Later.Core;
using Later.Windows.Reader;

namespace Later.Windows.Engine;

public enum BrowserStatus { NotInstalled, NotRunning, Watching, ReadFailed }

/// The engine thread and the work it hands off. `RunBackground` runs on the thread pool (MTA); a stand-in runs it at once.
public interface IEngineHost
{
    void Post(Action a);
    IDisposable After(TimeSpan delay, Action a);
    void RunBackground(Action work);
}

/// What the engine asks of the outside world about a browser, so tests can stand in for it.
public interface IBrowserIO
{
    bool IsInstalled(Browser b);
    ReadResult Read(Browser b);                                                   // any thread; the real one serializes
    Dictionary<string, HistoryResult> History(IReadOnlyList<string> links);       // any thread
    Dictionary<string, HashSet<int>> VisitDays(DateTime since);                  // any thread
    IReadOnlyCollection<nint> KnownWindows(Browser b);
    void BrowserQuit(Browser b);
    Browser? FrontBrowser();
    Browser? BrowserOfWindow(nint hwnd);
}

/// Glue: wake-up (hook, app switch, wake from sleep) → reader → judge → library → file. Everything runs on the engine thread
/// except the reads, the history questions and the file writes. The only timers are one-shots: the save coalescer, a
/// retry when a read could not resolve every tab, and, while a page could still be saved by time alone, one read at the moment
/// it would be (so a page left open or read for a long while is saved though the browser reports nothing).
public sealed class Engine
{
    static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1), RetryDelay = TimeSpan.FromSeconds(15);
    static readonly TimeSpan DeadlineSlack = TimeSpan.FromSeconds(1);
    const int MaxRetries = 2;
    const string SaveFailedNotice = "Later couldn't save its list. It'll try again with your next change.";

    readonly Library library;
    readonly IBrowserIO io;
    readonly IEngineHost host;
    readonly Action<LibraryData>? save;
    readonly Func<DateTime> clock;
    readonly string? loadNotice;
    Judge judge;
    Settings settings;

    readonly Dictionary<Browser, BrowserStatus> status = [];
    readonly HashSet<Browser> reading = [], readAgain = [];
    /// Bumped on pause and stop: a read that began before then belongs to a judge or session that's gone.
    readonly Dictionary<Browser, int> generation = [], retries = [];
    /// Consecutive reads of a browser with a window the reader could not see into, across wake-ups.
    readonly Dictionary<Browser, int> unrecognized = [];
    readonly Dictionary<Browser, IDisposable> retryTimers = [], deadlineTimers = [];
    readonly Dictionary<nint, Browser> windowOwner = [];
    /// The reasons nobody is looking: "sleep", "screen" (display off) and "lock". Away until all have ended.
    readonly HashSet<string> away = [];
    IReadOnlyDictionary<string, int> openCopies = new Dictionary<string, int>();
    bool started, stopped, saveScheduled, saveFailed;
    /// The day (see `Familiarity.Today`) the history was last asked which days each site was visited.
    int? visitDaysAsOf;
    IDisposable? saveTimer;

    // The writer: the newest copy wins, and nothing older is written after something newer.
    readonly object writeGate = new(), diskGate = new();
    LibraryData? pending; long pendingSequence, sequence, written;
    bool writing;
    /// Tests only: runs in the write loop after it took its data and before it asks for the disk.
    internal Action? BeforeDiskWrite;

    public Engine(Library library, Judge judge, IBrowserIO io, IEngineHost host, LibraryStore? store, Settings settings, Func<DateTime>? clock = null, string? loadNotice = null)
        : this(library, judge, io, host, store is null ? null : (Action<LibraryData>)store.Save, settings, clock, loadNotice) { }

    internal Engine(Library library, Judge judge, IBrowserIO io, IEngineHost host, Action<LibraryData>? save, Settings settings, Func<DateTime>? clock, string? loadNotice)
    {
        this.library = library; this.judge = judge; this.io = io; this.host = host; this.save = save;
        this.settings = settings; this.clock = clock ?? (() => DateTime.UtcNow); this.loadNotice = loadNotice;
        Wake = Ring;
        foreach (var b in BrowserInfo.All) status[b] = io.IsInstalled(b) ? BrowserStatus.NotRunning : BrowserStatus.NotInstalled;
        library.Changed += OnLibraryChanged;
    }

    public Library Library => library;
    /// Where the History's answer is kept between engines; the owner of the process passes `VisitDaysCache.Shared`.
    public VisitDaysCache DaysCache { get; init; } = new();
    public IReadOnlyDictionary<Browser, BrowserStatus> Status => new Dictionary<Browser, BrowserStatus>(status);
    public bool Paused => settings.Paused;
    public bool SavesFeedHomes => settings.SavesFeedHomes;
    public Settings Settings => settings;
    /// The load notice, or, over it, the save-failed notice (which goes once saving works again).
    public string? Notice => saveFailed ? SaveFailedNotice : loadNotice;
    public IReadOnlyDictionary<string, int> OpenCopies => openCopies;
    /// Whether this engine has a file to save to; false for a list that must not be written (see `EngineHost.Open`).
    public bool CanSave => save is not null;
    /// The last save failed (and none has worked since).
    public bool SaveFailed => saveFailed;
    /// Raised when saving starts failing (true) or works again (false), for a process with no window to say so in.
    public event Action<bool>? SaveFailedChanged;

    /// Raised when a status, the notice, the copies or the pause changed.
    public event Action? StateChanged;
    /// Raised after a read was applied, so whatever follows the browser's windows can look again.
    public event Action<Browser>? ReadApplied;
    /// Raised when a setting changed, for the owner to keep.
    public event Action<Settings>? SettingsChanged;

    /// How a browser's change reaches `Ring`. The host puts its per-browser debounce here; by default a wake-up reads at once.
    public Action<Browser> Wake { get; set; }

    // MARK: Start and stop

    public void Start()
    {
        if (started || stopped) return;
        started = true;
        library.Prune(clock());
        judge = MakeJudge();
        BackfillHistory();
        RefreshVisitDays();
        RingFrontFirst();
    }

    /// Stops reading and saving. With `flush`, the list is written first (a failure is the notice, not an exception).
    public void Stop(bool flush)
    {
        if (stopped) return;
        stopped = true;
        foreach (var b in BrowserInfo.All) generation[b] = generation.GetValueOrDefault(b) + 1;
        foreach (var t in retryTimers.Values) t.Dispose();
        retryTimers.Clear();
        CancelDeadlines();
        saveTimer?.Dispose(); saveTimer = null;
        library.Changed -= OnLibraryChanged;
        if (flush)
        {
            try { Flush(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // reported by SaveFailedChanged
        }
        // Whatever the flush did, a write that was queued earlier must not land after this: the lock is let go next, and the
        // process that takes it writes the list from then on.
        lock (diskGate) written = long.MaxValue;
    }

    /// Writes the list now, waiting for it to finish. Throws if the write failed. Without a store it does nothing: the notice
    /// already says saves won't be kept.
    public void Flush()
    {
        if (save is null) return;
        var data = library.CopyData();
        long mine;
        lock (writeGate) { pending = null; mine = ++sequence; }
        try { lock (diskGate) { save(data); written = Math.Max(written, mine); } }
        catch (IOException) { PerfLog.Log.SaveFailed(); SaveFinished(false); throw; }
        catch (UnauthorizedAccessException) { PerfLog.Log.SaveFailed(); SaveFinished(false); throw; }
        SaveFinished(true);
    }

    // MARK: Wake-ups

    /// From the event hooks (already on the engine thread).
    public void OnEvent(WatchEventKind kind, nint hwnd)
    {
        if (!started || stopped) return;
        switch (kind)
        {
            case WatchEventKind.Foreground:
                // The new foreground window may be no browser; then the browser that was left is read.
                FrontAppChanged(io.BrowserOfWindow(hwnd) ?? io.FrontBrowser());
                break;
            case WatchEventKind.NameChanged:
                if (io.BrowserOfWindow(hwnd) is { } named) { Seen(named); Wake(named); }
                break;
            case WatchEventKind.WindowShown:
                if (io.BrowserOfWindow(hwnd) is { } shown)
                {
                    Seen(shown);
                    if (io.KnownWindows(shown).Count == 0 && !settings.Paused) judge.BrowserLaunched(shown);   // until its first read it may be restoring pages this judge can't see
                    Wake(shown);
                }
                break;
            case WatchEventKind.WindowDestroyed:
                if (windowOwner.Remove(hwnd, out var owner)) Wake(owner);
                break;
        }
    }

    /// A browser that shows windows is installed, wherever it lives.
    void Seen(Browser b)
    {
        if (status[b] != BrowserStatus.NotInstalled) return;
        status[b] = BrowserStatus.NotRunning;
        judge.Watch(b);
        StateChanged?.Invoke();
    }

    public void RingAll()
    {
        foreach (var b in BrowserInfo.All) Ring(b);
    }

    /// A wake-up: read the browser now (a change that arrives during a read causes one more).
    public void Ring(Browser b)
    {
        CancelRetry(b);
        retries[b] = 0;
        Read(b);
    }

    void Read(Browser b)
    {
        if (!started || stopped || settings.Paused || status[b] == BrowserStatus.NotInstalled) return;
        if (!reading.Add(b)) { readAgain.Add(b); return; }
        var readGeneration = generation.GetValueOrDefault(b);
        var timer = Stopwatch.StartNew();
        host.RunBackground(() =>
        {
            ReadResult result;
            try { result = io.Read(b); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                PerfLog.Log.ReadFailed(b.Wire(), e.GetType().Name);   // the class only
                result = new ReadResult(ReadStatus.Failed, null, 0, 0, 0);
            }
            var readMs = timer.Elapsed.TotalMilliseconds;
            host.Post(() => FinishRead(b, readGeneration, result, readMs));
        });
    }

    void FinishRead(Browser b, int readGeneration, ReadResult result, double readMs)
    {
        reading.Remove(b);
        if (!stopped && !settings.Paused && generation.GetValueOrDefault(b) == readGeneration) Apply(b, result, readMs);
        if (readAgain.Remove(b)) Read(b);   // also how a ring during a stale read gets through
    }

    void Apply(Browser b, ReadResult result, double readMs)
    {
        switch (result.Status)
        {
            case ReadStatus.Ok:
                // A normal window the read could not see into may hold tabs: judging without it would count them as closed. Look
                // again; if it stays unreadable, this is the best view there is.
                // The count runs over consecutive reads of this browser, wake-ups included: a window that stays unclassifiable must
                // not keep the judge waiting for as long as the user is active.
                if (result.UnrecognizedWindows > 0)
                {
                    int streak = unrecognized[b] = unrecognized.GetValueOrDefault(b) + 1;
                    if (streak <= MaxRetries && ScheduleRetry(b)) return;
                }
                else unrecognized[b] = 0;
                RefreshVisitDays();
                var judgeTimer = Stopwatch.StartNew();
                var ingestedAt = clock();
                judge.Ingest(result.Snapshot!, ingestedAt);
                var judgeMs = judgeTimer.Elapsed.TotalMilliseconds;
                LookUpHistory(library.TakeAwaitingHistory());
                PublishCopies();
                SetStatus(b, BrowserStatus.Watching);
                foreach (var gone in windowOwner.Where(o => o.Value == b).Select(o => o.Key).ToList()) windowOwner.Remove(gone);
                foreach (var hwnd in io.KnownWindows(b)) windowOwner[hwnd] = b;
                PerfLog.Log.Ring(b.Wire(), readMs, judgeMs, result.Snapshot!.Tabs.Count, result.PrivateWindowsSkipped, result.UnresolvedTabs, library.Data.Items.Count);
                ReadApplied?.Invoke(b);
                if (result.UnresolvedTabs == 0 || !ScheduleRetry(b)) CancelRetry(b);
                ArmDeadline(b, ingestedAt);
                break;
            case ReadStatus.NotRunning:
                CancelRetry(b);
                CancelDeadline(b);
                unrecognized[b] = 0;
                judge.BrowserQuit(b, clock());   // not running: known to have nothing open
                foreach (var gone in windowOwner.Where(o => o.Value == b).Select(o => o.Key).ToList()) windowOwner.Remove(gone);
                PublishCopies();
                SetStatus(b, BrowserStatus.NotRunning);
                ReadApplied?.Invoke(b);
                break;
            case ReadStatus.Transient:
                // The next wake-up catches up. A page that time alone could save must not wait on one that may never come (a
                // minimized browser sends none): try again, with the same bound as for unresolved tabs, then wait for a real event.
                // MUTANT: no retry
                break;
            default:
                CancelDeadline(b);
                judge.BrowserUnreadable(b);
                PublishCopies();
                SetStatus(b, BrowserStatus.ReadFailed);
                break;
        }
    }

    /// One more read in 15 s, at most twice per wake-up. False when the retries are used up.
    bool ScheduleRetry(Browser b)
    {
        if (retries.GetValueOrDefault(b) >= MaxRetries) return false;
        retries[b] = retries.GetValueOrDefault(b) + 1;
        CancelRetry(b);
        retryTimers[b] = host.After(RetryDelay, () => { PerfLog.Log.Wake("retry"); retryTimers.Remove(b); Read(b); });
        return true;
    }

    void CancelRetry(Browser b)
    {
        if (retryTimers.Remove(b, out var timer)) timer.Dispose();
    }

    /// While the judge holds a page of this browser that time alone could still save (off screen for a minute, or on screen
    /// long enough), one read is armed for that moment, plus a second. It replaces the one armed before; with nothing pending
    /// there is no timer at all.
    void ArmDeadline(Browser b, DateTime now)
    {
        CancelDeadline(b);
        if (judge.NextDeadline(b, now) is not { } at) return;
        var delay = at - now + DeadlineSlack;
        PerfLog.Log.Armed(b.Wire(), delay.TotalSeconds);
        deadlineTimers[b] = host.After(delay, () => { PerfLog.Log.Wake("deadline"); deadlineTimers.Remove(b); Ring(b); });
    }

    void CancelDeadline(Browser b)
    {
        if (deadlineTimers.Remove(b, out var timer)) timer.Dispose();
    }

    void CancelDeadlines()
    {
        foreach (var timer in deadlineTimers.Values) timer.Dispose();
        deadlineTimers.Clear();
    }

    /// Switching into or out of a browser is a moment to look: the one being left and the one being entered are read now.
    public void FrontAppChanged(Browser? browser)
    {
        if (settings.Paused) return;
        var left = judge.FrontBrowser;
        judge.SetFrontBrowser(browser, clock());
        if (left is { } leaving) Ring(leaving);
        if (browser is { } entering && entering != left) Ring(entering);
    }

    /// The front browser (read by the app switch) and then every other, once each; the front one is taken before any read ends.
    void RingFrontFirst()
    {
        var front = io.FrontBrowser();
        FrontAppChanged(front);
        foreach (var b in BrowserInfo.All) if (b != front) Ring(b);
    }

    /// Asleep, display off or locked: `reason` is "sleep", "screen" or "lock". Time away is neither reading nor leaving a page open.
    public void SetAway(string reason, bool on)
    {
        if (on) away.Add(reason); else away.Remove(reason);
        judge.SetAway(away.Count > 0, clock());
        if (away.Count > 0) CancelDeadlines();   // nothing can be saved by time while away; the wake reads again
    }

    // MARK: Actions

    public void SetPaused(bool value)
    {
        settings = settings with { Paused = value };
        SettingsChanged?.Invoke(settings);
        foreach (var b in BrowserInfo.All) { generation[b] = generation.GetValueOrDefault(b) + 1; CancelRetry(b); }
        CancelDeadlines();
        if (value) { StateChanged?.Invoke(); return; }
        // Start judging afresh: the judge's last view of the screen is stale, and paused time isn't reading time.
        judge = MakeJudge();
        PublishCopies();
        RingFrontFirst();
        StateChanged?.Invoke();
    }

    public void SetSavesFeedHomes(bool value)
    {
        if (value == settings.SavesFeedHomes) return;
        settings = settings with { SavesFeedHomes = value };
        SettingsChanged?.Invoke(settings);
        judge.SavesFeedHomes = value;   // judges every open tab again at the next read
        foreach (var b in BrowserInfo.All) Ring(b);
        StateChanged?.Invoke();
    }

    Judge MakeJudge()
    {
        var made = new Judge(library, BrowserInfo.All.Where(b => status[b] != BrowserStatus.NotInstalled).ToHashSet(), settings.SavesFeedHomes);
        if (away.Count > 0) made.SetAway(true, clock());
        return made;
    }

    // MARK: History

    /// Asks the history which days of the last 28 each site was visited, off the engine thread: as the engine starts, and again
    /// at the first read after the local date changes. Then pages saved before Regulars that are places on regular sites move
    /// there, once. Nothing is read in between (no timer), and a failed or empty answer keeps the last good one. Logs counts
    /// and time only.
    void RefreshVisitDays()
    {
        var now = clock();
        int today = Familiarity.Today(now);
        if (visitDaysAsOf == today) return;
        visitDaysAsOf = today;
        var since = now.ToLocalTime().Date.AddDays(-Familiarity.Window);   // the start of the day 28 days back
        var timer = Stopwatch.StartNew();
        host.RunBackground(() =>
        {
            PerfLog.Log.Wake("visitdays");
            var days = new VisitDaysCache().Days(since, io.VisitDays);   // MUTANT: no shared cache
            host.Post(() =>
            {
                if (stopped) return;
                library.SetHistoryDays(days);
                PerfLog.Log.VisitDays(days.Count, library.RegularSiteCount(today), timer.Elapsed.TotalMilliseconds);
                var (migrated, of) = library.MigrateRegulars(today);
                if (of > 0) PerfLog.Log.RegularsMigrated(migrated, of);
            });
        });
    }

    /// Once, as the engine starts: when the history says saved pages were opened, for those it doesn't say yet and wasn't
    /// already asked about at an earlier launch.
    void BackfillHistory() => LookUpHistory(library.LinksMissingHistory(), remembersMisses: true);

    /// Asks the history about these links off the engine thread, then records the answers. Only for pages that are new or
    /// still unknown, never for a page on every read. Logs counts and time only.
    void LookUpHistory(IReadOnlyList<string> links, bool remembersMisses = false)
    {
        if (links.Count == 0) return;
        var timer = Stopwatch.StartNew();
        host.RunBackground(() =>
        {
            PerfLog.Log.Wake("history");
            Dictionary<string, HistoryResult> results;
            try { results = io.History(links); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { results = []; }
            host.Post(() =>
            {
                if (stopped) return;
                var changed = library.ApplyHistory(results);
                // Only the start-up backfill remembers a miss: a page saved moments ago may not be in the history file yet.
                if (remembersMisses) library.MarkHistoryChecked(links);
                PerfLog.Log.HistoryAsked(links.Count, results.Count, changed, timer.Elapsed.TotalMilliseconds);
            });
        });
    }

    // MARK: State

    void PublishCopies()
    {
        var copies = judge.OpenCopies;
        if (copies.Count == openCopies.Count && copies.All(c => openCopies.TryGetValue(c.Key, out var n) && n == c.Value)) return;
        openCopies = copies;
        StateChanged?.Invoke();
    }

    /// Only a real change is announced: every read repeats the status.
    void SetStatus(Browser b, BrowserStatus value)
    {
        if (status[b] == value) return;
        status[b] = value;
        StateChanged?.Invoke();
    }

    // MARK: Saving the list

    void OnLibraryChanged()
    {
        if (saveScheduled || save is null || stopped) return;
        saveScheduled = true;
        // One-shot, armed only after a change: merges a burst of changes into one write.
        saveTimer = host.After(SaveDelay, SaveNow);
    }

    void SaveNow()
    {
        PerfLog.Log.Wake("save");
        saveScheduled = false; saveTimer = null;
        if (save is null || stopped) return;
        var data = library.CopyData();
        lock (writeGate)
        {
            pending = data; pendingSequence = ++sequence;
            if (writing) return;
            writing = true;
        }
        host.RunBackground(WriteLoop);
    }

    void WriteLoop()
    {
        while (true)
        {
            LibraryData data; long mine;
            lock (writeGate)
            {
                if (pending is null) { writing = false; return; }
                data = pending; mine = pendingSequence; pending = null;
            }
            BeforeDiskWrite?.Invoke();
            bool? ok = null;   // nothing to report when a newer write (or the stop) made this one unnecessary
            lock (diskGate)
            {
                if (mine > written)
                {
                    try { save!(data); written = mine; ok = true; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; PerfLog.Log.SaveFailed(); }
                }
            }
            if (ok is { } result) host.Post(() => SaveFinished(result));
        }
    }

    void SaveFinished(bool ok)
    {
        if (saveFailed != ok) return;
        saveFailed = !ok;
        SaveFailedChanged?.Invoke(saveFailed);
        StateChanged?.Invoke();
    }
}
