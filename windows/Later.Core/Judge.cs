namespace Later.Core;

/// Decides which open pages are ideas worth keeping. Fed a snapshot each time a browser's tabs change,
/// and told when the front app changes. Every input first judges the time that just passed, then applies
/// the new state. No timer: "spent time" is as precise as the browser's write interval, and nothing is shown.
/// Owned by one thread (the engine thread), like the `Library` it writes to.
public sealed class Judge
{
    public static TimeSpan LeftOpenAfter { get; internal set; } = TimeSpan.FromSeconds(60);

    /// A page is its page key, not its link: one page open as www and bare, or with noise in its query, is one page.
    readonly record struct Key(Browser Browser, string Page);

    /// `Verdict` is the page's verdict by the rules alone, before the user's corrections, which are applied on every read.
    sealed record OpenPage(Tab Tab, ParsedUrl Url, string PageKey, Verdict Verdict);

    /// A tab as the browser reports it. Its link and title are all its cleaned link and rule verdict depend on.
    readonly record struct TabKey(string Url, string Title);

    sealed class PageRecord
    {
        /// The cleaned link the page was first seen with; what is saved if the page is kept.
        public string Url = "";
        public string Title = "";
        /// Null while the page waits for its title to be judged; a pending page keeps its timing but is never saved.
        public Verdict? Verdict;
        public DateTime? OnScreenSince;
        public DateTime? OffScreenSince;
        public double ActiveSeconds;
        public bool Saved;
        /// It arrived as a return visit; applied once the page is judged specific.
        public bool CameBack;
        /// It sat a full minute off screen while pending; applied once the page is judged specific.
        public bool LeftOpenQualified;
    }

    readonly Library library;
    /// The browsers Later watches. A dismissal ends only once each has shown this judge its pages.
    readonly HashSet<Browser> watched;
    /// Watched browsers whose open pages are known: read at least once, or quit since.
    readonly HashSet<Browser> known = new();
    readonly Dictionary<Browser, Dictionary<Key, OpenPage>> open = new();
    readonly Dictionary<Key, PageRecord> records = new();
    /// Each browser's open tabs, cleaned and judged, from its latest read, so an unchanged tab isn't cleaned and judged
    /// again on every read. Null: a link that's never tracked.
    readonly Dictionary<Browser, Dictionary<TabKey, OpenPage?>> judged = new();
    bool savesFeedHomes;
    /// The machine is asleep or locked: no screen clock starts and no page counts as left open.
    bool away;
    /// How many tabs hold each page, by page key, across every watched browser. Not saved.
    readonly Dictionary<Browser, Dictionary<string, int>> copyCounts = new();

    public Browser? FrontBrowser { get; private set; }

    /// Whether home feeds (the YouTube home, X's Home) are kept too. Changing it judges every open tab again.
    public bool SavesFeedHomes
    {
        get => savesFeedHomes;
        set { if (savesFeedHomes != value) { savesFeedHomes = value; judged.Clear(); } }
    }

    public IReadOnlyDictionary<string, int> OpenCopies { get; private set; } = new Dictionary<string, int>();

    public Judge(Library library, ISet<Browser>? watched = null, bool savesFeedHomes = false)
    {
        this.library = library;
        this.watched = new HashSet<Browser>(watched ?? BrowserInfo.All.ToHashSet());
        this.savesFeedHomes = savesFeedHomes;
    }

    // MARK: Inputs

