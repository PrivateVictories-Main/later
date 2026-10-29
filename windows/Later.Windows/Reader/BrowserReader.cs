using Later.Core;

namespace Later.Windows.Reader;

/// `IsPrivate`: a private or guest window; it was not read. `IsUnrecognized`: not positively a normal window and not a private
/// one either (a profile name not known yet, a chrome still being built); it was not read, and a result that left it out is not complete.
public sealed record RawWindow(nint Hwnd, bool IsPrivate, IReadOnlyList<RawTab> Tabs, string? ActiveUrl, bool IsUnrecognized = false);
public interface IUiaSource { RawWindow? ReadWindow(BrowserWindow window); }            // null: the window could not be read
public interface IHistorySource { Dictionary<string, string> LinksForTitles(Browser b, IReadOnlyCollection<string> titles, DateTime since); }
public interface ISessionSource { IReadOnlyList<SessionTab> Read(Browser b); }

public enum ReadStatus { Ok, NotRunning, Transient, Failed }
/// `UnrecognizedWindows`: windows that were not read and might hold tabs (see `RawWindow`), or, when no window gave a tab strip,
/// the windows without one. Above 0, the snapshot may be missing tabs that are open and must not be read as "they closed".
public sealed record ReadResult(ReadStatus Status, Snapshot? Snapshot, int WindowsRead, int PrivateWindowsSkipped, int UnresolvedTabs, int UnrecognizedWindows = 0);

/// Reads a browser's open tabs: one UI Automation pass per window, then links for the tabs that pass could not give one.
/// Never opens, closes or selects anything. One per process; MTA thread only.
public sealed class BrowserReader : IDisposable
{
    readonly IUiaSource uia; readonly IHistorySource history; readonly ISessionSource session;
    readonly Func<IReadOnlyList<BrowserWindow>> windowsOf; readonly Func<DateTime> clock;
    readonly Dictionary<Browser, TabLinkResolver> resolvers = new();
    readonly Dictionary<Browser, IReadOnlyList<SessionTab>> sessions = new();   // read once per browser run
    readonly System.Collections.Concurrent.ConcurrentDictionary<Browser, HashSet<nint>> known = new();   // sets are replaced whole, so the engine thread can read them during a read
    readonly Dictionary<nint, long> windowIds = new();                          // first-seen counter
    long nextWindowId = 1;
    static readonly ReadResult Transient = new(ReadStatus.Transient, null, 0, 0, 0);

    public BrowserReader() : this(new UiaSource(), new HistorySource(), new SessionSource(), BrowserDiscovery.Enumerate, () => DateTime.UtcNow) { }

    internal BrowserReader(IUiaSource uia, IHistorySource history, ISessionSource session, Func<IReadOnlyList<BrowserWindow>> windows, Func<DateTime> clock)
    { this.uia = uia; this.history = history; this.session = session; windowsOf = windows; this.clock = clock; }

