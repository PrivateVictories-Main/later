using System.ComponentModel;
using System.Globalization;
using Later.App.Actions;
using Later.Core;
using Later.Windows.Engine;

namespace Later.App.ViewModels;

/// What the engine says besides the list. `Statuses` is empty when no engine runs (a sample, a read-only list, before setup).
/// `LastBrowser`: the browser last in front, where "Open" goes first.
public sealed record EngineState(IReadOnlyDictionary<Browser, BrowserStatus> Statuses, bool Paused, string? Notice, bool CanEdit, bool Onboarded, Browser? LastBrowser = null)
{
    public static readonly EngineState None = new(new Dictionary<Browser, BrowserStatus>(), false, null, false, true);

    /// Where "Open" goes, worked out from what the engine tracks (no look at the machine's processes): the browser last in front if it
    /// runs, else any running one, else the first installed (Helium first). Null when the engine knows of none (no engine, or none installed).
    public Browser? OpenIn => Opener.BrowserFor(LastBrowser,
        b => Statuses.TryGetValue(b, out var s) && s is BrowserStatus.Watching or BrowserStatus.ReadFailed,
        b => Statuses.TryGetValue(b, out var s) && s != BrowserStatus.NotInstalled);

    /// The statuses by content: the engine hands out a new dictionary on every call, so a reference comparison never says "same".
    public bool Equals(EngineState? other) =>
        other is not null && Paused == other.Paused && Notice == other.Notice && CanEdit == other.CanEdit && Onboarded == other.Onboarded && LastBrowser == other.LastBrowser
        && Statuses.Count == other.Statuses.Count && Statuses.All(kv => other.Statuses.TryGetValue(kv.Key, out var s) && s == kv.Value);

    public override int GetHashCode() => HashCode.Combine(Paused, Notice, CanEdit, Onboarded, Statuses.Count, LastBrowser);
}

/// The window's state, for its three panes: the port of `LibraryModel.swift`'s section, search, grouping, sort, folding, selection,
/// listing, counts and wake. UI thread only. The engine thread owns the `Library`; what comes here are snapshots (`SetSnapshot`),
/// posted by `AppEngine`, and the user's changes go back through `IListEditor`.
public sealed class LibraryViewModel : INotifyPropertyChanged
{
    /// Everything the panes' listing depends on; it is only worked out again when this changes, and not at all while hidden.
    readonly record struct Key(int Revision, LibrarySection Section, string Query, DateTime Now, LibraryGrouping Grouping, LibrarySort Sort, bool Reversed, int Folds);

    /// A change the user made, whose result has not arrived yet: the pages it touched and the rows before it.
    sealed record Pending(int Token, HashSet<Guid> Ids, IReadOnlyList<Guid> Before);

    readonly Library pageKeys = new();   // only for `PageKey`, which needs no list
    readonly HashSet<string> collapsed = [];
    readonly List<Pending> pending = [];
    LibraryData data = new();
    IReadOnlyDictionary<string, int> openCopies = new Dictionary<string, int>();
    IReadOnlyDictionary<string, int> visitDays = new Dictionary<string, int>();
    int revision, folds, tokens;
    LibrarySection section = LibrarySection.All;
    string search = "";
    LibraryGrouping grouping = LibraryGrouping.Date;
    LibrarySort sort = LibrarySort.LastSeen;
    bool sortReversed;
    EngineState engineState = EngineState.None;
    HashSet<Guid> selected = [];
    Guid? cursor;
    Key? cachedKey;
    (int Revision, DateTime Now)? cachedCounts;
    List<Item>? selectedItems;

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
    public bool CanEdit => engineState.CanEdit;

    /// The newest saved list, for the panes that need more than the listing (the inspector's facts).
    public LibraryData Data => data;

    /// The last change the user made, for the Undo bar: its name, and a number that grows with each, so two of a kind are two.
    public string? UndoName { get; private set; }
    public int UndoVersion { get; private set; }

