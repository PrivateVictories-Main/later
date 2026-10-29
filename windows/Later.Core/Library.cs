using System.Security.Cryptography;
using System.Text;

namespace Later.Core;

/// The saved pages and the user's learning, owned by one thread (the engine thread). Every change bumps `Revision` and
/// raises `Changed`, so the app can schedule a save and a view can tell in one comparison whether it is out of date.
public sealed class Library
{
    readonly LibraryData data;
    /// Saved pages by page key (see `LinkCleaner.PageKey`), so one page is one item however its link was written.
    readonly Dictionary<string, int> index = new();
    readonly Dictionary<string, string> keys = new();
    DateTime? lastPrune;
    /// Links of pages saved since the history was last asked, so each is asked about once. Not saved.
    readonly List<string> awaitingHistory = new();

    public static readonly TimeSpan CameBackWindow = TimeSpan.FromDays(30);
    const int MaxDwellSamples = 200;

    public Library(LibraryData? data = null)
    {
        this.data = data ?? new LibraryData();
        Reindex();
    }

    /// The live data, for the owner thread only.
    public LibraryData Data => data;

    /// A copy for saving and for views that run on another thread.
    public LibraryData CopyData() => data.Copy();

    /// Bumped on every change.
    public int Revision { get; private set; }

    /// Raised after every change.
    public event Action? Changed;

    // MARK: Lookup

    public bool Contains(string url) => index.ContainsKey(PageKey(url));

    /// The link's page key, remembered: the judge asks about every open page on every read.
    public string PageKey(string url)
    {
        if (keys.TryGetValue(url, out var known)) return known;
        if (keys.Count > 4_096) keys.Clear();
        var key = ParsedUrl.Parse(url) is { } parsed ? LinkCleaner.PageKey(parsed) : url;
        keys[url] = key;
        return key;
    }

    // MARK: Saving

    /// Saves a page the judge decided to keep. Does nothing if it's already here, active or done.
    /// A page without a title yet is saved under its host; `UpdateTitle` replaces that when the real title arrives.
    public bool Save(string url, string title, Verdict verdict, Browser browser, DateTime now)
    {
        var key = PageKey(url);
        if (index.ContainsKey(key)) return false;
        var host = HostOf(url);
        var blank = string.IsNullOrWhiteSpace(title);
        data.Items.Add(new Item(Guid.NewGuid(), url, blank ? host : title, host, verdict.Kind, verdict.PageType, browser, now, now,
                                1, ItemStatus.Active));
        awaitingHistory.Add(url);   // every browser: unlike the Mac, Windows can read Chrome's History too
        index[key] = data.Items.Count - 1;
        NotifyChanged();
        return true;
    }

    // MARK: Opened time and place

    /// The links of pages saved since the last call, for the history to be asked about.
    public IReadOnlyList<string> TakeAwaitingHistory()
    {
        var taken = awaitingHistory.ToList();
        awaitingHistory.Clear();
        return taken;
    }

    /// Links of pages whose opening time isn't known yet, for the one-time backfill when the list loads.
    public IReadOnlyList<string> LinksMissingHistory() =>
        data.Items.Where(i => i.OpenedAt is null && i.HistoryChecked != i.Url).Select(i => i.Url).ToList();

    /// Remembers that the history was asked about these links and had nothing to say about some, so the next launch's
    /// backfill doesn't ask again. Saved: it is the only way the answer outlives this launch.
    public void MarkHistoryChecked(IEnumerable<string> links)
    {
        bool any = false;
        foreach (var link in links)
        {
            if (!index.TryGetValue(PageKey(link), out var i)) continue;
            var item = data.Items[i];
            if (item.OpenedAt is not null || item.HistoryChecked == item.Url) continue;
            data.Items[i] = item with { HistoryChecked = item.Url };
            any = true;
        }
        if (any) NotifyChanged();
    }

    /// The browser a saved page was first saved from.
    public Browser? BrowserOf(string url) => index.TryGetValue(PageKey(url), out var i) ? data.Items[i].Browser : null;

