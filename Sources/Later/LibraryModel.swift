import AppKit
import LaterCore
import Observation
import SwiftUI

/// What the library window shows and what is selected, shared by its sidebar, list and inspector. It lives as long as
/// the app, so the window reopens exactly as the user left it.
@MainActor @Observable
final class LibraryModel {
    let engine: Engine
    /// The app's undo history for the list (every change the user makes to it), not the window's (see AppDelegate.deleteUndo).
    let undo: UndoManager

    var section: LibrarySection = .all {
        didSet { if section != oldValue { selection.select(nil) } }
    }
    /// What's typed in the toolbar's search field.
    private(set) var search = ""
    var selection = ListSelection()
    /// Whether the grouping and sort are kept as settings. Not in a sample run, which starts by date and saves nothing.
    @ObservationIgnored var keepsChoices = !Sample.enabled
    /// Kept as a setting.
    var grouping = (Sample.enabled ? nil : LibraryGrouping(rawValue: UserDefaults.standard.string(forKey: "grouping") ?? "")) ?? .date {
        didSet { if keepsChoices { UserDefaults.standard.set(grouping.rawValue, forKey: "grouping") } }
    }
    /// Kept as a setting, like the grouping. Dates run newest first and text A to Z unless `sortReversed`.
    var sort = (Sample.enabled ? nil : LibrarySort(rawValue: UserDefaults.standard.string(forKey: "sort") ?? "")) ?? .lastSeen {
        didSet { if keepsChoices { UserDefaults.standard.set(sort.rawValue, forKey: "sort") } }
    }
    var sortReversed = !Sample.enabled && UserDefaults.standard.bool(forKey: "sortReversed") {
        didSet { if keepsChoices { UserDefaults.standard.set(sortReversed, forKey: "sortReversed") } }
    }
    /// Folded headings, as "grouping|title".
    var collapsed: Set<String> = []
    /// The list's clock. Its dates and headings go by the day, so it moves on at midnight, when the time zone changes and
    /// when the window opens to something new (see wake()): no timer.
    var now = Date()
    var dismissedGeneration: Int?
    var confirmingEmptyDone = false
    var opening: OpenRequest?
    /// False while the window is hidden: the panes stop following the list, so saves made meanwhile cost nothing. Not
    /// observed, so hiding and showing the window redraw nothing by themselves.
    @ObservationIgnored var isVisible = false

    /// Set by the window: put text in the search field (and start searching from there when `focus`).
    @ObservationIgnored var searchFieldText: (_ text: String, _ focus: Bool) -> Void = { _, _ in }

    init(engine: Engine, undo: UndoManager) {
        self.engine = engine
        self.undo = undo
    }

    /// The search as LibraryQuery uses it, so a whitespace-only search counts as no search.
    var query: String { search.trimmingCharacters(in: .whitespaces) }
    var searching: Bool { !query.isEmpty }

    // MARK: What the panes show

    struct Summary: Equatable {
        var pages = 0, newToday = 0, sites = 0
    }

    struct Listing {
        var items: [Item] = []
        var groups: [ItemGroup] = []
        /// The rows in order, without folded groups: what arrows move through.
        var rowIDs: [UUID] = []
        var summary = Summary()
        /// Tabs open now for each page here that is open, by item.
        var copies: [UUID: Int] = [:]
        /// A short hint for rows that share a site and title, by item.
        var hints: [UUID: String] = [:]
    }

    private struct Key: Equatable {
        var revision: Int, section: LibrarySection, query: String, now: Date, grouping: LibraryGrouping, collapsed: Set<String>
        var sort: LibrarySort, sortReversed: Bool, open: [String: Int]
    }

    private func currentKey(now: Date) -> Key {
        Key(revision: engine.library.revision, section: section, query: query, now: now, grouping: grouping,
            collapsed: collapsed, sort: sort, sortReversed: sortReversed, open: engine.openCopies)
    }

    @ObservationIgnored private var cached: (key: Key, listing: Listing)?
    @ObservationIgnored private var cachedCounts: (revision: Int, now: Date, counts: [LibrarySection: Int])?

