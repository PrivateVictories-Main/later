using Later.Core;

namespace Later.Windows.Reader;

public sealed record RawWindow(nint Hwnd, bool IsPrivate, IReadOnlyList<RawTab> Tabs, string? ActiveUrl);
public interface IUiaSource { RawWindow? ReadWindow(BrowserWindow window); }            // null: the window could not be read
public interface IHistorySource { Dictionary<string, string> LinksForTitles(Browser b, IReadOnlyCollection<string> titles, DateTime since); }
public interface ISessionSource { IReadOnlyList<SessionTab> Read(Browser b); }

public enum ReadStatus { Ok, NotRunning, Transient, Failed }
public sealed record ReadResult(ReadStatus Status, Snapshot? Snapshot, int WindowsRead, int PrivateWindowsSkipped, int UnresolvedTabs);

/// Reads a browser's open tabs: one UI Automation pass per window, then links for the tabs that pass could not give one.
/// Never opens, closes or selects anything. One per process; MTA thread only.
public sealed class BrowserReader : IDisposable
{
    readonly IUiaSource uia; readonly IHistorySource history; readonly ISessionSource session;
    readonly Func<IReadOnlyList<BrowserWindow>> windowsOf; readonly Func<DateTime> clock;
    readonly Dictionary<Browser, TabLinkResolver> resolvers = new();
    readonly Dictionary<Browser, IReadOnlyList<SessionTab>> sessions = new();   // read once per browser run
    readonly Dictionary<Browser, HashSet<nint>> known = new();
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
        // Windows never seen get the next number; the first read numbers bottom-of-z-order first.
        foreach (var w in Enumerable.Reverse(windows)) if (!windowIds.ContainsKey(w.Hwnd)) windowIds[w.Hwnd] = nextWindowId++;
        known[browser] = windows.Select(w => w.Hwnd).ToHashSet();
        if (!resolvers.TryGetValue(browser, out var resolver)) resolvers[browser] = resolver = new TabLinkResolver();

        var raws = new List<(BrowserWindow Window, int Z, RawWindow Raw)>();
        int privateSkipped = 0;
        for (int z = 0; z < windows.Count; z++)
        {
            var raw = uia.ReadWindow(windows[z]);
            if (raw is null) return Transient;                                         // never "the tabs closed"
            if (raw.IsPrivate) { privateSkipped++; continue; }                         // its tabs go no further than this loop
            if (raw.Tabs.Count == 0) { if (windows[z].IsMinimized) return Transient; continue; }   // no tab strip: an app window
            raws.Add((windows[z], z, raw));
        }
        if (raws.Count == 0) return new ReadResult(ReadStatus.Ok, new Snapshot(browser, []), 0, privateSkipped, 0);

        var allTabs = raws.SelectMany(x => x.Raw.Tabs).ToList();
        var ambiguous = TabLinkResolver.AmbiguousTitles(allTabs);
        resolver.Prune(allTabs.Select(t => t.RuntimeId).ToHashSet(StringComparer.Ordinal));
        foreach (var (_, _, raw) in raws)
            foreach (var t in raw.Tabs.Where(t => t.IsSelected))
                if (!string.IsNullOrEmpty(raw.ActiveUrl)) resolver.Remember(t, raw.ActiveUrl);

        var links = allTabs.ToDictionary(t => t.RuntimeId, resolver.Recall, StringComparer.Ordinal);
        var unresolved = allTabs.Where(t => links[t.RuntimeId] is null).ToList();
        if (unresolved.Count > 0)
        {
            if (!sessions.TryGetValue(browser, out var previous)) sessions[browser] = previous = session.Read(browser);
            foreach (var t in unresolved)
                if (TabLinkResolver.FromSession(t.Title, previous, ambiguous) is { } url) { links[t.RuntimeId] = url; resolver.Remember(t, url); }
            unresolved = unresolved.Where(t => links[t.RuntimeId] is null).ToList();
        }
        if (unresolved.Count > 0)
        {
            var byTitle = history.LinksForTitles(browser, unresolved.Select(t => t.Title).ToHashSet(StringComparer.Ordinal), clock().AddDays(-30));
            foreach (var t in unresolved)
                if (TabLinkResolver.FromHistory(t.Title, byTitle, ambiguous) is { } url) { links[t.RuntimeId] = url; resolver.Remember(t, url); }
            unresolved = unresolved.Where(t => links[t.RuntimeId] is null).ToList();
        }
        var tabs = new List<Tab>();
        foreach (var (w, z, raw) in raws)
            foreach (var t in raw.Tabs)
                if (links[t.RuntimeId] is { } link) tabs.Add(new Tab(link, t.Title, t.IsSelected, z, t.Index, windowIds[w.Hwnd]));   // tabs without a link are not judged yet
        return new ReadResult(ReadStatus.Ok, new Snapshot(browser, tabs), raws.Count, privateSkipped, unresolved.Count);
    }

    /// Forget remembered links and the cached previous session.
    public void BrowserQuit(Browser b) { resolvers.Remove(b); sessions.Remove(b); known.Remove(b); }

    public IReadOnlyCollection<nint> KnownWindows(Browser b) => known.TryGetValue(b, out var k) ? k : [];

    public void Dispose() => (uia as IDisposable)?.Dispose();
}

sealed class UiaSource : IUiaSource, IDisposable
{
    readonly UiaReader reader = new();
    public RawWindow? ReadWindow(BrowserWindow w) => reader.TryRead(w);      // UiaReader applies PrivateWindowDetector
    public void Dispose() => reader.Dispose();
}

/// `dataFolder` is the browser's real folder unless a test points the reader at a throwaway profile.
sealed class HistorySource(Func<Browser, string>? dataFolder = null) : IHistorySource
{
    public Dictionary<string, string> LinksForTitles(Browser b, IReadOnlyCollection<string> titles, DateTime since) =>
        HistoryLookupStore.LinksForTitles(titles, HistoryLookupStore.Databases((dataFolder ?? BrowserDiscovery.DataFolder)(b)), since);
}

sealed class SessionSource(Func<Browser, string>? dataFolder = null) : ISessionSource
{
    public IReadOnlyList<SessionTab> Read(Browser b) => PreviousSession.Read((dataFolder ?? BrowserDiscovery.DataFolder)(b));
}