    /// Records what the browser's history says about pages, by link. A page's opening time is only ever filled in, never
    /// replaced; its visit count follows the history. Returns how many pages changed.
    public int ApplyHistory(IReadOnlyDictionary<string, HistoryResult> results)
    {
        int changes = 0;
        foreach (var (url, result) in results)
        {
            if (!index.TryGetValue(PageKey(url), out var i)) continue;
            var item = data.Items[i];
            var openedAt = item.OpenedAt ?? result.FirstVisit;
            if (openedAt == item.OpenedAt && result.VisitCount == item.HistoryVisits) continue;
            data.Items[i] = item with { OpenedAt = openedAt, HistoryVisits = result.VisitCount };
            changes++;
        }
        if (changes > 0) NotifyChanged();
        return changes;
    }

    /// Where open pages' tabs are now, by link: the place in the tab strip and the window's rank. A change is only
    /// announced when a place really moved.
    public void SetPlaces(IEnumerable<(string Url, int Tab, int Window)> places)
    {
        bool moved = false;
        foreach (var place in places)
        {
            if (!index.TryGetValue(PageKey(place.Url), out var i)) continue;
            var item = data.Items[i];
            if (item.TabIndex == place.Tab && item.WindowRank == place.Window) continue;
            data.Items[i] = item with { TabIndex = place.Tab, WindowRank = place.Window };
            moved = true;
        }
        if (moved) NotifyChanged();
    }

    /// Puts every saved page's kind back to what the current rules say, except pages whose type the user moved themselves.
    /// Runs once when the list loads, before anything is shown or saved. Only kinds change: nothing is removed or
    /// un-saved, and no title, date or link is touched. Returns counts only, for the log.
    public (int Items, int Changed, int ToTools) RefreshKinds()
    {
        int changed = 0, toTools = 0;
        for (int i = 0; i < data.Items.Count; i++)
        {
            var item = data.Items[i];
            if (data.Corrections.TryGetValue(item.PageType, out var correction) && correction.Kind is not null) continue;
            if (ParsedUrl.Parse(item.Url) is not { Host: not null } url) continue;
            var kind = PageRules.Judge(url, item.Title, data.Corrections).Kind;
            if (kind == item.Kind) continue;
            data.Items[i] = item with { Kind = kind };
            changed++;
            if (kind == Kind.Tools) toTools++;
        }
        return (data.Items.Count, changed, toTools);
    }

    /// Folds saved pages that are really one page (the same link written two ways) into one item: the earliest save, all
    /// the visits, the latest sighting and title, done if a copy was marked done after the others were last seen. The
    /// user's kind for a page type is kept. Runs once when the list loads. Returns how many items went away.
    public int MergeDuplicates()
    {
        var groups = new Dictionary<string, List<Item>>();
        var order = new List<string>();
        foreach (var item in data.Items)
        {
            var key = PageKey(item.Url);
            if (!groups.TryGetValue(key, out var list)) { groups[key] = list = new List<Item>(); order.Add(key); }
            list.Add(item);
        }
        if (!groups.Values.Any(g => g.Count > 1)) return 0;
        data.Items = order.Select(key => Merge(groups[key])).ToList();
        int removed = order.Sum(k => groups[k].Count - 1);
        NotifyChanged(reindexing: true);
        return removed;
    }

    Item Merge(List<Item> copies)
    {
        if (copies.Count < 2) return copies[0];
        var merged = copies.MinBy(c => c.FirstSaved)!;
        var latest = copies.MaxBy(c => c.LastSeen)!;
        var moved = copies.FirstOrDefault(c => data.Corrections.TryGetValue(c.PageType, out var fix) && fix.Kind is not null) ?? latest;
        // Done if a copy was marked done after every active copy was last seen; otherwise the user has been back to it.
        DateTime? doneAt = copies.Where(c => c.Status == ItemStatus.Done).Select(c => (DateTime?)(c.DoneAt ?? c.LastSeen)).Max();
        DateTime? activeSeen = copies.Where(c => c.Status == ItemStatus.Active).Select(c => (DateTime?)c.LastSeen).Max();
        bool isDone = doneAt is { } done && (activeSeen is not { } seen || done > seen);
        return merged with
        {
            Visits = copies.Sum(c => c.Visits),
            OpenedAt = copies.Where(c => c.OpenedAt is not null).Select(c => c.OpenedAt).Min(),
            HistoryVisits = copies.Where(c => c.HistoryVisits is not null).Select(c => c.HistoryVisits).Max(),
            TabIndex = latest.TabIndex,
            WindowRank = latest.WindowRank,
            LastSeen = latest.LastSeen,
            Title = latest.Title,
            PageType = moved.PageType,
            Kind = moved.Kind,
            Status = isDone ? ItemStatus.Done : ItemStatus.Active,
            DoneAt = isDone ? doneAt : null,
        };
    }

