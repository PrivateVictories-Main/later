using System.ComponentModel;
using System.Diagnostics;
using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Platform;
using LaterEngine = Later.Windows.Engine.Engine;

namespace Later.App;

/// The window app's side of watching. One thread of its own owns the engine (and the engine lock, which is thread-affine): it asks
/// the background agent for the list, starts the engine when the agent lets go, and hands the engine back at exit by releasing the
/// lock after a final save, which wakes the agent. The window sees only snapshots, at most one per 100 ms and none while hidden.
/// Until the first snapshot the window shows nothing of the list (the model is not loaded), never an empty list.
/// Without the lock (the agent is slow to answer) the list is shown read-only and asked for again whenever the window is shown or
/// activated. Before setup (Task 9) nothing is watched at all. Never starts an agent.
/// Closing is prompt: it ends a wait for the lock, skips an engine start not yet begun, bounds the final save, and, as the last resort,
/// ends the process (the lock is then abandoned, which the agent recovers from).
internal sealed class AppEngine : IDisposable
{
    /// The running engine and how to take it apart (without its final save, which `AppEngine` bounds itself).
    internal sealed record Started(LaterEngine Engine, Action Stop);

    internal delegate Started StartEngine(AppPaths paths, IEngineHost host, Action sessionEnded);

    static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(100);
    static readonly IReadOnlyDictionary<Browser, BrowserStatus> NoStatuses = new Dictionary<Browser, BrowserStatus>();
    static readonly IReadOnlyDictionary<string, int> NoCopies = new Dictionary<string, int>();
    const string BusyNotice = "Later is busy saving, so your list is read-only for now. It asks again each time you come back to this window.";
    const string NoLockNotice = "Later couldn't take charge of your list, so it is read-only for now.";

    readonly AppPaths paths;
    readonly LibraryViewModel model;
    readonly Func<Action, bool> ui;
    readonly Action exitApp;
    readonly StartEngine startEngine;
    readonly EngineThread thread = new();
    readonly ThreadHost host;
    readonly AgentStatusFile agentStatus;
    Thread? runner;
    Timer? watchdog;

    // Engine thread only:
    EngineLock? gate;
    Started? running;
    bool holding, stopping, onboarded;
    LibraryData? peeked;
    int peekVersion;
    string? readOnlyNotice, agentNotice;
    IDisposable? coalescer;
    (Started? Run, int Revision, object? Copies, int Peek) sent = (null, -1, null, -1);

    // Both threads:
    volatile bool visible, needsLock, retryQueued, closing;

    /// How long to wait for the agent to let go of the lock.
    internal TimeSpan HandoffTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// How long the final save may take before the lock is let go without it, and how long a closed window may take to end the process.
    internal TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan ExitLimit { get; init; } = TimeSpan.FromSeconds(10);
    internal Action ForceExit { get; init; } = () => Process.GetCurrentProcess().Kill();   // not Exit: that runs handlers that may be the thing that hangs

    /// `ui` runs an action on the window's thread (false when that thread is gone). `startEngine`: tests stand in for the hooks.
    public AppEngine(AppPaths paths, LibraryViewModel model, Func<Action, bool> ui, Action exitApp, StartEngine? startEngine = null)
    {
        this.paths = paths; this.model = model; this.ui = ui; this.exitApp = exitApp;
        this.startEngine = startEngine ?? StartWithHooks;
        host = new ThreadHost(thread);
        agentStatus = new AgentStatusFile(paths.AgentStatus);
    }

    static Started StartWithHooks(AppPaths paths, IEngineHost host, Action sessionEnded)
    {
        var started = EngineHost.Start(paths, host, sessionEnded);
        return new Started(started.Engine, () => started.Stop(flush: false));
    }

    public void Start()
    {
        runner = new Thread(() => { try { thread.Run(init: Init, waitHandles: () => [], signaled: _ => { }, onExit: OnExit); } finally { Dispose(); } }) { Name = "Later.EngineRunner", IsBackground = true };
        runner.Start();
    }

    /// Tests: whether the engine thread has ended (and with it the lock been let go) within `limit`.
    internal bool WaitForExit(TimeSpan limit) => runner?.Join(limit) ?? true;

    /// Tests: runs `work` on the engine thread.
    internal void Post(Action work) => thread.Post(work);

    /// The window was shown or hidden (minimized counts as hidden): showing sends the list as it is now, hiding stops sending.
    public void SetVisible(bool value)
    {
        if (visible == value) return;
        visible = value;
        PerfLog.Log.Shown(value);
        if (value) thread.Post(Publish);
    }

    /// The window was shown or activated: a read-only list asks for the engine once more.
    public void RetryLock()
    {
        if (!needsLock || retryQueued || closing) return;
        retryQueued = true;
        thread.Post(Take);
    }

