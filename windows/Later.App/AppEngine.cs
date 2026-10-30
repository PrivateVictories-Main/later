using System.ComponentModel;
using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Platform;
using Microsoft.UI.Dispatching;

namespace Later.App;

/// The window app's side of watching. One thread of its own owns the engine (and the engine lock, which is thread-affine): it asks
/// the background agent for the list, starts the engine when the agent lets go, and hands the engine back at exit by releasing the
/// lock after a final save, which wakes the agent. The window sees only snapshots, at most one per 100 ms and none while hidden.
/// Without the lock (the agent is slow to answer) the list is shown read-only and asked for again whenever the window is shown or
/// activated. Before setup (Task 9) nothing is watched at all. Never starts an agent.
internal sealed class AppEngine
{
    static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(5), Coalesce = TimeSpan.FromMilliseconds(100);
    static readonly IReadOnlyDictionary<Browser, BrowserStatus> NoStatuses = new Dictionary<Browser, BrowserStatus>();
    const string BusyNotice = "Later is busy saving, so your list is read-only for now. It asks again each time you come back to this window.";
    const string NoLockNotice = "Later couldn't take charge of your list, so it is read-only for now.";

    readonly AppPaths paths;
    readonly LibraryViewModel model;
    readonly DispatcherQueue ui;
    readonly Action exitApp;
    readonly EngineThread thread = new();
    readonly ThreadHost host;
    readonly AgentStatusFile agentStatus;

    // Engine thread only:
    EngineLock? gate;
    EngineHost? running;
    bool holding, stopping, onboarded;
    LibraryData? peeked;
    string? readOnlyNotice, agentNotice;
    IDisposable? coalescer;
    (EngineHost? Host, int Revision, object? Copies) sent = (null, -1, null);

    // Both threads:
    volatile bool visible, needsLock, retryQueued;

    public AppEngine(AppPaths paths, LibraryViewModel model, DispatcherQueue ui, Action exitApp)
    {
        this.paths = paths; this.model = model; this.ui = ui; this.exitApp = exitApp;
        host = new ThreadHost(thread);
        agentStatus = new AgentStatusFile(paths.AgentStatus);
    }

    public void Start()
    {
        var runner = new Thread(() => thread.Run(init: Init, waitHandles: () => [], signaled: _ => { }, onExit: OnExit)) { Name = "Later.EngineRunner", IsBackground = true };
        runner.Start();
    }

    /// The window was shown or hidden: showing sends the list as it is now, hiding stops sending.
    public void SetVisible(bool value)
    {
        visible = value;
        if (value) thread.Post(Publish);
    }

    /// The window was shown or activated: a read-only list asks for the engine once more.
    public void RetryLock()
    {
        if (!needsLock || retryQueued) return;
        retryQueued = true;
        thread.Post(Take);
    }

    public void Resume() => thread.Post(() => running?.Engine.SetPaused(false));

    public void SetView(LibraryGrouping grouping, LibrarySort sort, bool reversed) => thread.Post(() => running?.Engine.SetView(grouping, sort, reversed));

    /// The window closed: save, let go of the lock (the agent takes over), and end the thread.
    public void Shutdown() => thread.Quit();

    void Init()
    {
        onboarded = SettingsStore.Load(paths.Settings).Onboarded;
        if (!onboarded) { ShowListOnly(null); return; }   // before setup nothing watches and the agent does not run either
        try { gate = EngineLock.Open(paths.InstanceSuffix); }
        catch (Win32Exception) { ShowListOnly(NoLockNotice); return; }
        Take();
    }

    /// Asks for the engine and starts it. Engine thread: the mutex belongs to the thread that took it.
    void Take()
    {
        retryQueued = false;
        if (running is not null || stopping || gate is null || !onboarded) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool took = gate.AcquireForApp(HandoffTimeout);
        PerfLog.Log.Handoff(timer.Elapsed.TotalMilliseconds, took);
        if (!took) { ShowListOnly(BusyNotice); return; }
        holding = true;
        needsLock = false;
        agentNotice = agentStatus.Take()?.Notice;   // what the agent could not do while the window was closed; cleared here, written again if it goes on
        var started = EngineHost.Start(paths, host, sessionEnded: () => { ui.TryEnqueue(() => exitApp()); thread.Quit(); });
        running = started;
        started.Engine.StateChanged += Changed;
        started.Engine.Library.Changed += Changed;
        Publish();
    }

    /// The list as it is on disk, with nobody writing it from here.
    void ShowListOnly(string? notice)
    {
        needsLock = onboarded && gate is not null;
        readOnlyNotice = notice;
        peeked = new LibraryStore(paths.Library).Peek(notice ?? "").Data;
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
        var token = (running, engine?.Library.Revision ?? 0, (object?)engine?.OpenCopies);
        if (token == sent) { ui.TryEnqueue(() => model.SetEngineState(state)); return; }
        sent = token;
        var data = engine is null ? peeked ?? new LibraryData() : engine.Library.CopyData();
        var copies = engine is null ? NoCopies : new Dictionary<string, int>(engine.OpenCopies);
        ui.TryEnqueue(() => { model.SetSnapshot(data, copies); model.SetEngineState(state); });
    }

    static readonly IReadOnlyDictionary<string, int> NoCopies = new Dictionary<string, int>();

    static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + " " + b;

    /// On the engine thread after the loop ended: the final save, then the lock, which the agent is waiting for.
    void OnExit()
    {
        stopping = true;
        coalescer?.Dispose();
        running?.Stop(flush: true);
        if (holding) gate!.Release();
        gate?.Dispose();
    }
}
