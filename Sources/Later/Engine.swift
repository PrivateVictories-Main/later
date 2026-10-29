import AppKit
import LaterCore
import Observation

enum BrowserStatus: Equatable {
    case notInstalled, notRunning, watching, needsPermission, readFailed

    /// Statuses the window warns about: ones the user can fix, or real breakage.
    var isProblem: Bool { self == .needsPermission || self == .readFailed }

    /// Not watched yet: a browser opened for the first time (no data folder until then) or installed after Later started.
    var isUnwatched: Bool { self == .notRunning || self == .notInstalled }

    /// "Helium", "Helium and Chrome".
    static func names(_ browsers: [Browser]) -> String {
        ListFormatter.localizedString(byJoining: browsers.map(\.displayName))
    }

    func text(for browser: Browser) -> String {
        let name = browser.displayName
        return switch self {
        case .watching: "Watching \(name)"
        case .notRunning: "\(name) isn't open"
        case .needsPermission: "\(name) needs access. Allow it in Settings."
        case .readFailed: "Can't read \(name)'s tabs. A browser update may have changed something."
        case .notInstalled: "\(name) isn't installed"
        }
    }
}

/// What the engine asks of the outside world about a browser, so tests can stand in for it. Every default is a stand-in that
/// answers nothing, so an engine built in a test can never read a real browser or its history; only `real` does.
struct BrowserIO {
    var isRunning: (Browser) -> Bool = { _ in false }
    /// Nil: allowed. Never shows macOS's prompt.
    var permission: (Browser) async -> ReadError? = { _ in .notRunning }
    var read: (Browser) async throws -> Snapshot = { _ in throw ReadError.notRunning }
    /// When Helium's history says each link was first opened, and how often. Reads the History files, read-only.
    var history: @Sendable ([String]) -> [String: HistoryLookup.Result] = { _ in [:] }
    /// Which days of the last 28 each site was visited, from the same files.
    var visitDays: @Sendable (Date) -> [String: Set<Int>] = { _ in [:] }
    /// The browser folder `history` and `visitDays` read. Nil for a stand-in.
    var historyFolder: String?

    static var real: BrowserIO {
        let folder = RealHome.path + "/" + Browser.helium.dataFolder
        return BrowserIO(
            isRunning: { BrowserReader.mainInstance(of: $0) != nil },
            permission: { await BrowserReader.checkPermission($0) },
            read: { try await BrowserReader.read($0) },
            history: { HistoryLookup.lookup($0, in: HistoryLookup.databases(in: folder)) },
            visitDays: { since in
                VisitDaysCache.shared.days(since: since) { HistoryLookup.visitDays(in: HistoryLookup.databases(in: folder), since: $0) }
            },
            historyFolder: folder)
    }
}

/// The last History answer to "which days was each site visited", kept for the process: an engine started again (each
/// hand-off between the app and its helper) reuses it within the same day instead of reading the History again. An empty
/// answer (a failed read) is never kept.
final class VisitDaysCache: @unchecked Sendable {
    static let shared = VisitDaysCache()
    private let lock = NSLock()
    private var cached: (since: Date, days: [String: Set<Int>])?

    func days(since: Date, ask: (Date) -> [String: Set<Int>]) -> [String: Set<Int>] {
        lock.lock()
        defer { lock.unlock() }
        if let cached, cached.since == since { return cached.days }
        let days = ask(since)
        if !days.isEmpty { cached = (since, days) }
        return days
    }
}

