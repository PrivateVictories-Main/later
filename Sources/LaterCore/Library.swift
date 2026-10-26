import CryptoKit
import Foundation
import Observation

@MainActor
@Observable
public final class Library {
    public private(set) var data: LibraryData
    /// Bumped on every change, so a view of the list can tell in one comparison whether it's out of date.
    public private(set) var revision = 0
    /// Called after every change, so the app can schedule a save.
    @ObservationIgnored public var onChange: (@MainActor () -> Void)?
    /// Saved pages by page key (see `LinkCleaner.pageKey`), so one page is one item however its link was written.
    @ObservationIgnored private var index: [String: Int] = [:]
    @ObservationIgnored private var keys: [String: String] = [:]
    @ObservationIgnored private var lastPrune: Date?
    /// Links of Helium pages saved since the history was last asked, so each is asked about once. Not saved.
    @ObservationIgnored private var awaitingHistory: [String] = []

    public static let cameBackWindow: TimeInterval = 30 * 86_400
    static let maxDwellSamples = 200

    public init(data: LibraryData = LibraryData()) {
        self.data = data
        reindex()
    }

    // MARK: Lookup

    public func contains(url: String) -> Bool { index[pageKey(of: url)] != nil }

    /// The link's page key, remembered: the judge asks about every open page on every read.
    public func pageKey(of url: String) -> String {
        if let key = keys[url] { return key }
        if keys.count > 4_096 { keys.removeAll() }
        let key = URL(string: url).map(LinkCleaner.pageKey) ?? url
        keys[url] = key
        return key
    }
    // MARK: Saving

    /// Saves a page the judge decided to keep. Does nothing if it's already here, active or done.
    /// A page without a title yet is saved under its host; `updateTitle` replaces that when the real title arrives.
    @discardableResult
    public func save(url: String, title: String, verdict: Verdict, browser: Browser, now: Date) -> Bool {
        let key = pageKey(of: url)
        guard index[key] == nil else { return false }
        let host = Self.host(of: url)
        let blank = title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        data.items.append(Item(id: UUID(), url: url, title: blank ? host : title, host: host, kind: verdict.kind,
                               pageType: verdict.pageType, browser: browser, firstSaved: now, lastSeen: now,
                               visits: 1, status: .active, doneAt: nil))
        if browser == .helium { awaitingHistory.append(url) }
        index[key] = data.items.count - 1
        changed()
        return true
    }

    // MARK: Opened time and place

    /// The links of Helium pages saved since the last call, for the history to be asked about.
    public func takeAwaitingHistory() -> [String] {
        defer { awaitingHistory = [] }
        return awaitingHistory
    }

    /// Links of Helium pages whose opening time isn't known yet, for the one-time backfill when the list loads.
    public func linksMissingHistory() -> [String] {
        data.items.filter { $0.openedAt == nil && $0.browser == .helium && $0.historyChecked != $0.url }.map(\.url)
    }

    /// Remembers that the history was asked about these links and had nothing to say about some, so the next launch's
    /// backfill doesn't ask again. Saved: it is the only way the answer outlives this launch.
    public func markHistoryChecked(_ links: [String]) {
        var any = false
        for link in links {
            guard let i = index[pageKey(of: link)], data.items[i].openedAt == nil, data.items[i].historyChecked != data.items[i].url else { continue }
            data.items[i].historyChecked = data.items[i].url
            any = true
        }
        if any { changed() }
    }

    /// The browser a saved page was first saved from.
    public func browser(of url: String) -> Browser? { index[pageKey(of: url)].map { data.items[$0].browser } }

    /// Records what the browser's history says about pages, by link. A page's opening time is only ever filled in, never
    /// replaced; its visit count follows the history. Returns how many pages changed.
    @discardableResult
    public func applyHistory(_ results: [String: HistoryLookup.Result]) -> Int {
        var changes = 0
        for (url, result) in results {
            guard let i = index[pageKey(of: url)] else { continue }
            let openedAt = data.items[i].openedAt ?? result.firstVisit
            guard openedAt != data.items[i].openedAt || result.visitCount != data.items[i].historyVisits else { continue }
            data.items[i].openedAt = openedAt
            data.items[i].historyVisits = result.visitCount
            changes += 1
        }
        if changes > 0 { changed() }
        return changes
    }

    /// Where open pages' tabs are now, by link: the place in the tab strip and the window's rank. A change is only
    /// announced when a place really moved.
    public func setPlaces(_ places: [(url: String, tab: Int, window: Int)]) {
        var moved = false
        for place in places {
            guard let i = index[pageKey(of: place.url)],
                  data.items[i].tabIndex != place.tab || data.items[i].windowRank != place.window else { continue }
            data.items[i].tabIndex = place.tab
            data.items[i].windowRank = place.window
            moved = true
        }
        if moved { changed() }
    }