    public void RecordVisit(string url, DateTime now)
    {
        if (!index.TryGetValue(PageKey(url), out var i) || data.Items[i].Status != ItemStatus.Active) return;
        data.Items[i] = data.Items[i] with { Visits = data.Items[i].Visits + 1, LastSeen = now };
        NotifyChanged();
    }

    public void Touch(string url, DateTime now)
    {
        if (!index.TryGetValue(PageKey(url), out var i)) return;
        data.Items[i] = data.Items[i] with { LastSeen = now };
        NotifyChanged();
    }

    public void UpdateTitle(string url, string title)
    {
        if (!index.TryGetValue(PageKey(url), out var i) || title.Length == 0 || data.Items[i].Title == title) return;
        data.Items[i] = data.Items[i] with { Title = title };
        NotifyChanged();
    }

    // MARK: The user's actions

    public void SetDone(Guid id, bool done, DateTime now)
    {
        int i = data.Items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        data.Items[i] = data.Items[i] with { Status = done ? ItemStatus.Done : ItemStatus.Active, DoneAt = done ? now : null };
        NotifyChanged();
    }

    /// Deleted pages are remembered as dismissed, so a tab still open isn't saved again after Later restarts.
    public void Delete(Guid id, DateTime now)
    {
        int i = data.Items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        var removed = data.Items[i];
        data.Items.RemoveAt(i);
        data.Dismissed[Hash(PageKey(removed.Url))] = now;
        NotifyChanged(reindexing: true);
    }

    /// Puts a deleted item back at its old position, for Undo. If its page was saved again since,
    /// the original replaces the newer entry, so there's still one entry per page.
    public void Restore(Item item, int position) => Restore([(item, position)]);

    /// Puts several deleted items back, in ascending order of their old positions, as one change.
    public void Restore(IReadOnlyList<(Item Item, int Position)> entries)
    {
        foreach (var (item, position) in entries)
        {
            var key = PageKey(item.Url);
            int existing = data.Items.FindIndex(x => PageKey(x.Url) == key);
            if (existing >= 0) data.Items.RemoveAt(existing);
            Forget(item.Url);
            data.Items.Insert(Math.Min(Math.Max(position, 0), data.Items.Count), item);
        }
        NotifyChanged(reindexing: true);
    }

    public void EmptyDone(DateTime now)
    {
        foreach (var item in data.Items.Where(i => i.Status == ItemStatus.Done)) data.Dismissed[Hash(PageKey(item.Url))] = now;
        data.Items.RemoveAll(i => i.Status == ItemStatus.Done);
        NotifyChanged(reindexing: true);
    }

    /// Sets the kind for this item's page type, and for every saved item of that type.
    public void Move(Guid id, Kind to)
    {
        var item = data.Items.FirstOrDefault(x => x.Id == id);
        if (item is null) return;
        data.Corrections[item.PageType] = (data.Corrections.GetValueOrDefault(item.PageType) ?? new Correction()) with { Kind = to };
        for (int i = 0; i < data.Items.Count; i++)
            if (data.Items[i].PageType == item.PageType) data.Items[i] = data.Items[i] with { Kind = to };
        NotifyChanged();
    }

    /// Puts pages' kinds back as they were before a move, for Undo.
    public void SetKinds(IReadOnlyDictionary<Guid, Kind> kinds)
    {
        for (int i = 0; i < data.Items.Count; i++)
            if (kinds.TryGetValue(data.Items[i].Id, out var kind)) data.Items[i] = data.Items[i] with { Kind = kind };
        NotifyChanged();
    }

    public void NotAnIdea(Guid id)
    {
        var item = data.Items.FirstOrDefault(x => x.Id == id);
        if (item is null) return;
        NotAnIdea(item.PageType, item.Url);
    }