/// Glue: doorbell or app switch → reader → judge → library → file. Everything runs on the main actor
/// except the Apple Events (reader queue) and the file write (writer queue). No timers except the
/// one-shot save coalescer.
@MainActor
@Observable
final class Engine {
    let library: Library
    let icons: SiteIcons
    @ObservationIgnored private(set) var judge: Judge
    private(set) var status: [Browser: BrowserStatus] = [:]
    /// A failed save shows over the startup notice, which returns once saving works again.
    var notice: String? {
        if saveFailed { return Self.saveFailedNotice }
        let text = [backgroundNotice, loadNotice].compactMap { $0 }.joined(separator: " ")
        return text.isEmpty ? nil : text
    }
    private var loadNotice: String?
    /// What the background helper reported while the window was closed (see AgentStatus).
    private var backgroundNotice: String?
    private var saveFailed = false
    /// Bumped whenever the notice changes, so a notice the user dismissed shows again when it comes back,
    /// even if it went and came back between two renders with the same text.
    private(set) var noticeGeneration = 0
    private(set) var paused: Bool
    private(set) var lastBrowser: Browser?
    /// Tabs holding each page, by page key, across every watched browser. Not saved; the list shows it as copies.
    private(set) var openCopies: [String: Int] = [:]
    /// Whether home feeds (the YouTube home, X's Home) are kept too. Off unless the user turns it on in Settings.
    private(set) var savesFeedHomes: Bool
    static let savesFeedHomesKey = "savesFeedHomes"

    private var store: LibraryStore?
    /// The store even while read-only, for taking over once the lock is free.
    private let candidate: LibraryStore?
    /// False while another process holds the engine lock: the list is shown, never changed or saved.
    private(set) var canEdit: Bool
    static let readOnlyNotice = "The background helper hasn't let go of your list yet, so it's read-only for now. Later tries again each time you come back to this window."
    /// Called when saving starts failing (true) or works again (false), for a process with no window to show it in.
    @ObservationIgnored var onSaveFailedChange: ((Bool) -> Void)?
    private let writer = DispatchQueue(label: "later.writer", qos: .utility)
    @ObservationIgnored private var bells: [Browser: Doorbell] = [:]
    /// Browsers macOS won't let Later watch for changes (Chrome's data folder is protected). They are read when the user
    /// switches into or out of them, when they launch, before the Mac sleeps or locks, and when Later's window opens.
    @ObservationIgnored private(set) var switchOnly: Set<Browser> = []
    @ObservationIgnored private let io: BrowserIO
    @ObservationIgnored private var saveScheduled = false
    @ObservationIgnored private var reading: Set<Browser> = []
    @ObservationIgnored private var readAgain: Set<Browser> = []
    /// Bumped on pause, resume and quit: a read that began before then belongs to a judge or session that's gone.
    @ObservationIgnored private var generation: [Browser: Int] = [:]
    @ObservationIgnored private var started = false
    /// Set by `stop()`: this engine has handed the list to another process and must never write it again.
    @ObservationIgnored private var stopped = false
    @ObservationIgnored private var observers: [(center: NotificationCenter, token: NSObjectProtocol)] = []
    @ObservationIgnored private var historyLookups = 0
    /// The day (see `Familiarity.dayNumber`) the history was last asked which days each site was visited.
    @ObservationIgnored private var visitDaysAsOf: Int?
    /// Saved pages removed at load because their link carried a secret; start() saves the list without them.
    @ObservationIgnored private var secretsDropped = 0

