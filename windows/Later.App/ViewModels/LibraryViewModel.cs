using System.ComponentModel;
using Later.Core;
using Later.Windows.Engine;

namespace Later.App.ViewModels;

/// What a section or search shows. Task 8 replaces this with the Core `Listing` (groups, copies, hints).
public sealed record Listing(IReadOnlyList<Item> Items, int NewToday, int Sites)
{
    public static readonly Listing Empty = new([], 0, 0);
}

/// What the engine says besides the list. `Statuses` is empty when no engine runs (a sample, a read-only list, before setup).
public sealed record EngineState(IReadOnlyDictionary<Browser, BrowserStatus> Statuses, bool Paused, string? Notice, bool CanEdit, bool Onboarded)
{
    public static readonly EngineState None = new(new Dictionary<Browser, BrowserStatus>(), false, null, false, true);

    /// The statuses by content: the engine hands out a new dictionary on every call, so a reference comparison never says "same".
    public bool Equals(EngineState? other) =>
        other is not null && Paused == other.Paused && Notice == other.Notice && CanEdit == other.CanEdit && Onboarded == other.Onboarded
        && Statuses.Count == other.Statuses.Count && Statuses.All(kv => other.Statuses.TryGetValue(kv.Key, out var s) && s == kv.Value);

    public override int GetHashCode() => HashCode.Combine(Paused, Notice, CanEdit, Onboarded, Statuses.Count);
}

/// The window's state, for its three panes: the port of `LibraryModel.swift`'s section, search, grouping, sort, listing, counts and
/// wake. UI thread only. The engine thread owns the `Library`; what comes here are snapshots (`SetSnapshot`), posted by `AppEngine`.
public sealed class LibraryViewModel : INotifyPropertyChanged
{
    /// Everything the panes' listing depends on; it is only worked out again when this changes, and not at all while hidden.
    readonly record struct Key(int Revision, LibrarySection Section, string Query, DateTime Now, LibraryGrouping Grouping, LibrarySort Sort, bool Reversed);

    readonly Library pageKeys = new();   // only for `PageKey`, which needs no list
    LibraryData data = new();
    IReadOnlyDictionary<string, int> openCopies = new Dictionary<string, int>();
    int revision;
    LibrarySection section = LibrarySection.All;
    string search = "";
    LibraryGrouping grouping = LibraryGrouping.Date;
    LibrarySort sort = LibrarySort.LastSeen;
    bool sortReversed;
    EngineState engineState = EngineState.None;
    Key? cachedKey;
    (int Revision, DateTime Now)? cachedCounts;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// `now`: the clock the first listing is worked out at (a sample run pins it); the real time by default.
    public LibraryViewModel(DateTime? now = null) => Now = now ?? DateTime.UtcNow;

    /// False until the first list arrives (`SetSnapshot`). An unloaded model is not an empty library: the panes show nothing of
    /// the list yet, not the first-run welcome and not zero counts.
    public bool IsLoaded { get; private set; }

    /// False while the window is hidden: the panes stop following the list, so saves made meanwhile cost nothing.
    public bool IsVisible { get; private set; }

    public DateTime Now { get; private set; }
    public IReadOnlyDictionary<LibrarySection, int> Counts { get; private set; } = new Dictionary<LibrarySection, int>();
    public Listing Current { get; private set; } = Listing.Empty;
    public EngineState Engine => engineState;

    public LibrarySection Section
    {
        get => section;
        set { if (section != value) { section = value; Changed(nameof(Section)); Refresh(); } }
    }

    public string Search => search;
    /// The search as `LibraryQuery` uses it: a whitespace-only search counts as no search.
    public string Query => search.Trim();
    public bool Searching => Query.Length > 0;

    public LibraryGrouping Grouping
    {
        get => grouping;
        set { if (grouping != value) { grouping = value; Changed(nameof(Grouping)); Refresh(); } }
    }

    public LibrarySort Sort
    {
        get => sort;
        set { if (sort != value) { sort = value; Changed(nameof(Sort)); Refresh(); } }
    }

    public bool SortReversed
    {
        get => sortReversed;
        set { if (sortReversed != value) { sortReversed = value; Changed(nameof(SortReversed)); Refresh(); } }
    }

    /// From the toolbar's field, on every keystroke.
    public void SetSearch(string text)
    {
        if (text == search) return;
        search = text;
        Changed(nameof(Search));
        Refresh();
    }

    /// A newer copy of the list (from the engine thread's `Library.CopyData()`) and the tabs open now, by page key.
    public void SetSnapshot(LibraryData snapshot, IReadOnlyDictionary<string, int> copies)
    {
        data = snapshot;
        openCopies = copies;
        revision++;
        bool first = !IsLoaded;
        IsLoaded = true;
        Refresh();
        if (first) Changed(nameof(IsLoaded));
    }

    public void SetEngineState(EngineState state)
    {
        if (state == engineState) return;
        engineState = state;
        Changed(nameof(Engine));
    }

    /// The window shows: the clock moves on, and so the panes redraw, only if what they last drew is out of date: another list,
    /// section, search, grouping, sort or open pages, or looking different by now (a new day, or a page moved to Older).
    /// Otherwise showing redraws nothing.
    public void Wake(DateTime now)
    {
        IsVisible = true;
        bool outdated = cachedKey != CurrentKey(Now) || cachedCounts != (revision, Now) || !LibraryQuery.LooksSame(data, Now, now);
        if (outdated) Now = now;
        Refresh();
    }

    public void Hide() => IsVisible = false;

    Key CurrentKey(DateTime now) => new(revision, section, Query, now, grouping, sort, sortReversed);

    void Refresh()
    {
        if (!IsVisible && cachedKey is not null) return;
        var key = CurrentKey(Now);
        if (cachedCounts != (revision, Now))
        {
            Counts = LibraryQuery.Counts(data, Now);
            cachedCounts = (revision, Now);
            Changed(nameof(Counts));
        }
        if (cachedKey == key) return;
        cachedKey = key;
        // Which items are open now: only the tab orders look at it.
        HashSet<Guid>? open = null;
        if (sort.IsTabOrder() && openCopies.Count > 0)
            open = data.Items.Where(i => openCopies.ContainsKey(pageKeys.PageKey(i.Url))).Select(i => i.Id).ToHashSet();
        var items = LibraryQuery.Items(data, section, key.Query, Now, sort, sortReversed, open);
        var today = Now.ToLocalTime().Date;
        Current = new Listing(items, items.Count(i => i.FirstSaved.ToLocalTime() >= today), items.Select(i => i.Host).Distinct(StringComparer.Ordinal).Count());
        Changed(nameof(Current));
    }

    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