    public LibrarySection Section
    {
        get => section;
        set { if (section != value) { section = value; ClearSelectionQuietly(); Changed(nameof(Section)); Refresh(); RaiseSelection(); } }
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

    /// The grouping the headings really follow: kind inside one kind falls back to dates.
    public LibraryGrouping EffectiveGrouping => LibraryListing.EffectiveGrouping(section, grouping);

    /// Whether headings follow the grouping (not in a search nor in tab order).
    public bool GroupingActive => LibraryListing.GroupingActive(Searching, sort);

    /// From the toolbar's field, on every keystroke.
    public void SetSearch(string text)
    {
        if (text == search) return;
        search = text;
        ClearSelectionQuietly();
        Changed(nameof(Search));
        Refresh();
        RaiseSelection();
    }

    /// A newer copy of the list (from the engine thread's `Library.CopyData()`), the tabs open now by page key, and on how many of
    /// the last 28 days each site was visited (for the inspector's "why" line).
    public void SetSnapshot(LibraryData snapshot, IReadOnlyDictionary<string, int> copies, IReadOnlyDictionary<string, int>? days = null)
    {
        data = snapshot;
        openCopies = copies;
        visitDays = days ?? visitDays;
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

    // MARK: Folding

    public bool IsFolded(string groupTitle) => collapsed.Contains(FoldKeyOf(groupTitle));

    string FoldKeyOf(string groupTitle) => LibraryListing.FoldKey(EffectiveGrouping, sort, Searching, groupTitle);

    /// Folds a heading away, or brings it back. The pages stay in the listing; only their rows go.
    public void ToggleFold(string groupTitle)
    {
        var key = FoldKeyOf(groupTitle);
        if (!collapsed.Remove(key)) collapsed.Add(key);
        folds++;
        Refresh();
    }

    // MARK: Selection

    /// The selected pages, by id. Pages the listing does not show are not selected.
    public IReadOnlySet<Guid> Selected => selected;

    /// Where the keyboard is: the page the selection moves on from.
    public Guid? Cursor => cursor;

    /// The selected pages in the listing's order.
    public IReadOnlyList<Item> SelectedItems => selectedItems ??= Current.Items.Where(i => selected.Contains(i.Id)).ToList();

    public void SetSelection(IEnumerable<Guid> ids, Guid? newCursor = null)
    {
        var next = ids.ToHashSet();
        var nextCursor = newCursor is { } c && next.Contains(c) ? c : cursor is { } old && next.Contains(old) ? old : next.Count > 0 ? next.First() : (Guid?)null;
        if (next.SetEquals(selected) && nextCursor == cursor) return;
        selected = next;
        cursor = nextCursor;
        selectedItems = null;
        RaiseSelection();
    }

    public void SelectAll() => SetSelection(Current.RowIds);

    public void ClearSelection() => SetSelection([]);

    void ClearSelectionQuietly() { selected = []; cursor = null; selectedItems = null; }

    void RaiseSelection() => Changed(nameof(Selected));

    // MARK: The user's changes

    /// Before a change to `ids` is sent: remembers the rows, so that once the result arrives the selection moves on from what went.
    public int BeginChange(IEnumerable<Guid> ids)
    {
        int token = ++tokens;
        pending.Add(new Pending(token, ids.ToHashSet(), Current.RowIds));
        return token;
    }

    /// The result of a change (and of those before it) has been sent: the selection moves on, and the change is offered for Undo.
    /// Token 0 is for Undo and Redo themselves, which offer nothing.
    public void ChangeApplied(int token, string? undoName)
    {
        bool moved = false;
        foreach (var p in pending.Where(p => p.Token <= token).OrderBy(p => p.Token).ToList())
        {
            pending.Remove(p);
            var (keep, at) = SelectionKeep.Keep(selected, cursor, Current.RowIds, p.Ids, p.Before);
            if (!keep.SetEquals(selected) || at != cursor) { selected = keep; cursor = at; selectedItems = null; moved = true; }
        }
        if (token > 0 && undoName is not null) { UndoName = undoName; UndoVersion++; Changed(nameof(UndoName)); }
        if (moved) RaiseSelection();
    }

    /// The Undo bar was dismissed, or the change undone.
    public void ClearUndoOffer()
    {
        if (UndoName is null) return;
        UndoName = null;
        Changed(nameof(UndoName));
    }

    // MARK: Words for one page

    /// One line on why the page is where it is.
    public string Why(Item item) => LibraryListing.Why(item, data, visitDays);

    Key CurrentKey(DateTime now) => new(revision, section, Query, now, grouping, sort, sortReversed, folds);

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
        Current = LibraryListing.Build(data, openCopies, pageKeys.PageKey, section, key.Query, Now, grouping, sort, sortReversed, collapsed,
                                       TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        selectedItems = null;
        // Pages that are no longer listed are no longer selected. Not while a change of the user's is on its way: where the selection goes
        // on from is worked out from what it was (see `ChangeApplied`).
        bool pruned = false;
        if (pending.Count == 0)
        {
            var listed = Current.Items.Select(i => i.Id).ToHashSet();
            pruned = selected.RemoveWhere(id => !listed.Contains(id)) > 0;
            if (cursor is { } c && !listed.Contains(c)) { cursor = selected.Count > 0 ? selected.First() : null; pruned = true; }
        }
        Changed(nameof(Current));
        if (pruned) RaiseSelection();
    }

    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