    /// `readOnly`: another process holds the engine lock, so this one shows the list as it is on disk and never recovers,
    /// watches or saves.
    /// `dropsSecretLinks`: whether this process removes saved pages whose link carries a secret. The background helper
    /// leaves them for the window app, which can tell the user how many went.
    init(readOnly: Bool = false, dropsSecretLinks: Bool = true) {
        canEdit = !readOnly
        io = .real
        let feeds = !Sample.enabled && UserDefaults.standard.bool(forKey: Self.savesFeedHomesKey)
        savesFeedHomes = feeds
        #if DEBUG
        let sample = Sample.data()
        #else
        let sample: LibraryData? = nil
        #endif
        let store = sample == nil ? try? LibraryStore.appDefault() : nil   // a sample list is never saved
        let loaded = sample.map { LibraryStore.Loaded(data: $0, notice: nil) }
            ?? (readOnly ? store?.peek(notice: Self.readOnlyNotice) : store?.load())
            ?? LibraryStore.Loaded(data: LibraryData(), notice: nil)
        // A damaged file that couldn't be set aside blocks saving, so it can never be overwritten.
        self.store = loaded.writable ? store : nil
        candidate = store
        library = Library(data: loaded.data)
        if sample == nil { secretsDropped = Self.tidy(library, dropsSecretLinks: dropsSecretLinks) }
        // A sample list's icons stay in memory, like the list itself.
        icons = SiteIcons(library: library, folder: sample == nil ? SiteIcons.folder : nil)
        judge = Judge(library: library, watched: [], savesFeedHomes: feeds)   // replaced in start(), once the watched browsers are known
        loadNotice = store == nil && sample == nil ? "Later can't open its storage folder, so saves won't be kept." : loaded.notice
        paused = !Sample.enabled && UserDefaults.standard.bool(forKey: "paused")
        if !readOnly { addSecretsNotice() }
        for browser in Browser.allCases {
            // Known before setup too. One that has never been opened has no data folder yet; it's watched once it has
            // (see ring). Until it's read, it counts as not open.
            status[browser] = browser.isInstalled ? .notRunning : .notInstalled
        }
        #if DEBUG
        if sample != nil {
            library.setHistoryDays(Sample.visitDays())
            status = Sample.statuses
            openCopies = Sample.openCopies(library)
        }
        #endif
        library.onChange = { [weak self] in self?.libraryChanged() }
        #if DEBUG
        if sample != nil { icons.show(samples: Sample.iconPNGs()) }
        #endif
    }

    #if DEBUG
    /// Tests only: an engine around `library`, in memory: nothing is read or saved, and no browser is looked at.
    init(testing library: Library, io: BrowserIO = BrowserIO(), store: LibraryStore? = nil, readOnly: Bool = false) {
        self.library = library
        self.io = io
        canEdit = !readOnly
        icons = SiteIcons(library: library, folder: nil)
        judge = Judge(library: library, watched: [])
        loadNotice = readOnly ? Self.readOnlyNotice : nil
        paused = false
        savesFeedHomes = false
        self.store = readOnly ? nil : store
        candidate = store
    }

    /// The browser folder this engine reads History from; nil for a stand-in.
    var historyFolder: String? { io.historyFolder }

    /// Tests only: waits for the reads in flight.
    func settle() async {
        for _ in 0..<500 where !reading.isEmpty || !readAgain.isEmpty || historyLookups > 0 { await Task.yield() }
    }
    #endif

