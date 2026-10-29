import Foundation

/// Decides which open pages are ideas worth keeping. Fed a snapshot each time a browser's tabs change,
/// and told when the front app changes. Every input first judges the time that just passed, then applies
/// the new state. No timer: "spent time" is as precise as the browser's write interval, and nothing is shown.
@MainActor
public final class Judge {
    public static let leftOpenAfter: TimeInterval = 60

    /// A page is its page key, not its link: one page open as www and bare, or with noise in its query, is one page.
    struct Key: Hashable {
        var browser: Browser
        var page: String
    }

    struct OpenPage {
        var tab: Tab
        var url: URL
        var pageKey: String
        /// Its host without "www.", for noting which days the site is visited.
        var site: String
        /// Its verdict by the rules alone, before the user's corrections, which are applied on every read.
        var verdict: Verdict
    }

    /// A tab as the browser reports it. Its link and title are all its cleaned link and rule verdict depend on.
    struct TabKey: Hashable {
        var url: String
        var title: String
    }

    struct Record {
        /// The cleaned link the page was first seen with; what is saved if the page is kept.
        var url: String
        var title: String
        /// Nil while the page waits for its title to be judged; a pending page keeps its timing but is never saved.
        var verdict: Verdict?
        var onScreenSince: Date?
        var offScreenSince: Date?
        var activeSeconds: TimeInterval = 0
        var saved: Bool
        /// It arrived as a return visit; applied once the page is judged specific.
        var cameBack = false
        /// It sat a full minute off screen while pending; applied once the page is judged specific.
        var leftOpenQualified = false
    }

    private let library: Library
    /// The browsers Later watches. A dismissal ends only once each has shown this judge its pages.
    private var watched: Set<Browser>
    /// Watched browsers whose open pages are known: read at least once, or quit since.
    private var known: Set<Browser> = []
    private var open: [Browser: [Key: OpenPage]] = [:]
    private var records: [Key: Record] = [:]
    /// Each browser's open tabs, cleaned and judged, from its latest read, so an unchanged tab isn't cleaned and judged
    /// again on every read. Nil: a link that's never tracked.
    private var judged: [Browser: [TabKey: OpenPage?]] = [:]
    public private(set) var frontBrowser: Browser?
    /// The Mac is asleep or locked: nobody is looking, so no page is on screen and no page is off screen.
    private var away = false
    /// Whether home feeds (the YouTube home, X's Home) are kept too. Changing it judges every open tab again.
    public var savesFeedHomes: Bool {
        didSet { if savesFeedHomes != oldValue { judged = [:] } }
    }
    /// How many tabs hold each page, by page key, across every watched browser. Not saved.
    public private(set) var openCopies: [String: Int] = [:]
    private var copyCounts: [Browser: [String: Int]] = [:]

    public init(library: Library, watched: Set<Browser> = Set(Browser.allCases), savesFeedHomes: Bool = false) {
        self.library = library
        self.watched = watched
        self.savesFeedHomes = savesFeedHomes
    }

    // MARK: Inputs