    public ReadResult Read(Browser browser)
    {
        var windows = windowsOf().Where(w => w.Browser == browser).ToList();          // z-order, top first
        if (windows.Count == 0) { BrowserQuit(browser); return new ReadResult(ReadStatus.NotRunning, null, 0, 0, 0); }
        // Windows never seen get the next number; the first read numbers bottom-of-z-order first. A closed window's number is
        // dropped, so a reused handle starts a new one.
        var current = windows.Select(w => w.Hwnd).ToHashSet();
        if (known.TryGetValue(browser, out var before)) foreach (var gone in before.Where(h => !current.Contains(h))) windowIds.Remove(gone);
        foreach (var w in Enumerable.Reverse(windows)) if (!windowIds.ContainsKey(w.Hwnd)) windowIds[w.Hwnd] = nextWindowId++;
        known[browser] = current;
        if (!resolvers.TryGetValue(browser, out var resolver)) resolvers[browser] = resolver = new TabLinkResolver();

        var raws = new List<(BrowserWindow Window, int Z, RawWindow Raw)>();
        int privateSkipped = 0, unrecognized = 0, noStrip = 0;
        for (int z = 0; z < windows.Count; z++)
        {
            var raw = uia.ReadWindow(windows[z]);
            if (raw is null) return Transient;                                         // never "the tabs closed"
            if (raw.IsUnrecognized) { unrecognized++; continue; }
            if (raw.IsPrivate) { privateSkipped++; continue; }                         // its tabs go no further than this loop
            if (raw.Tabs.Count == 0) { if (windows[z].IsMinimized) return Transient; noStrip++; continue; }   // no tab strip: an app window
            raws.Add((windows[z], z, raw));
        }
        // Windows without a tab strip are app windows while another window has tabs; when none has, they may be normal windows
        // whose strip is not exposed yet, and "no tabs" would be a guess.
        if (raws.Count == 0) return new ReadResult(ReadStatus.Ok, new Snapshot(browser, []), 0, privateSkipped, 0, unrecognized + noStrip);

        var allTabs = raws.SelectMany(x => x.Raw.Tabs).ToList();
        var ambiguous = TabLinkResolver.AmbiguousTitles(allTabs);
        resolver.Prune(allTabs.Select(t => t.RuntimeId).ToHashSet(StringComparer.Ordinal));
        foreach (var (_, _, raw) in raws)
            foreach (var t in raw.Tabs.Where(t => t.IsSelected))
                if (!string.IsNullOrEmpty(raw.ActiveUrl)) resolver.Remember(t, raw.ActiveUrl);

        var links = allTabs.ToDictionary(t => t.RuntimeId, t => resolver.Recall(t, ambiguous), StringComparer.Ordinal);
        var unresolved = allTabs.Where(t => links[t.RuntimeId] is null).ToList();
        if (unresolved.Count > 0)
        {
            if (!sessions.TryGetValue(browser, out var previous)) sessions[browser] = previous = session.Read(browser);
            foreach (var t in unresolved)
                if (TabLinkResolver.FromSession(t.Title, previous, ambiguous) is { } url) { links[t.RuntimeId] = url; resolver.Remember(t, url, exact: false); }
            unresolved = unresolved.Where(t => links[t.RuntimeId] is null).ToList();
        }
        if (unresolved.Count > 0)
        {
            var byTitle = history.LinksForTitles(browser, unresolved.Select(t => t.Title).ToHashSet(StringComparer.Ordinal), clock().AddDays(-30));
            foreach (var t in unresolved)
                if (TabLinkResolver.FromHistory(t.Title, byTitle, ambiguous) is { } url) { links[t.RuntimeId] = url; resolver.Remember(t, url, exact: false); }
            unresolved = unresolved.Where(t => links[t.RuntimeId] is null).ToList();
        }
        var tabs = new List<Tab>();
        foreach (var (w, z, raw) in raws)
            foreach (var t in raw.Tabs)
                if (links[t.RuntimeId] is { } link) tabs.Add(new Tab(link, t.Title, t.IsSelected, z, t.Index, windowIds[w.Hwnd]));   // tabs without a link are not judged yet
        return new ReadResult(ReadStatus.Ok, new Snapshot(browser, tabs), raws.Count, privateSkipped, unresolved.Count, unrecognized);
    }

    /// Forget remembered links and the cached previous session.
    public void BrowserQuit(Browser b)
    {
        resolvers.Remove(b); sessions.Remove(b);
        if (known.TryRemove(b, out var windows)) foreach (var h in windows) windowIds.Remove(h);
    }

    public IReadOnlyCollection<nint> KnownWindows(Browser b) => known.TryGetValue(b, out var k) ? k : [];

    public void Dispose() => (uia as IDisposable)?.Dispose();
}

/// The UI Automation session is made at the first window to read, so no browser open means no UIAutomationCore in the process.
sealed class UiaSource(Func<Browser, string>? dataFolder = null) : IUiaSource, IDisposable
{
    UiaReader? reader;
    public RawWindow? ReadWindow(BrowserWindow w) => (reader ??= new UiaReader(dataFolder)).TryRead(w);      // UiaReader applies PrivateWindowDetector
    public void Dispose() => reader?.Dispose();
}

/// `dataFolder` is the browser's real folder unless a test points the reader at a throwaway profile.
sealed class HistorySource(Func<Browser, string>? dataFolder = null) : IHistorySource
{
    public Dictionary<string, string> LinksForTitles(Browser b, IReadOnlyCollection<string> titles, DateTime since) =>
        HistoryLookupStore.LinksForTitles(titles, HistoryLookupStore.Databases((dataFolder ?? BrowserDiscovery.DataFolder)(b)), since);
}

sealed class SessionSource(Func<Browser, string>? dataFolder = null) : ISessionSource
{
    public IReadOnlyList<SessionTab> Read(Browser b)
    {
        var folder = (dataFolder ?? BrowserDiscovery.DataFolder)(b);
        var tabs = PreviousSession.Read(folder);
        if (!tabs.Any(t => t.Title.Length == 0)) return tabs;
        // Session files carry the address, not the title: History knows the title of every address it has seen.
        var titles = HistoryLookupStore.TitlesForLinks(tabs.Where(t => t.Title.Length == 0).Select(t => t.Url), HistoryLookupStore.Databases(folder));
        return tabs.Select(t => t.Title.Length == 0 && titles.TryGetValue(t.Url, out var title) ? t with { Title = title } : t).ToList();
    }
}