    public void Ingest(Snapshot snapshot, DateTime now)
    {
        library.Prune(now);
        // One threshold per input, so pages ending together are judged alike whatever their order.
        var threshold = library.DwellThreshold;
        Evaluate(now, threshold);
        var browser = snapshot.Browser;
        var current = new Dictionary<Key, OpenPage>();
        var copies = new Dictionary<string, int>();
        // Windows ranked by id, which grows as windows are made; each page's place is its leftmost tab in the oldest window.
        var rank = snapshot.Tabs.Select(t => t.WindowId).Distinct().Order().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var places = new Dictionary<string, (int Tab, int Window)>();
        var before = judged.GetValueOrDefault(browser) ?? new Dictionary<TabKey, OpenPage?>();
        var after = new Dictionary<TabKey, OpenPage?>();
        foreach (var tab in snapshot.Tabs)
        {
            var tabKey = new TabKey(tab.Url, tab.Title);
            // A cached null stays null: that link is never tracked.
            if (!before.TryGetValue(tabKey, out var cached) && !after.TryGetValue(tabKey, out cached))
            {
                var clean = LinkCleaner.Clean(tab.Url);
                cached = clean is null ? null
                    : new OpenPage(tab, clean, LinkCleaner.PageKey(clean), PageRules.Judge(clean, tab.Title, null, SavesFeedHomes));
            }
            after[tabKey] = cached;
            if (cached is null) continue;
            var page = cached with { Tab = tab };   // the same page, but maybe now on screen or in another window
            var key = new Key(browser, page.PageKey);
            copies[page.PageKey] = copies.GetValueOrDefault(page.PageKey) + 1;
            var place = (Tab: tab.TabIndex, Window: rank.GetValueOrDefault(tab.WindowId));
            if (!places.TryGetValue(page.PageKey, out var best) || (place.Window, place.Tab).CompareTo((best.Window, best.Tab)) < 0)
                places[page.PageKey] = place;
            // A page open in several tabs counts once; the on-screen copy wins.
            if (current.TryGetValue(key, out var existing) && existing.Tab.IsActive && existing.Tab.WindowIndex == 0) continue;
            current[key] = page;
        }
        judged[browser] = after;
        copyCounts[browser] = copies;
        UpdateCopies();
        var previous = open.GetValueOrDefault(browser);
        bool isFirstRead = previous is null;
        open[browser] = current;
        known.Add(browser);

        var dwellSamples = new List<double>();
        foreach (var key in records.Keys.Where(k => k.Browser == browser && !current.ContainsKey(k)).ToList())
            if (End(key, now, threshold) is { } seconds) dwellSamples.Add(seconds);
        foreach (var seconds in dwellSamples) library.AddDwellSample(seconds);

        foreach (var (key, page) in current)
        {
            var link = page.Url.Absolute;
            bool isNew = previous is null || !previous.ContainsKey(key);
            if (isNew && !isFirstRead)
            {
                library.RecordVisit(link, now);
                // Opened anew: a delete from an earlier visit doesn't carry over, unless another copy stayed open.
                if (IsClosedElsewhere(key)) library.Undismiss(link);
            }
            library.UpdateTitle(link, page.Tab.Title);
            // A reload briefly empties the title; keep the page's record as it was.
            if (page.Tab.Title.Length == 0 && records.ContainsKey(key)) continue;
            var verdict = PageRules.Apply(library.Data.Corrections, page.Verdict);
            bool cameBack = isNew && !library.Contains(link) && library.WasSeen(link, now);
            if (!verdict.IsSpecific)
            {
                // An empty title means it is still loading: time it from now, judge it when the title arrives.
                if (page.Tab.Title.Length == 0) records[key] = new PageRecord { Url = link, Saved = IsKept(link), CameBack = cameBack };
                else records.Remove(key);   // corrected to "not an idea", or the title showed a general page
                continue;
            }
            if (!records.TryGetValue(key, out var record)) record = new PageRecord { Url = link, Saved = IsKept(link), CameBack = cameBack };
            bool wasJudged = record.Verdict is not null;
            record.Title = page.Tab.Title;
            record.Verdict = verdict;
            records[key] = record;
            if (!wasJudged && record.CameBack) Save(key, now);
        }
        UpdateScreen(now);
        Evaluate(now, threshold);
        // After the saves above, so a page saved by this read gets its place at once.
        library.SetPlaces(current.Values
            .Where(page => RecordsPlace(page.PageKey, page.Url.Absolute, browser) && places.ContainsKey(page.PageKey))
            .Select(page => (page.Url.Absolute, places[page.PageKey].Tab, places[page.PageKey].Window))
            .ToList());
    }

    /// A page open in both browsers has one place on record, taken from the browser it was saved from while that still
    /// holds it, else from the first of the browsers that do, so each read doesn't overwrite the other's.
    bool RecordsPlace(string pageKey, string url, Browser browser)
    {
        var holders = open.Where(o => o.Key == browser || o.Value.ContainsKey(new Key(o.Key, pageKey))).Select(o => o.Key).ToList();
        if (holders.Count <= 1) return true;
        var saved = library.BrowserOf(url);
        var preferred = saved is { } s && holders.Contains(s) ? s : holders.OrderBy(b => Array.IndexOf(BrowserInfo.All, b)).First();
        return preferred == browser;
    }