    /// Puts every saved page's kind back to what the current rules say, except pages whose type the user moved themselves.
    /// Runs once when the list loads, before anything is shown or saved. Only kinds change: nothing is removed or
    /// un-saved, and no title, date or link is touched. Returns counts only, for the log.
    @discardableResult
    public func refreshKinds() -> (items: Int, changed: Int, toTools: Int) {
        var changed = 0, toTools = 0
        for i in data.items.indices {
            let item = data.items[i]
            guard data.corrections[item.pageType]?.kind == nil, let url = URL(string: item.url), url.host != nil else { continue }
            let kind = PageRules.judge(url, title: item.title, corrections: data.corrections).kind
            guard kind != item.kind else { continue }
            data.items[i].kind = kind
            changed += 1
            if kind == .tools { toTools += 1 }
        }
        return (data.items.count, changed, toTools)
    }

    /// Folds saved pages that are really one page (the same link written two ways) into one item: the earliest save, all
    /// the visits, the latest sighting and title, done if a copy was marked done after the others were last seen. the user's kind for a page type is kept.
    /// Runs once when the list loads. Returns how many items went away.
    @discardableResult
    public func mergeDuplicates() -> Int {
        var groups: [String: [Item]] = [:]
        var order: [String] = []
        for item in data.items {
            let key = pageKey(of: item.url)
            if groups[key] == nil { order.append(key) }
            groups[key, default: []].append(item)
        }
        guard groups.values.contains(where: { $0.count > 1 }) else { return 0 }
        data.items = order.map { key in
            let copies = groups[key]!
            guard copies.count > 1, var merged = copies.min(by: { $0.firstSaved < $1.firstSaved }),
                  let latest = copies.max(by: { $0.lastSeen < $1.lastSeen }) else { return copies[0] }
            let moved = copies.first { data.corrections[$0.pageType]?.kind != nil } ?? latest
            merged.visits = copies.reduce(0) { $0 + $1.visits }
            merged.openedAt = copies.compactMap(\.openedAt).min()
            merged.historyVisits = copies.compactMap(\.historyVisits).max()
            merged.tabIndex = latest.tabIndex
            merged.windowRank = latest.windowRank
            merged.lastSeen = latest.lastSeen
            merged.title = latest.title
            merged.pageType = moved.pageType
            merged.kind = moved.kind
            // Done if a copy was marked done after every active copy was last seen; otherwise the user has been back to it.
            let doneAt = copies.filter { $0.status == .done }.map { $0.doneAt ?? $0.lastSeen }.max()
            let activeSeen = copies.filter { $0.status == .active }.map(\.lastSeen).max()
            let isDone = doneAt.map { done in activeSeen.map { done > $0 } ?? true } ?? false
            merged.status = isDone ? .done : .active
            merged.doneAt = isDone ? doneAt : nil
            return merged
        }
        let removed = order.reduce(0) { $0 + groups[$1]!.count - 1 }
        changed(reindexing: true)
        return removed
    }

    public func recordVisit(url: String, now: Date) {
        guard let i = index[pageKey(of: url)], data.items[i].status == .active else { return }
        data.items[i].visits += 1
        data.items[i].lastSeen = now
        changed()
    }

    public func touch(url: String, now: Date) {
        guard let i = index[pageKey(of: url)] else { return }
        data.items[i].lastSeen = now
        changed()
    }

    public func updateTitle(url: String, title: String) {
        guard let i = index[pageKey(of: url)], !title.isEmpty, data.items[i].title != title else { return }
        data.items[i].title = title
        changed()
    }

    // MARK: The user's actions

    public func setDone(_ id: UUID, _ done: Bool, now: Date) {
        guard let i = data.items.firstIndex(where: { $0.id == id }) else { return }
        data.items[i].status = done ? .done : .active
        data.items[i].doneAt = done ? now : nil
        changed()
    }

    /// Deleted pages are remembered as dismissed, so a tab still open isn't saved again after Later restarts.
    public func delete(_ id: UUID, now: Date) {
        guard let i = data.items.firstIndex(where: { $0.id == id }) else { return }
        data.dismissed[Self.hash(pageKey(of: data.items.remove(at: i).url))] = now
        changed(reindexing: true)
    }

    /// Puts a deleted item back at its old position, for Undo. If its page was saved again since,
    /// the original replaces the newer entry, so there's still one entry per page.
    public func restore(_ item: Item, at position: Int) { restore([(item, position)]) }

    /// Puts several deleted items back, in ascending order of their old positions, as one change.
    public func restore(_ entries: [(item: Item, position: Int)]) {
        for (item, position) in entries {
            let key = pageKey(of: item.url)
            if let i = data.items.firstIndex(where: { pageKey(of: $0.url) == key }) { data.items.remove(at: i) }
            forget(item.url)
            data.items.insert(item, at: min(max(position, 0), data.items.count))
        }
        changed(reindexing: true)
    }