    /// The current section or search, grouped: worked out once per change and shared by every pane. While the window is
    /// hidden it isn't recomputed, and nothing reading it is told about changes to the list.
    var listing: Listing {
        let now = self.now   // observed while hidden too, so wake() can bring every pane up to date
        guard isVisible || cached == nil else { return cached!.listing }
        let library = engine.library
        let key = currentKey(now: now)
        if let cached, cached.key == key { return cached.listing }
        // Which items are open now: one lookup of each page's key, only while some page is open.
        var copies: [UUID: Int] = [:]
        if !key.open.isEmpty {
            for item in library.data.items { if let n = key.open[library.pageKey(of: item.url)] { copies[item.id] = n } }
        }
        let open = Set(copies.keys)
        let items = LibraryQuery.items(library.data, section: section, search: key.query, now: now, sort: sort,
                                       reversed: sortReversed, open: open)
        let groups = LibraryQuery.groups(items, by: effectiveGrouping, searching: searching, now: now, sort: sort,
                                       reversed: sortReversed, open: open)
        let today = Calendar.current.startOfDay(for: now)
        var listing = Listing(items: items, groups: groups)
        listing.copies = copies
        listing.hints = LibraryQuery.differentiators(items, now: now)
        listing.rowIDs = groups.flatMap { collapsed.contains(foldKey($0)) ? [] : $0.items.map(\.id) }
        listing.summary = Summary(pages: items.count, newToday: items.count(where: { $0.firstSaved >= today }),
                                  sites: Set(items.map(\.host)).count)
        cached = (key, listing)
        return listing
    }

    /// Every section's count, for the sidebar.
    var counts: [LibrarySection: Int] {
        let now = self.now
        guard isVisible || cachedCounts == nil else { return cachedCounts!.counts }
        let library = engine.library
        if let cachedCounts, cachedCounts.revision == library.revision, cachedCounts.now == now { return cachedCounts.counts }
        let counts = LibraryQuery.counts(library.data, now: now)
        cachedCounts = (library.revision, now, counts)
        return counts
    }

    /// The window opens: the clock moves on, and so the panes redraw, only if what they last drew is out of date: drawn
    /// for another list, section, search, grouping, sort, open pages, folding or clock (midnight and a new time zone move the clock while
    /// the window is hidden), or looking different by now, on a new day or with a page moved to Older. Otherwise opening
    /// redraws nothing.
    func wake(at date: Date = .now) {
        isVisible = true
        let library = engine.library
        let current = currentKey(now: now)
        let outdated = cached.map { $0.key != current } ?? false
            || cachedCounts.map { $0.revision != library.revision || $0.now != now } ?? false
            || !LibraryQuery.looksSame(library.data, at: now, as: date)   // by here `now` is the caches' own clock
        if outdated { now = date }   // every pane reads the clock, even while hidden
    }

    var selectedItems: [Item] { listing.items.filter { selection.selected.contains($0.id) } }

    func foldKey(_ group: ItemGroup) -> String {
        (searching ? "search" : sort.isTabOrder ? "tabs" : effectiveGrouping.rawValue) + "|" + group.title
    }

    func toggleFold(_ group: ItemGroup) {
        let key = foldKey(group)
        withAnimation(Motion.standard) { if collapsed.remove(key) == nil { collapsed.insert(key) } }
    }

    // MARK: Search

    /// From the toolbar's field, on every keystroke.
    func searchChanged(_ text: String) {
        guard text != search else { return }
        search = text
        selection.select(nil)
    }

    /// From the window: an overview's site, or Esc in the list.
    func setSearch(_ text: String, focus: Bool = false) {
        searchChanged(text)
        searchFieldText(text, focus)
    }

    /// Typing a letter in the list starts a search with it.
    func typeToSearch(_ characters: String) { setSearch(search + characters, focus: true) }

    // MARK: Selection

    /// A click: alone, it selects the page; ⌘ adds or removes it; ⇧ selects the run from the last click to it.
    /// (Not animated here: only the selection's highlight moves, by itself, so the inspector redraws once, not every frame.)
    func click(_ id: UUID) {
        let flags = NSEvent.modifierFlags
        selection.click(id, in: listing.rowIDs, command: flags.contains(.command), shift: flags.contains(.shift))
    }

    /// The arrow keys: up or down one page, to the first or last with ⌘, growing the selection with ⇧.
    func move(by step: Int, extending: Bool, toEnd: Bool) {
        selection.move(by: step, extending: extending, toEnd: toEnd, in: listing.rowIDs)
    }

    func selectAll() { selection.selectAll(listing.rowIDs) }