    func start() {
        guard !started, !Sample.enabled, canEdit else { return }   // a sample list watches no browser
        started = true
        // Watching is the whole job: macOS must not quietly quit Later once its last window closes.
        ProcessInfo.processInfo.disableAutomaticTermination("Later watches your open tabs")
        library.prune(now: .now)
        if secretsDropped > 0 { libraryChanged() }
        let center = NSWorkspace.shared.notificationCenter
        func observe(_ center: NotificationCenter, _ name: Notification.Name, _ handler: @escaping @Sendable (Notification) -> Void) {
            observers.append((center, center.addObserver(forName: name, object: nil, queue: .main, using: handler)))
        }
        observe(center, NSWorkspace.didActivateApplicationNotification) { [weak self] note in
            Perf.log.info("wake appSwitch")
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated { self?.frontAppChanged(bundleID: id) }
        }
        observe(center, NSWorkspace.didLaunchApplicationNotification) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated {
                guard let self, let browser = Browser.matching(bundleID: id) else { return }
                if !self.paused { self.judge.browserLaunched(browser) }
                self.ring(browser)
            }
        }
        observe(center, NSWorkspace.didTerminateApplicationNotification) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated {
                guard let self, let browser = Browser.matching(bundleID: id) else { return }
                self.browserTerminated(browser)
            }
        }
        // A browser Later can't watch for changes is read as the Mac goes to sleep or locks, so what was open then is
        // known: the user may be away long enough for their tabs to have been waiting.
        // Time asleep or locked is neither reading nor leaving a page open (see Judge.setAway).
        for (name, reason, away) in [(NSWorkspace.willSleepNotification, "sleep", true), (NSWorkspace.didWakeNotification, "sleep", false),
                                     (NSWorkspace.screensDidSleepNotification, "screen", true), (NSWorkspace.screensDidWakeNotification, "screen", false)] {
            observe(center, name) { [weak self] _ in
                if away { Perf.log.info("wake sleep") }
                MainActor.assumeIsolated {
                    self?.setAway(reason, away)
                    if away { self?.readSwitchOnlyBrowsers() }
                }
            }
        }
        for (name, away) in [("com.apple.screenIsLocked", true), ("com.apple.screenIsUnlocked", false)] {
            observe(DistributedNotificationCenter.default(), Notification.Name(name)) { [weak self] _ in
                if away { Perf.log.info("wake lock") }
                MainActor.assumeIsolated {
                    self?.setAway("lock", away)
                    if away { self?.readSwitchOnlyBrowsers() }
                }
            }
        }
        for browser in Browser.allCases where status[browser] != .notInstalled { startWatching(browser) }
        judge = makeJudge()
        backfillHistory()
        refreshVisitDays()
        frontAppChanged(bundleID: NSWorkspace.shared.frontmostApplication?.bundleIdentifier)   // rings the front browser
        for browser in Browser.allCases where browser != judge.frontBrowser { ring(browser) }
    }

    /// Hands the list over: stops watching, ignores reads still in flight, writes the list now and never writes it again.
    /// The agent calls this when the app opens; the engine is then dropped and a new one loads the list when the agent resumes.
    func stop() throws {
        guard started, !stopped else { return }
        stopped = true
        observers.forEach { $0.center.removeObserver($0.token) }
        observers = []
        bells.values.forEach { $0.stop() }
        bells = [:]
        switchOnly = []
        for browser in Browser.allCases { generation[browser, default: 0] += 1 }
        try write()
    }

    /// Old pages take the current rules (saved by the next change): merged copies, no links with a secret, kinds as the rules
    /// say now. Returns how many pages with a secret went away. Logs counts only.
    private static func tidy(_ library: Library, dropsSecretLinks: Bool = true) -> Int {
        let merged = library.mergeDuplicates()
        let secrets = dropsSecretLinks ? library.dropSecretLinks() : 0
        let r = library.refreshKinds()
        let places = library.refreshPlaces()
        Perf.log.info("refreshPlaces backToList=\(places, privacy: .public) regulars=\(library.data.items.filter(\.isActiveRegular).count, privacy: .public) done=\(library.data.items.filter { $0.status == .done }.count, privacy: .public) items=\(library.data.items.count, privacy: .public)")
        Perf.log.info("dropSecretLinks removed=\(secrets, privacy: .public)")
        Perf.log.info("mergeDuplicates removed=\(merged, privacy: .public) refreshKinds items=\(r.items, privacy: .public) changed=\(r.changed, privacy: .public) toTools=\(r.toTools, privacy: .public)")
        return secrets
    }

    /// The window app held the list read-only because the helper didn't hand it over, and now holds the engine lock: load
    /// the list as it is now (recovering it if need be) and take over. The caller starts watching.
    func becomeWritable() {
        guard !canEdit, let candidate else { return }
        let loaded = candidate.load()
        store = loaded.writable ? candidate : nil
        loadNotice = loaded.notice
        library.replace(with: loaded.data)
        secretsDropped = Self.tidy(library)
        addSecretsNotice()
        canEdit = true
        noticeGeneration += 1
    }

    static func secretsNotice(removed count: Int) -> String? {
        count > 0 ? "Later removed \(count) saved \(count == 1 ? "page" : "pages") whose address carried a one-time secret." : nil
    }

    private func addSecretsNotice() {
        guard let text = Self.secretsNotice(removed: secretsDropped) else { return }
        loadNotice = [loadNotice, text].compactMap { $0 }.joined(separator: " ")
    }

    /// Shows what the background helper reported while the window was closed, over the startup notice.
    func showBackgroundProblem(_ text: String) {
        backgroundNotice = text
        noticeGeneration += 1
    }

    /// Once, as Later starts: when the history says saved pages were opened, for those it doesn't say yet and wasn't already
    /// asked about at an earlier launch.
    func backfillHistory() { lookUpHistory(library.linksMissingHistory(), remembersMisses: true) }

    /// Asks the history which days of the last 28 each site was visited, off the main thread: at launch, and again on the
    /// first read after the local date changes. Then pages saved before Regulars that are places on regular sites move
    /// there, once. Nothing is read in between, and logs are counts and time only.
    func refreshVisitDays(now: Date = .now) {
        let today = Familiarity.dayNumber(of: now)
        guard visitDaysAsOf != today, !Sample.enabled else { return }
        visitDaysAsOf = today
        let calendar = Calendar.current
        let since = calendar.date(byAdding: .day, value: -Familiarity.window, to: calendar.startOfDay(for: now)) ?? now
        let ask = io.visitDays
        historyLookups += 1
        Task {
            let start = ContinuousClock.now
            let days = await Task.detached(priority: .utility) { ask(since) }.value
            library.setHistoryDays(days)
            let ms = Perf.ms(since: start)
            Perf.log.info("visitDays sites=\(days.count, privacy: .public) regularSites=\(self.library.regularSiteCount(today: today), privacy: .public) \(ms, format: .fixed(precision: 1), privacy: .public)ms")
            let r = library.migrateRegulars(today: today)
            if r.of > 0 { Perf.log.info("regulars migrated=\(r.migrated, privacy: .public) of \(r.of, privacy: .public)") }
            historyLookups -= 1
        }
    }

    /// Asks the history about these links off the main thread, then records the answers. Only ever for pages that are new
    /// or still unknown, never for a page on every read. Logs counts and time only.
    private func lookUpHistory(_ links: [String], remembersMisses: Bool = false) {
        guard !links.isEmpty, !Sample.enabled else { return }
        let history = io.history
        historyLookups += 1
        Task {
            let start = ContinuousClock.now
            let results = await Task.detached(priority: .utility) { history(links) }.value
            let changed = library.applyHistory(results)
            // Only the launch backfill remembers a miss: a page saved moments ago may not be in the history file yet.
            if remembersMisses { library.markHistoryChecked(links) }
            historyLookups -= 1
            Perf.log.info("history asked=\(links.count, privacy: .public) found=\(results.count, privacy: .public) changed=\(changed, privacy: .public) \(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)ms")
        }
    }

    // MARK: Actions

    func setPaused(_ value: Bool) {
        paused = value
        if !Sample.enabled { UserDefaults.standard.set(value, forKey: "paused") }
        for browser in Browser.allCases { generation[browser, default: 0] += 1 }
        guard !value else { return }
        // Start judging afresh: the judge's last view of the screen is stale, and paused time isn't reading time.
        judge = makeJudge()
        publishCopies()
        frontAppChanged(bundleID: NSWorkspace.shared.frontmostApplication?.bundleIdentifier)
        for browser in Browser.allCases where browser != judge.frontBrowser { ring(browser) }
    }

    func setSavesFeedHomes(_ value: Bool) {
        guard value != savesFeedHomes else { return }
        savesFeedHomes = value
        if !Sample.enabled { UserDefaults.standard.set(value, forKey: Self.savesFeedHomesKey) }
        judge.savesFeedHomes = value   // judges every open tab again at the next read
        for browser in watchedBrowsers { ring(browser) }
    }

    /// Opening Later's window is a moment to look at the browsers that can't tell Later when their tabs change.
    func readSwitchOnlyBrowsers() {
        for browser in switchOnly where io.isRunning(browser) { ring(browser) }
    }

    private var watchedBrowsers: Set<Browser> { Set(bells.keys).union(switchOnly) }

    private func makeJudge() -> Judge {
        let judge = Judge(library: library, watched: watchedBrowsers, savesFeedHomes: savesFeedHomes)
        if !away.isEmpty { judge.setAway(true, now: .now) }
        return judge
    }

    /// The reasons nobody is looking: "sleep", "screen" (display asleep) and "lock". Away until all have ended.
    @ObservationIgnored private var away: Set<String> = []

    private func setAway(_ reason: String, _ on: Bool) {
        if on { away.insert(reason) } else { away.remove(reason) }
        judge.setAway(!away.isEmpty, now: .now)
    }

    func browserTerminated(_ browser: Browser) {
        guard watchedBrowsers.contains(browser), !io.isRunning(browser) else { return }
        generation[browser, default: 0] += 1
        if !paused { judge.browserQuit(browser, now: .now) }   // resuming makes a fresh judge anyway
        publishCopies()
        setStatus(browser, .notRunning)
    }

    func requestPermission(_ browser: Browser) async {
        switch await BrowserReader.requestPermission(browser) {
        case nil: ring(browser)
        case .notRunning: setStatus(browser, .notRunning)
        default: setStatus(browser, .needsPermission)
        }
    }

    func open(_ item: Item) { Opener.open(item.url, preferred: lastBrowser) }
    func open(_ item: Item, in browser: Browser) { Opener.open(item.url, in: browser) }

    /// Writes the list now, waiting for it to finish. Called when the app quits; throws if the write failed.
    /// Without a writable store it does nothing: the notice already says saves won't be kept.
    func flush() throws {
        guard !stopped else { return }
        try write()
    }

    private func write() throws {
        guard let store else { return }
        let data = library.data
        do { try writer.sync { try store.save(data) } } catch {
            saveFinished(ok: false)
            throw error
        }
        saveFinished(ok: true)
    }

    // MARK: Doorbell → reader → judge

    func watchSwitchOnly(_ browser: Browser) {
        switchOnly.insert(browser)
        judge.watch(browser)
    }

    private func startWatching(_ browser: Browser) {
        let folder = RealHome.path + "/" + browser.dataFolder
        let bell = Doorbell { [weak self] in self?.ring(browser) }
        if bell.start(folder: folder) {
            bells[browser] = bell
            judge.watch(browser)
        } else if WatchProbe.access(folder: folder) != .missing {
            watchSwitchOnly(browser)   // macOS keeps Later out of its data folder: read it when the user switches
        }
    }

    private func ring(_ browser: Browser) {
        // A browser opened for the first time creates its data folder as it starts; it rings on its launch and whenever
        // The user switches to it, so it's watched from the first ring after its folder appears.
        if started, bells[browser] == nil, !switchOnly.contains(browser), status[browser]?.isUnwatched == true { startWatching(browser) }
        guard !paused, bells[browser] != nil || switchOnly.contains(browser) else { return }
        guard !reading.contains(browser) else {
            readAgain.insert(browser)   // a change arrived mid-read; read once more afterwards
            return
        }
        reading.insert(browser)
        let start = ContinuousClock.now
        let readGeneration = generation[browser, default: 0]
        let switchOnly = switchOnly.contains(browser)
        let io = io
        Task {
            let outcome: Result<Snapshot, Error>
            do {
                // A browser macOS asked about only once (Chrome) is never asked by a read: without the user's yes, or while it
                // isn't open, nothing is sent to it, so no prompt can appear at a random app switch.
                if switchOnly, let error = await io.permission(browser) { throw error }
                outcome = .success(try await io.read(browser))
            } catch { outcome = .failure(error) }
            if !paused, generation[browser, default: 0] == readGeneration {
                finishRead(browser, outcome, readMs: Perf.ms(since: start))
            }
            reading.remove(browser)
            if readAgain.remove(browser) != nil { ring(browser) }   // also how a ring during a stale read gets through
        }
    }

    private func finishRead(_ browser: Browser, _ outcome: Result<Snapshot, Error>, readMs: Double) {
        do {
            let snapshot = try outcome.get()
            let judgeStart = ContinuousClock.now
            refreshVisitDays()
            judge.ingest(snapshot, now: .now)
            lookUpHistory(library.takeAwaitingHistory())
            publishCopies()
            setStatus(browser, .watching)
            Perf.log.info("ring \(browser.rawValue, privacy: .public) read=\(readMs, format: .fixed(precision: 1), privacy: .public)ms judge=\(Perf.ms(since: judgeStart), format: .fixed(precision: 2), privacy: .public)ms tabs=\(snapshot.tabs.count, privacy: .public) skipped=\(snapshot.skippedWindows, privacy: .public) items=\(self.library.data.items.count, privacy: .public)")
        } catch ReadError.notRunning {
            judge.browserQuit(browser, now: .now)   // not running: known to have nothing open
            publishCopies()
            setStatus(browser, .notRunning)
        } catch ReadError.needsPermission {
            unreadable(browser)
            setStatus(browser, .needsPermission)
        } catch ReadError.inconsistent, ReadError.timedOut {
            // Transient: the next ring catches up.
        } catch {
            unreadable(browser)
            setStatus(browser, .readFailed)
            Perf.log.error("ring \(browser.rawValue, privacy: .public) failed: \((error as NSError).domain, privacy: .public) \((error as NSError).code, privacy: .public)")
        }
    }

    /// A switch-only browser Later can't read (no permission yet, or an answer it doesn't understand) is never read, so it
    /// can't be hiding a copy of a page the user deleted: for dismissals it counts as known, with nothing open. A watched
    /// browser that fails to read is retried by its doorbell, so it keeps its place.
    private func unreadable(_ browser: Browser) {
        guard switchOnly.contains(browser) else { return }
        judge.browserUnreadable(browser)
        publishCopies()
    }

    private func publishCopies() {
        let copies = judge.openCopies
        if copies != openCopies { openCopies = copies }
    }

    /// Only a real change is written: every ring repeats the status, and each write, even of the same value, tells
    /// everything showing it to update, the library window kept hidden included.
    private func setStatus(_ browser: Browser, _ value: BrowserStatus) {
        if status[browser] != value { status[browser] = value }
    }

    /// Switching into or out of a browser is the one moment we know to look without waiting for its
    /// next session write (~10–60 s), so the browser being left and the one being entered are read now.
    func frontAppChanged(bundleID: String?) {
        let browser = Browser.matching(bundleID: bundleID)
        if let browser { lastBrowser = browser }
        guard !paused else { return }
        let left = judge.frontBrowser
        judge.setFrontBrowser(browser, now: .now)
        for changed in Set([left, browser].compactMap { $0 }) { ring(changed) }
    }

    // MARK: Saving the list

    private func libraryChanged() {
        icons.libraryChanged()
        guard !saveScheduled, store != nil, !stopped else { return }
        saveScheduled = true
        // One-shot, armed only after a change: merges a burst of changes into one write.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            MainActor.assumeIsolated { self?.saveNow() }
        }
    }

    private func saveNow() {
        saveScheduled = false
        guard let store, !stopped else { return }
        let data = library.data
        writer.async { [weak self] in
            let ok: Bool
            do { try store.save(data); ok = true } catch {
                Perf.log.error("save failed: \((error as NSError).domain, privacy: .public) \((error as NSError).code, privacy: .public)")
                ok = false
            }
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.saveFinished(ok: ok) } }
        }
    }

    private static let saveFailedNotice = "Later couldn't save its list. It'll try again with your next change."

    private func saveFinished(ok: Bool) {
        if saveFailed == ok {
            saveFailed = !ok
            noticeGeneration += 1
            onSaveFailedChange?(!ok)
        }
    }
}