    /// Marks the page type general and removes that one page from the list.
    public void NotAnIdea(string pageType, string? url)
    {
        data.Corrections[pageType] = (data.Corrections.GetValueOrDefault(pageType) ?? new Correction()) with { IsSpecific = false };
        if (url is not null)
        {
            var key = PageKey(url);
            data.Items.RemoveAll(x => PageKey(x.Url) == key);
        }
        NotifyChanged(reindexing: url is not null);
    }

    /// Puts a page type's correction back as it was before Not an Idea, for Undo (null: it had none).
    public void SetCorrection(Correction? correction, string pageType)
    {
        if (correction is null) data.Corrections.Remove(pageType); else data.Corrections[pageType] = correction;
        NotifyChanged();
    }

    // MARK: Learning

    /// Remembered pages are hashed by page key. A list from v1.1 hashed the cleaned link instead, so both are looked up;
    /// only the page key is written.
    string[] Hashes(string url) => [Hash(PageKey(url)), Hash(url)];

    public bool WasSeen(string url, DateTime now)
    {
        DateTime? when = Hashes(url).Where(data.Seen.ContainsKey).Select(h => (DateTime?)data.Seen[h]).Max();
        return when is { } seen && now - seen <= CameBackWindow;
    }

    public void MarkSeen(string url, DateTime now)
    {
        var hash = Hash(PageKey(url));
        bool same = data.Seen.TryGetValue(hash, out var old) && old == now;
        data.Seen[hash] = now;
        if (!same) NotifyChanged();
    }

    public bool IsDismissed(string url) => Hashes(url).Any(data.Dismissed.ContainsKey);

    /// The page's tab closed or was opened anew, so the user's delete no longer applies to it.
    public void Undismiss(string url)
    {
        if (Forget(url)) NotifyChanged();
    }

    bool Forget(string url)
    {
        bool any = false;
        foreach (var h in Hashes(url)) if (data.Dismissed.Remove(h)) any = true;
        return any;
    }

    /// Drops seen pages and dismissals older than 30 days. Runs at most once a day, so it can sit on every input.
    public void Prune(DateTime now)
    {
        if (lastPrune is { } last && (now - last).TotalSeconds < 86_400) return;
        lastPrune = now;
        bool Recent(DateTime when) => now - when <= CameBackWindow;
        var seen = data.Seen.Where(kv => Recent(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var dismissed = data.Dismissed.Where(kv => Recent(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (seen.Count == data.Seen.Count && dismissed.Count == data.Dismissed.Count) return;
        data.Seen = seen;
        data.Dismissed = dismissed;
        NotifyChanged();
    }

    public void AddDwellSample(double seconds)
    {
        data.DwellSamples.Add(seconds);
        if (data.DwellSamples.Count > MaxDwellSamples) data.DwellSamples.RemoveRange(0, data.DwellSamples.Count - MaxDwellSamples);
        NotifyChanged();
    }

    /// 2 minutes until there are 20 samples; then the 75th percentile of the user's on-screen time
    /// across their recent specific pages, clamped to 1-5 minutes.
    public TimeSpan DwellThreshold
    {
        get
        {
            if (data.DwellSamples.Count < 20) return TimeSpan.FromSeconds(120);
            var sorted = data.DwellSamples.Order().ToList();
            var p75 = sorted[(int)Math.Round((sorted.Count - 1) * 0.75, MidpointRounding.AwayFromZero)];
            return TimeSpan.FromSeconds(Math.Min(Math.Max(p75, 60), 300));
        }
    }

    // MARK: Helpers

    public static string HostOf(string url)
    {
        var host = ParsedUrl.Parse(url)?.Host ?? url;
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    void Reindex()
    {
        index.Clear();
        for (int i = 0; i < data.Items.Count; i++) index.TryAdd(PageKey(data.Items[i].Url), i);
    }

    /// Only changes that add, remove or reorder pages reindex: most changes (a visit, a title, a seen page) leave every
    /// link where it was.
    void NotifyChanged(bool reindexing = false)
    {
        if (reindexing) Reindex();
        Revision++;
        Changed?.Invoke();
    }
}