    public void SetFrontBrowser(Browser? browser, DateTime now)
    {
        var threshold = library.DwellThreshold;
        Evaluate(now, threshold);
        FrontBrowser = browser;
        UpdateScreen(now);
        Evaluate(now, threshold);
    }

    /// The machine went to sleep or locked (or came back). Time up to now keeps its credit; the time away counts as neither
    /// reading nor leaving a page open, so a page in front when the lid closes isn't saved for the night it sat there.
    public void SetAway(bool value, DateTime now)
    {
        if (value == away) return;
        Evaluate(now, library.DwellThreshold);
        away = value;
        UpdateScreen(now);
        if (value) foreach (var record in records.Values) record.OffScreenSince = null;   // starts again at the wake
    }

    /// The first moment after `now` at which an unsaved page of this browser could be saved by time alone: a minute off screen,
    /// or long enough on screen. The judge has no timer of its own; the engine arms one read for this moment. Null when no
    /// page is waiting on the clock (a page that can't be saved, such as a deleted one, doesn't count).
    public DateTime? NextDeadline(Browser browser, DateTime now)
    {
        var threshold = library.DwellThreshold;
        DateTime? first = null;
        void Consider(DateTime at) { if (at > now && (first is null || at < first)) first = at; }
        foreach (var (key, record) in records)
        {
            if (key.Browser != browser || record.Saved || record.LeftOpenQualified) continue;
            if (record.OffScreenSince is { } off) Consider(off + LeftOpenAfter);
            if (record.Verdict is not null && record.OnScreenSince is { } on) Consider(on + TimeSpan.FromSeconds(Math.Max(0, threshold.TotalSeconds - record.ActiveSeconds)));
        }
        return first;
    }

    /// Whether an unsaved page of this browser waits on the clock at all, however late: what `NextDeadline` looks for, without the
    /// "after now" filter. A read that came back unreadable uses it to know the wait is not over.
    public bool WaitsOnTheClock(Browser browser) => records.Any(r =>
        r.Key.Browser == browser && !r.Value.Saved && !r.Value.LeftOpenQualified
        && (r.Value.OffScreenSince is not null || (r.Value.Verdict is not null && r.Value.OnScreenSince is not null)));

    /// The browser's last copy quit. Time before now keeps its credit; then its pages are forgotten without
    /// ending them, so a relaunch that restores the session is a first read: no visits, nothing "came back".
    public void BrowserQuit(Browser browser, DateTime now)
    {
        Evaluate(now, library.DwellThreshold);
        Forget(browser);
        known.Add(browser);   // a quit browser has nothing open
        if (FrontBrowser == browser) FrontBrowser = null;
        UpdateScreen(now);
    }

    /// A browser Later can't read (and won't without the user's yes) can't be hiding a copy of a page the user deleted: for
    /// dismissals it counts as known, with nothing open. Unlike a quit, it stays the front browser if it is.
    public void BrowserUnreadable(Browser browser)
    {
        Forget(browser);
        known.Add(browser);
    }

    void Forget(Browser browser)
    {
        foreach (var key in records.Keys.Where(k => k.Browser == browser).ToList()) records.Remove(key);
        open.Remove(browser);
        judged.Remove(browser);
        copyCounts.Remove(browser);
        UpdateCopies();
    }

    /// The browser launched: until its first read, it may be restoring pages this judge can't see.
    public void BrowserLaunched(Browser browser) => known.Remove(browser);

    /// Later began watching the browser after this judge was made: its data folder appeared on its first launch.
    public void Watch(Browser browser) => watched.Add(browser);

    // MARK: Internals

    void UpdateCopies()
    {
        var total = new Dictionary<string, int>();
        foreach (var counts in copyCounts.Values)
            foreach (var (page, n) in counts) total[page] = total.GetValueOrDefault(page) + n;
        OpenCopies = total;
    }

    /// Saved already, or deleted by the user while its tab stayed open: either way it's never saved again by itself.
    bool IsKept(string url) => library.Contains(url) || library.IsDismissed(url);