    public func ingest(_ snapshot: Snapshot, now: Date) {
        library.prune(now: now)
        // One threshold per input, so pages ending together are judged alike whatever their order.
        let threshold = library.dwellThreshold
        evaluate(now: now, threshold: threshold)
        let browser = snapshot.browser
        var current: [Key: OpenPage] = [:]
        var copies: [String: Int] = [:]
        // Windows ranked by id, which grows as windows are made; each page's place is its leftmost tab in the oldest window.
        let windowIDs = Set(snapshot.tabs.map(\.windowID)).sorted()
        let rank = Dictionary(uniqueKeysWithValues: windowIDs.enumerated().map { ($1, $0) })
        var places: [String: (tab: Int, window: Int)] = [:]
        var sites: Set<String> = []
        let before = judged[browser] ?? [:]
        var after: [TabKey: OpenPage?] = [:]
        for tab in snapshot.tabs {
            let tabKey = TabKey(url: tab.url, title: tab.title)
            // A cached nil stays nil: that link is never tracked.
            let known = before[tabKey] ?? after[tabKey] ?? LinkCleaner.clean(tab.url).map {
                OpenPage(tab: tab, url: $0, pageKey: LinkCleaner.pageKey($0), site: Library.host(of: $0.absoluteString),
                         verdict: PageRules.judge($0, title: tab.title, savesFeedHomes: savesFeedHomes))
            }
            after[tabKey] = known
            guard var page = known else { continue }
            page.tab = tab   // the same page, but maybe now on screen or in another window
            let key = Key(browser: browser, page: page.pageKey)
            copies[page.pageKey, default: 0] += 1
            // Only what is in front counts as a visit, and only where the browser's own history can't say: a tab left open
            // in the background is not a day spent on its site.
            if !browser.hasReadableHistory, !away, frontBrowser == browser, tab.isActive, tab.windowIndex == 0 { sites.insert(page.site) }
            let place = (tab: tab.tabIndex, window: rank[tab.windowID] ?? 0)
            if places[page.pageKey].map({ (place.window, place.tab) < ($0.window, $0.tab) }) ?? true {
                places[page.pageKey] = place
            }
            // A page open in several tabs counts once; the on-screen copy wins.
            if let existing = current[key], existing.tab.isActive, existing.tab.windowIndex == 0 { continue }
            current[key] = page
        }
        judged[browser] = after
        library.noteSitesOpen(sites, day: Familiarity.dayNumber(of: now))
        copyCounts[browser] = copies
        updateCopies()
        let previous = open[browser]
        let isFirstRead = previous == nil
        open[browser] = current
        known.insert(browser)

        var dwellSamples: [TimeInterval] = []
        for key in records.keys where key.browser == browser && current[key] == nil {
            if let seconds = end(key, now: now, threshold: threshold) { dwellSamples.append(seconds) }
        }
        dwellSamples.forEach(library.addDwellSample)

        for (key, page) in current {
            let link = page.url.absoluteString
            let isNew = previous?[key] == nil
            if isNew, !isFirstRead {
                library.recordVisit(url: link, now: now)
                // Opened anew: a delete from an earlier visit doesn't carry over, unless another copy stayed open.
                if isClosedElsewhere(key) { library.undismiss(link) }
            }
            library.updateTitle(url: link, title: page.tab.title)
            // A reload briefly empties the title; keep the page's record as it was.
            if page.tab.title.isEmpty, records[key] != nil { continue }
            let verdict = PageRules.apply(library.data.corrections, to: page.verdict)
            let cameBack = isNew && !library.contains(url: link) && library.wasSeen(link, now: now)
            guard verdict.isSpecific else {
                // An empty title means it is still loading: time it from now, judge it when the title arrives.
                records[key] = page.tab.title.isEmpty
                    ? Record(url: link, title: "", verdict: nil, saved: isKept(link), cameBack: cameBack)
                    : nil   // corrected to "not an idea", or the title showed a general page
                continue
            }
            var record = records[key] ?? Record(url: link, title: "", verdict: nil, saved: isKept(link), cameBack: cameBack)
            let wasJudged = record.verdict != nil
            record.title = page.tab.title
            record.verdict = verdict
            records[key] = record
            if !wasJudged, record.cameBack { save(key, now: now) }
        }
        updateScreen(now: now)
        evaluate(now: now, threshold: threshold)
        // After the saves above, so a page saved by this read gets its place at once.
        library.setPlaces(current.values.compactMap { page in
            guard recordsPlace(of: page.pageKey, url: page.url.absoluteString, from: browser) else { return nil }
            return places[page.pageKey].map { (page.url.absoluteString, $0.tab, $0.window) }
        })
    }

    /// A page open in both browsers has one place on record, taken from the browser it was saved from while that still
    /// holds it, else from the first of the browsers that do, so each read doesn't overwrite the other's.
    private func recordsPlace(of pageKey: String, url: String, from browser: Browser) -> Bool {
        let holders = open.filter { $0.key == browser || $0.value[Key(browser: $0.key, page: pageKey)] != nil }.keys
        guard holders.count > 1 else { return true }
        let saved = library.browser(of: url)
        let preferred = saved.flatMap { holders.contains($0) ? $0 : nil }
            ?? holders.min { (Browser.allCases.firstIndex(of: $0) ?? 0) < (Browser.allCases.firstIndex(of: $1) ?? 0) }
        return preferred == browser
    }

    public func setFrontBrowser(_ browser: Browser?, now: Date) {
        let threshold = library.dwellThreshold
        evaluate(now: now, threshold: threshold)
        frontBrowser = browser
        updateScreen(now: now)
        evaluate(now: now, threshold: threshold)
    }

    /// The Mac went to sleep or locked (or came back). Time up to now keeps its credit; the time away counts as neither
    /// reading nor leaving a page open, so a page in front when the lid closes isn't saved for the night it sat there.
    public func setAway(_ value: Bool, now: Date) {
        guard value != away else { return }
        evaluate(now: now, threshold: library.dwellThreshold)
        away = value
        updateScreen(now: now)
        if value { for key in records.keys { records[key]?.offScreenSince = nil } }   // starts again at the wake
    }

    /// The browser's last copy quit. Time before now keeps its credit; then its pages are forgotten without
    /// ending them, so a relaunch that restores the session is a first read: no visits, nothing "came back".
    public func browserQuit(_ browser: Browser, now: Date) {
        evaluate(now: now, threshold: library.dwellThreshold)
        records = records.filter { $0.key.browser != browser }
        open[browser] = nil
        judged[browser] = nil
        copyCounts[browser] = nil
        updateCopies()
        known.insert(browser)   // a quit browser has nothing open
        if frontBrowser == browser { frontBrowser = nil }
        updateScreen(now: now)
    }

    /// A browser Later can't read (and won't without the user's yes) can't be hiding a copy of a page the user deleted: for
    /// dismissals it counts as known, with nothing open. Unlike a quit, it stays the front browser if it is.
    public func browserUnreadable(_ browser: Browser) {
        records = records.filter { $0.key.browser != browser }
        open[browser] = nil
        judged[browser] = nil
        copyCounts[browser] = nil
        updateCopies()
        known.insert(browser)
    }