    public void Resume() => thread.Post(() => running?.Engine.SetPaused(false));

    public void SetView(LibraryGrouping grouping, LibrarySort sort, bool reversed) => thread.Post(() => running?.Engine.SetView(grouping, sort, reversed));

    /// The window closed: stop waiting, save, let go of the lock (the agent takes over), and end the thread.
    public void Shutdown()
    {
        closing = true;
        thread.Quit();
        // Last resort: a thread stuck anywhere (a disk stall while starting, say) must not leave a windowless process holding the lock.
        watchdog ??= new Timer(_ => { PerfLog.Log.ExitForced(); ForceExit(); }, null, ExitLimit, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        watchdog?.Dispose();
        thread.Dispose();
    }

    void Init()
    {
        if (closing) return;
        onboarded = SettingsStore.Load(paths.Settings).Onboarded;
        if (!onboarded) { ShowListOnly(null); return; }   // before setup nothing watches and the agent does not run either
        try { gate = EngineLock.Open(paths.InstanceSuffix); }
        catch (Win32Exception) { ShowListOnly(NoLockNotice); return; }
        Take();
    }

    /// Asks for the engine and starts it. Engine thread: the mutex belongs to the thread that took it.
    /// Another ask made while this one waits is dropped (`retryQueued` stays set until it ends), so the waits never queue up.
    void Take()
    {
        try
        {
            if (running is not null || closing || gate is null || !onboarded) return;
            var timer = Stopwatch.StartNew();
            bool took;
            try { took = gate.AcquireForApp(HandoffTimeout, () => closing); }
            catch (Win32Exception) { ShowListOnly(NoLockNotice); return; }
            PerfLog.Log.Handoff(timer.Elapsed.TotalMilliseconds, took);
            holding |= took;   // OnExit lets go of it
            if (closing) return;   // the window closed while waiting: no engine, just give the lock back
            if (!took) { ShowListOnly(BusyNotice); return; }
            needsLock = false;
            agentNotice = agentStatus.Take()?.Notice;   // what the agent could not do while the window was closed; cleared here, written again if it goes on
            var started = startEngine(paths, host, () => { ui(() => exitApp()); thread.Quit(); });
            running = started;
            started.Engine.StateChanged += Changed;
            started.Engine.Library.Changed += Changed;
            Publish();
        }
        finally { retryQueued = false; }
    }

    /// The list as it is on disk, with nobody writing it from here.
    void ShowListOnly(string? notice)
    {
        needsLock = onboarded && gate is not null;
        readOnlyNotice = notice;
        peeked = new LibraryStore(paths.Library).Peek(notice ?? "").Data;
        peekVersion++;
        agentNotice = agentStatus.Read()?.Notice;
        Publish();
    }

    void Changed()
    {
        if (coalescer is not null || stopping) return;
        coalescer = host.After(Coalesce, () => { coalescer = null; Publish(); });
    }

    void Publish()
    {
        if (!visible || stopping) return;
        var engine = running?.Engine;
        var state = new EngineState(
            engine?.Status ?? NoStatuses,
            engine?.Paused ?? false,
            engine is null ? Join(agentNotice, readOnlyNotice) : engine.SaveFailed ? engine.Notice : Join(agentNotice, engine.Notice),
            engine?.CanSave ?? false,
            onboarded);
        var token = (running, engine?.Library.Revision ?? 0, (object?)engine?.OpenCopies, peekVersion);
        if (token == sent) { ui(() => model.SetEngineState(state)); return; }
        sent = token;
        var data = engine is null ? peeked ?? new LibraryData() : engine.Library.CopyData();
        var copies = engine is null ? NoCopies : new Dictionary<string, int>(engine.OpenCopies);
        ui(() => { model.SetSnapshot(data, copies); model.SetEngineState(state); });
    }

    static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + " " + b;

    /// On the engine thread after the loop ended: the final save (bounded), then the lock, which the agent is waiting for.
    void OnExit()
    {
        stopping = true;
        coalescer?.Dispose();
        if (running is { } r)
        {
            r.Engine.StateChanged -= Changed;
            r.Engine.Library.Changed -= Changed;
            r.Stop();
            FlushWithin(r.Engine, FlushTimeout);
        }
        if (holding) gate!.Release();
        gate?.Dispose();
    }

    /// The final save on a thread of its own, so a stalled disk delays the exit by `limit`, not for ever.
    static void FlushWithin(LaterEngine engine, TimeSpan limit)
    {
        var timer = Stopwatch.StartNew();
        var flusher = new Thread(() => { try { engine.Flush(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }) { Name = "Later.ExitFlush", IsBackground = true };
        flusher.Start();
        if (!flusher.Join(limit)) PerfLog.Log.ExitFlushTimedOut(timer.Elapsed.TotalMilliseconds, engine.Library.Data.Items.Count);
    }
}