    /// No other browser has the page open, and none could be hiding a copy: every watched browser is known.
    /// Callers have already updated `key.Browser`'s own pages.
    bool IsClosedElsewhere(Key key) =>
        watched.IsSubsetOf(known) && !open.Any(o => o.Key != key.Browser && o.Value.ContainsKey(new Key(o.Key, key.Page)));

    /// Every save goes through here, so a page the user deleted is never saved again by itself.
    bool Keep(Key key, PageRecord record, Verdict verdict, DateTime now)
    {
        if (library.IsDismissed(record.Url)) return false;
        library.Save(record.Url, record.Title, verdict, key.Browser, now);
        return true;
    }

    /// The active tab of the front window of the front browser.
    Key? OnScreenKey
    {
        get
        {
            if (away || FrontBrowser is not { } browser || !open.TryGetValue(browser, out var pages)) return null;
            foreach (var (key, page) in pages)
                if (page.Tab.IsActive && page.Tab.WindowIndex == 0) return key;
            return null;
        }
    }

    void UpdateScreen(DateTime now)
    {
        var onScreen = OnScreenKey;
        foreach (var (key, record) in records.ToList())
        {
            if (key == onScreen)
            {
                if (record.OnScreenSince is null)
                {
                    record.OnScreenSince = now;
                    record.OffScreenSince = null;
                }
            }
            else if (record.OnScreenSince is { } since)
            {
                record.ActiveSeconds += (now - since).TotalSeconds;
                record.OnScreenSince = null;
                record.OffScreenSince = now;
            }
            else if (record.OffScreenSince is null && !away)   // no clock starts while away; the wake starts it
            {
                record.OffScreenSince = now;
            }
        }
    }

    void Evaluate(DateTime now, TimeSpan threshold)
    {
        foreach (var (key, record) in records.ToList())
        {
            if (record.Saved) continue;
            if (record.Verdict is null)
            {
                // Every input evaluates before it moves pages on screen, so a completed minute is never lost.
                if (LeftOpen(record, now)) record.LeftOpenQualified = true;
            }
            else if (ShouldSave(record, now, threshold))
            {
                Save(key, now);
            }
        }
    }

    static bool ShouldSave(PageRecord record, DateTime now, TimeSpan threshold)
    {
        if (record.Verdict is null) return false;
        var active = record.ActiveSeconds + (record.OnScreenSince is { } since ? (now - since).TotalSeconds : 0);
        return record.LeftOpenQualified || LeftOpen(record, now) || active >= threshold.TotalSeconds;
    }

    static bool LeftOpen(PageRecord record, DateTime now) =>
        record.OffScreenSince is { } since && now - since >= LeftOpenAfter;

    /// The page's verdict with corrections made since it was judged; null once it's "not an idea".
    Verdict? CurrentVerdict(Verdict? verdict)
    {
        if (verdict is null || !library.Data.Corrections.TryGetValue(verdict.PageType, out var correction)) return verdict;
        if (correction.IsSpecific == false) return null;
        return correction.Kind is { } kind ? verdict with { Kind = kind } : verdict;
    }

    void Save(Key key, DateTime now)
    {
        if (!records.TryGetValue(key, out var record) || record.Saved) return;
        var verdict = CurrentVerdict(record.Verdict);
        if (verdict is null)
        {
            records.Remove(key);
            return;
        }
        if (!Keep(key, record, verdict, now)) return;
        record.Saved = true;
    }

    /// The page closed, or its tab navigated away. Returns its on-screen time to learn from, if any.
    double? End(Key key, DateTime now, TimeSpan threshold)
    {
        if (!records.Remove(key, out var record)) return null;
        var verdict = CurrentVerdict(record.Verdict);
        if (verdict is not null && !record.Saved && ShouldSave(record, now, threshold)) record.Saved = Keep(key, record, verdict, now);
        if (IsClosedElsewhere(key)) library.Undismiss(record.Url);
        if (verdict is null) return null;
        if (record.OnScreenSince is { } since) record.ActiveSeconds += (now - since).TotalSeconds;
        library.MarkSeen(record.Url, now);
        if (record.Saved) library.Touch(record.Url, now);
        return record.ActiveSeconds > 0 ? record.ActiveSeconds : null;
    }
}