    public func emptyDone(now: Date) {
        for item in data.items where item.status == .done { data.dismissed[Self.hash(pageKey(of: item.url))] = now }
        data.items.removeAll { $0.status == .done }
        changed(reindexing: true)
    }

    /// Sets the kind for this item's page type, and for every saved item of that type.
    public func move(_ id: UUID, to kind: Kind) {
        guard let item = data.items.first(where: { $0.id == id }) else { return }
        data.corrections[item.pageType, default: Correction()].kind = kind
        for i in data.items.indices where data.items[i].pageType == item.pageType { data.items[i].kind = kind }
        changed()
    }

    /// Puts pages' kinds back as they were before a move, for Undo.
    public func setKinds(_ kinds: [UUID: Kind]) {
        for i in data.items.indices { if let kind = kinds[data.items[i].id] { data.items[i].kind = kind } }
        changed()
    }

    public func notAnIdea(_ id: UUID) {
        guard let item = data.items.first(where: { $0.id == id }) else { return }
        notAnIdea(pageType: item.pageType, url: item.url)
    }

    /// Marks the page type general and removes that one page from the list.
    public func notAnIdea(pageType: String, url: String?) {
        data.corrections[pageType, default: Correction()].isSpecific = false
        if let url { let key = pageKey(of: url); data.items.removeAll { pageKey(of: $0.url) == key } }
        changed(reindexing: url != nil)
    }

    /// Puts a page type's correction back as it was before Not an Idea, for Undo (nil: it had none).
    public func setCorrection(_ correction: Correction?, for pageType: String) {
        data.corrections[pageType] = correction
        changed()
    }

    // MARK: Learning

    /// Remembered pages are hashed by page key. A list from v1.1 hashed the cleaned link instead, so both are looked up;
    /// only the page key is written.
    private func hashes(_ url: String) -> [String] { [Self.hash(pageKey(of: url)), Self.hash(url)] }

    public func wasSeen(_ url: String, now: Date) -> Bool {
        guard let when = hashes(url).compactMap({ data.seen[$0] }).max() else { return false }
        return now.timeIntervalSince(when) <= Self.cameBackWindow
    }

    public func markSeen(_ url: String, now: Date) {
        guard data.seen.updateValue(now, forKey: Self.hash(pageKey(of: url))) != now else { return }
        changed()
    }

    public func isDismissed(_ url: String) -> Bool { hashes(url).contains { data.dismissed[$0] != nil } }

    /// The page's tab closed or was opened anew, so the user's delete no longer applies to it.
    public func undismiss(_ url: String) {
        guard forget(url) else { return }
        changed()
    }

    @discardableResult
    private func forget(_ url: String) -> Bool {
        hashes(url).reduce(false) { data.dismissed.removeValue(forKey: $1) != nil || $0 }
    }

    /// Drops seen pages and dismissals older than 30 days. Runs at most once a day, so it can sit on every input.
    public func prune(now: Date) {
        if let lastPrune, now.timeIntervalSince(lastPrune) < 86_400 { return }
        lastPrune = now
        let recent = { (when: Date) in now.timeIntervalSince(when) <= Self.cameBackWindow }
        let seen = data.seen.filter { recent($0.value) }, dismissed = data.dismissed.filter { recent($0.value) }
        guard seen.count != data.seen.count || dismissed.count != data.dismissed.count else { return }
        data.seen = seen
        data.dismissed = dismissed
        changed()
    }

    public func addDwellSample(_ seconds: Double) {
        data.dwellSamples.append(seconds)
        if data.dwellSamples.count > Self.maxDwellSamples {
            data.dwellSamples.removeFirst(data.dwellSamples.count - Self.maxDwellSamples)
        }
        changed()
    }

    /// 2 minutes until there are 20 samples; then the 75th percentile of the user's on-screen time
    /// across their recent specific pages, clamped to 1–5 minutes.
    public var dwellThreshold: TimeInterval {
        guard data.dwellSamples.count >= 20 else { return 120 }
        let sorted = data.dwellSamples.sorted()
        let p75 = sorted[Int((Double(sorted.count - 1) * 0.75).rounded())]
        return min(max(p75, 60), 300)
    }

    // MARK: Helpers

    public static func host(of url: String) -> String {
        let host = URL(string: url)?.host ?? url
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    static func hash(_ url: String) -> String {
        SHA256.hash(data: Data(url.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    private func reindex() {
        index = Dictionary(data.items.enumerated().map { (pageKey(of: $1.url), $0) }, uniquingKeysWith: { first, _ in first })
    }

    /// Only changes that add, remove or reorder pages reindex: most changes (a visit, a title, a seen page) leave every
    /// link where it was.
    private func changed(reindexing: Bool = false) {
        if reindexing { reindex() }
        revision += 1
        onChange?()
    }
}
