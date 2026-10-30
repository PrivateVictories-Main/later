using System.Globalization;
using Later.Core;
using Later.Windows.Platform;
using Later.Windows.Reader;

namespace Later.Windows.Engine;

/// The engine with everything it listens to, started on the engine thread: the event hooks, the debounce, the session window.
public sealed class EngineHost : IDisposable
{
    static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    readonly RealBrowserIO io;
    readonly IEngineHost threadHost;
    readonly WinEventWatcher watcher;
    readonly SessionNotifier notifier;
    readonly Dictionary<Browser, Debouncer> debouncers = [];
    bool stopped;

    public Engine Engine { get; }

    /// What the files say at start. A list that can't be read, or can't be set aside, comes back without a store: it is
    /// never written over, for the whole session, and the notice says why.
    internal sealed record Opened(Library Library, LibraryStore? Store, string? Notice, Settings Settings);

    internal static Opened Open(AppPaths paths)
    {
        var settings = SettingsStore.Load(paths.Settings);
        LibraryStore store; LibraryStore.Loaded loaded;
        try
        {
            Directory.CreateDirectory(paths.DataDir);
            store = new LibraryStore(paths.Library);
            loaded = store.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Opened(new Library(), null, "Later can't open its storage folder, so saves won't be kept.", settings);
        }
        var library = new Library(loaded.Data);
        // Old pages take the current rules; saved by the next change.
        var merged = library.MergeDuplicates();
        var refreshed = library.RefreshKinds();
        PerfLog.Log.Opened(merged, refreshed.Items, refreshed.Changed, refreshed.ToTools);
        int backToList = library.RefreshPlaces();
        PerfLog.Log.PlacesRefreshed(backToList, library.Data.Items.Count(i => i.IsActiveRegular), library.Data.Items.Count(i => i.Status == ItemStatus.Done), library.Data.Items.Count);
        return new Opened(library, loaded.Writable ? store : null, loaded.Notice, settings);
    }

    /// `sessionEnded` runs after the engine was stopped and flushed because Windows is ending the session.
    public static EngineHost Start(AppPaths paths, IEngineHost threadHost, Action? sessionEnded = null)
    {
        var opened = Open(paths);
        Func<Browser, string>? folder = null;
#if DEBUG
        // Tests point the reader at a throwaway browser profile and shorten the minute a page must stay open.
        if (Environment.GetEnvironmentVariable("LATER_TEST_PROFILE") is { Length: > 0 } profile) folder = _ => profile;
        if (double.TryParse(Environment.GetEnvironmentVariable("LATER_TEST_LEFT_OPEN_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            Judge.LeftOpenAfter = TimeSpan.FromSeconds(seconds);
#endif
#if DEBUG
        if (Environment.GetEnvironmentVariable("LATER_TEST_TRACE") is { Length: > 0 } trace) TraceListener.Start(trace);
#endif
        var io = new RealBrowserIO(folder);
        var judge = new Judge(opened.Library, new HashSet<Browser>(), opened.Settings.SavesFeedHomes);   // replaced in Engine.Start, once the installed browsers are known
        var engine = new Engine(opened.Library, judge, io, threadHost, opened.Store, opened.Settings, loadNotice: opened.Notice) { DaysCache = VisitDaysCache.Shared };
        engine.SettingsChanged += s =>
        {
            try { SettingsStore.Save(paths.Settings, s); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        };
        var host = new EngineHost(engine, io, threadHost, sessionEnded);
        engine.Start();
        return host;
    }

    EngineHost(Engine engine, RealBrowserIO io, IEngineHost threadHost, Action? sessionEnded)
    {
        Engine = engine; this.io = io; this.threadHost = threadHost;
        foreach (var b in BrowserInfo.All)
        {
            var browser = b;
            debouncers[b] = new Debouncer(DebounceDelay, () => threadHost.Post(() => { PerfLog.Log.Wake("debounce"); engine.Ring(browser); }));
        }
        engine.Wake = b => debouncers[b].Poke();
        engine.ReadApplied += _ => watcher!.SetKnownBrowserWindows([.. BrowserInfo.All.SelectMany(b => io.KnownWindows(b))]);

        watcher = new WinEventWatcher((kind, hwnd) =>
        {
            engine.OnEvent(kind, hwnd);
            if (kind == WatchEventKind.WindowDestroyed) threadHost.Post(Retarget);   // a browser process may have ended
        });
        watcher.Start();
        Retarget();

        notifier = new SessionNotifier();
        ConnectSession(notifier, engine, Retarget, () => { Stop(flush: true); sessionEnded?.Invoke(); });
    }

    /// What the session window's events do. Going away (lock, sleep, display off): time from now is neither reading nor leaving a
    /// page open, and the browsers are read so what is open now is known. Coming back: the set of browser processes may be
    /// different after a sleep, a lock or the display coming back, so they are hooked again, and what changed meanwhile is read.
    internal static void ConnectSession(SessionNotifier notifier, Engine engine, Action retarget, Action sessionEnding)
    {
        void GoAway(string reason) { engine.SetAway(reason, true); engine.RingAll(); }
        void Back(string reason) { engine.SetAway(reason, false); retarget(); engine.RingAll(); }
        notifier.Locked += () => GoAway("lock");
        notifier.Unlocked += () => Back("lock");
        notifier.Suspending += () => GoAway("sleep");
        notifier.Resumed += () => Back("sleep");
        notifier.DisplayChanged += on => { if (on) Back("screen"); else GoAway("screen"); };
        notifier.SessionEnding += sessionEnding;
    }

    /// Hooks exactly the processes of the browser windows that exist now.
    void Retarget()
    {
        if (stopped) return;
        watcher.Retarget(BrowserDiscovery.Enumerate().Select(w => w.Pid).Distinct().ToList());
    }

#if DEBUG
    /// Tests only: the engine's own counts-and-timings events (never a link or a title), one line each, in a file the test reads.
    sealed class TraceListener : System.Diagnostics.Tracing.EventListener
    {
        readonly string path;
        readonly object gate = new();
        TraceListener(string path) => this.path = path;
        static TraceListener? instance;
        public static void Start(string path) => instance ??= new TraceListener(path);

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Later-Engine") EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            var line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + e.EventName + " " + (e.Payload is { } payload ? string.Join(" ", payload) : "");
            lock (gate) { try { File.AppendAllText(path, line + Environment.NewLine); } catch (IOException) { } }
        }
    }
#endif

    /// Stops watching and reading; with `flush` the list is written first. Engine thread.
    public void Stop(bool flush)
    {
        if (stopped) return;
        stopped = true;
        foreach (var d in debouncers.Values) d.Dispose();
        watcher.Dispose();
        notifier.Dispose();
        Engine.Stop(flush);
    }

    public void Dispose()
    {
        Stop(flush: false);
        io.Dispose();
    }
}