    /// Runs a change to some pages; when it takes selected ones out of this list, the selection moves on to the next.
    func perform(on ids: Set<UUID>, _ change: () -> Void) {
        let before = listing.rowIDs
        withAnimation(Motion.listChange, change)
        selection.keep(listing.rowIDs, after: ids, before: before)
    }
}

/// The app's motion: short and decisive, and none of the movement under Reduce Motion.
@MainActor
enum Motion {
    private static var reduced: Bool { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }
    /// The selection gliding from row to row.
    static var selection: Animation? { reduced ? nil : .snappy(duration: 0.2, extraBounce: 0.04) }
    /// Rows leaving or arriving.
    static var listChange: Animation? { reduced ? .easeOut(duration: 0.12) : .smooth(duration: 0.3) }
    /// Folding, notices and other layout changes.
    static var standard: Animation? { reduced ? nil : .smooth(duration: 0.26) }
    /// A row lighting up under the pointer.
    static var hoverFade: Animation? { reduced ? nil : .easeOut(duration: 0.14) }
    /// A button confirming itself ("Copied").
    static var quick: Animation? { reduced ? nil : .snappy }
    /// The notice bar arriving.
    static var notice: AnyTransition { reduced ? .opacity : .move(edge: .top).combined(with: .opacity) }
    /// An icon swapping for another.
    static var symbolSwap: ContentTransition { reduced ? .identity : .symbolEffect(.replace) }
    /// A row's Open and Done coming up under the pointer: they grow into place, or only fade in.
    static var hover: AnyTransition { reduced ? .opacity : .opacity.combined(with: .scale(scale: 0.85, anchor: .trailing)) }
}

/// Pages to open together, waiting for the user's OK when there are many.
struct OpenRequest {
    let items: [Item]
    /// Nil: wherever links usually open (see Opener).
    let browser: Browser?
}

/// Everything the user can do to saved pages, shared by the context menu, the row buttons, the keyboard and the inspector.
/// Opening only ever happens here, on their click or key.
@MainActor
struct ItemActions {
    let model: LibraryModel
    private var engine: Engine { model.engine }
    /// False while the list is read-only (see Engine.canEdit): every change is off.
    var canEdit: Bool { engine.canEdit }

    /// Opening more pages than this at once asks first, wherever it's asked for: Return, double-click, the menus or the
    /// inspector.
    static let openLimit = 10

    func open(_ items: [Item], in browser: Browser? = nil) {
        guard !items.isEmpty else { return }
        let request = OpenRequest(items: items, browser: browser)
        if items.count > Self.openLimit { model.opening = request } else { open(request) }
    }

    func open(_ request: OpenRequest) {
        for item in request.items {
            if let browser = request.browser { engine.open(item, in: browser) } else { engine.open(item) }
        }
    }

    func copyLinks(_ items: [Item]) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(items.map(\.url).joined(separator: "\n"), forType: .string)
    }

    func setDone(_ items: [Item], _ done: Bool) {
        change(items) { items.forEach { engine.library.setDone($0.id, done, now: .now, undo: model.undo) } }
    }
    func move(_ items: [Item], to kind: Kind) {
        change(items) { items.forEach { engine.library.move($0.id, to: kind, undo: model.undo) } }
    }
    func notAnIdea(_ items: [Item]) { change(items) { items.forEach { engine.library.notAnIdea($0.id, undo: model.undo) } } }
    func delete(_ items: [Item]) { change(items) { items.forEach { engine.library.delete($0.id, now: .now, undo: model.undo) } } }
    /// Everything in Done, as one Undo.
    func emptyDone(_ items: [Item]) { change(items) { engine.library.emptyDone(now: .now, undo: model.undo) } }

    /// Every change here is undoable, with Undo and Redo registered by the library (see LibraryUndo). Several pages
    /// changed at once are one Undo, since they're registered in the same turn of the run loop.
    private func change(_ items: [Item], _ body: () -> Void) {
        guard !items.isEmpty, engine.canEdit else { return }   // read-only: the buttons are disabled, and keys do nothing
        // Out of the search field first, so Edit ▸ Undo undoes this change, not the typing. The search text stays.
        for window in NSApp?.windows ?? [] where window.firstResponder is NSText { window.makeFirstResponder(nil) }
        model.perform(on: Set(items.map(\.id)), body)
    }
}