    /// The browser launched: until its first read, it may be restoring pages this judge can't see.
    public func browserLaunched(_ browser: Browser) { known.remove(browser) }

    /// Later began watching the browser after this judge was made: its data folder appeared on its first launch.
    public func watch(_ browser: Browser) { watched.insert(browser) }

    // MARK: Internals

    private func updateCopies() {
        var total: [String: Int] = [:]
        for counts in copyCounts.values { total.merge(counts, uniquingKeysWith: +) }
        if total != openCopies { openCopies = total }
    }

    /// Saved already, or deleted by the user while its tab stayed open: either way it's never saved again by itself.
    private func isKept(_ url: String) -> Bool { library.contains(url: url) || library.isDismissed(url) }

    /// No other browser has the page open, and none could be hiding a copy: every watched browser is known.
    /// Callers have already updated `key.browser`'s own pages.
    private func isClosedElsewhere(_ key: Key) -> Bool {
        watched.isSubset(of: known)
            && !open.contains { $0.key != key.browser && $0.value[Key(browser: $0.key, page: key.page)] != nil }
    }

    /// Every save goes through here, so a page the user deleted is never saved again by itself.
    private func keep(_ key: Key, _ record: Record, _ verdict: Verdict, now: Date) -> Bool {
        guard !library.isDismissed(record.url) else { return false }
        library.save(url: record.url, title: record.title, verdict: verdict, browser: key.browser, now: now)
        return true
    }

    /// The active tab of the front window of the front browser.
    private var onScreenKey: Key? {
        guard !away, let browser = frontBrowser, let pages = open[browser] else { return nil }
        return pages.first { $0.value.tab.isActive && $0.value.tab.windowIndex == 0 }?.key
    }

    private func updateScreen(now: Date) {
        let onScreen = onScreenKey
        for key in Array(records.keys) {
            guard var record = records[key] else { continue }
            if key == onScreen {
                if record.onScreenSince == nil {
                    record.onScreenSince = now
                    record.offScreenSince = nil
                }
            } else if let since = record.onScreenSince {
                record.activeSeconds += now.timeIntervalSince(since)
                record.onScreenSince = nil
                record.offScreenSince = now
            } else if record.offScreenSince == nil, !away {   // no clock starts while away; the wake starts it
                record.offScreenSince = now
            }
            records[key] = record
        }
    }

    private func evaluate(now: Date, threshold: TimeInterval) {
        for (key, record) in records where !record.saved {
            if record.verdict == nil {
                // Every input evaluates before it moves pages on screen, so a completed minute is never lost.
                if leftOpen(record, now: now) { records[key]?.leftOpenQualified = true }
            } else if shouldSave(record, now: now, threshold: threshold) {
                save(key, now: now)
            }
        }
    }

    private func shouldSave(_ record: Record, now: Date, threshold: TimeInterval) -> Bool {
        guard record.verdict != nil else { return false }
        let active = record.activeSeconds + (record.onScreenSince.map { now.timeIntervalSince($0) } ?? 0)
        return record.leftOpenQualified || leftOpen(record, now: now) || active >= threshold
    }

    private func leftOpen(_ record: Record, now: Date) -> Bool {
        record.offScreenSince.map { now.timeIntervalSince($0) >= Self.leftOpenAfter } ?? false
    }

    /// The page's verdict with corrections made since it was judged; nil once it's "not an idea".
    private func currentVerdict(_ verdict: Verdict?) -> Verdict? {
        guard var verdict, let correction = library.data.corrections[verdict.pageType] else { return verdict }
        if correction.isSpecific == false { return nil }
        if let kind = correction.kind { verdict.kind = kind }
        return verdict
    }

    private func save(_ key: Key, now: Date) {
        guard var record = records[key], !record.saved else { return }
        guard let verdict = currentVerdict(record.verdict) else {
            records[key] = nil
            return
        }
        guard keep(key, record, verdict, now: now) else { return }
        record.saved = true
        records[key] = record
    }

    /// The page closed, or its tab navigated away. Returns its on-screen time to learn from, if any.
    private func end(_ key: Key, now: Date, threshold: TimeInterval) -> TimeInterval? {
        guard var record = records.removeValue(forKey: key) else { return nil }
        let verdict = currentVerdict(record.verdict)
        if let verdict, !record.saved, shouldSave(record, now: now, threshold: threshold) {
            record.saved = keep(key, record, verdict, now: now)
        }
        if isClosedElsewhere(key) { library.undismiss(record.url) }
        guard verdict != nil else { return nil }
        if let since = record.onScreenSince { record.activeSeconds += now.timeIntervalSince(since) }
        library.markSeen(record.url, now: now)
        if record.saved { library.touch(url: record.url, now: now) }
        return record.activeSeconds > 0 ? record.activeSeconds : nil
    }
}
