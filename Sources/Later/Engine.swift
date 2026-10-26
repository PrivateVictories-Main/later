import AppKit
import LaterCore
import Observation

enum BrowserStatus: Equatable {
    case notInstalled, notRunning, watching, needsPermission, readFailed

    /// Statuses the window warns about: ones the user can fix, or real breakage.
    var isProblem: Bool { self == .needsPermission || self == .readFailed }

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

/// What the engine asks of the outside world about a browser, so tests can stand in for it.
struct BrowserIO {
    var isRunning: (Browser) -> Bool = { BrowserReader.mainInstance(of: $0) != nil }
    /// Nil: allowed. Never shows macOS's prompt.
    var permission: (Browser) async -> ReadError? = { await BrowserReader.checkPermission($0) }
    var read: (Browser) async throws -> Snapshot = { try await BrowserReader.read($0) }
    /// When Helium's history says each link was first opened, and how often. Reads the History files, read-only.
    var history: @Sendable ([String]) -> [String: HistoryLookup.Result] = { links in
        HistoryLookup.lookup(links, in: HistoryLookup.databases(in: RealHome.path + "/" + Browser.helium.dataFolder))
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
    var notice: String? { saveFailed ? Self.saveFailedNotice : loadNotice }
    private let loadNotice: String?
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

    private let store: LibraryStore?
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
    @ObservationIgnored private var historyLookups = 0

    init() {
        io = BrowserIO()
        let feeds = !Sample.enabled && UserDefaults.standard.bool(forKey: Self.savesFeedHomesKey)
        savesFeedHomes = feeds
        #if DEBUG
        let sample = Sample.data()
        #else
        let sample: LibraryData? = nil
        #endif
        let store = sample == nil ? try? LibraryStore.appDefault() : nil   // a sample list is never saved
        let loaded = sample.map { LibraryStore.Loaded(data: $0, notice: nil) }
            ?? store?.load() ?? LibraryStore.Loaded(data: LibraryData(), notice: nil)
        // A damaged file that couldn't be set aside blocks saving, so it can never be overwritten.
        self.store = loaded.writable ? store : nil
        library = Library(data: loaded.data)
        if sample == nil {   // old pages take the current rules; saved by the next change
            let merged = library.mergeDuplicates()
            let r = library.refreshKinds()
            Perf.log.info("mergeDuplicates removed=\(merged, privacy: .public) refreshKinds items=\(r.items, privacy: .public) changed=\(r.changed, privacy: .public) toTools=\(r.toTools, privacy: .public)")
        }
        // A sample list's icons stay in memory, like the list itself.
        icons = SiteIcons(library: library, folder: sample == nil ? SiteIcons.folder : nil)
        judge = Judge(library: library, watched: [], savesFeedHomes: feeds)   // replaced in start(), once the watched browsers are known
        loadNotice = store == nil && sample == nil ? "Later can't open its storage folder, so saves won't be kept." : loaded.notice
        paused = !Sample.enabled && UserDefaults.standard.bool(forKey: "paused")
        for browser in Browser.allCases {
            // Known before setup too. One that has never been opened has no data folder yet; it's watched once it has
            // (see ring). Until it's read, it counts as not open.
            status[browser] = browser.isInstalled ? .notRunning : .notInstalled
        }
        #if DEBUG
        if sample != nil {
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
    init(testing library: Library, io: BrowserIO = BrowserIO(isRunning: { _ in false }, permission: { _ in .notRunning },
                                                             read: { _ in throw ReadError.notRunning })) {
        self.library = library
        self.io = io
        icons = SiteIcons(library: library, folder: nil)
        judge = Judge(library: library, watched: [])
        loadNotice = nil
        paused = false
        savesFeedHomes = false
        store = nil
    }

    /// Tests only: waits for the reads in flight.
    func settle() async {
        for _ in 0..<500 where !reading.isEmpty || !readAgain.isEmpty || historyLookups > 0 { await Task.yield() }
    }
    #endif

    func start() {
        guard !started, !Sample.enabled else { return }   // a sample list watches no browser
        started = true
        // Watching is the whole job: macOS must not quietly quit Later once its last window closes.
        ProcessInfo.processInfo.disableAutomaticTermination("Later watches your open tabs")
        library.prune(now: .now)
        let center = NSWorkspace.shared.notificationCenter
        center.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated { self?.frontAppChanged(bundleID: id) }
        }
        center.addObserver(forName: NSWorkspace.didLaunchApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated {
                guard let self, let browser = Browser.matching(bundleID: id) else { return }
                if !self.paused { self.judge.browserLaunched(browser) }
                self.ring(browser)
            }
        }
        center.addObserver(forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated {
                guard let self, let browser = Browser.matching(bundleID: id) else { return }
                self.browserTerminated(browser)
            }
        }
        // A browser Later can't watch for changes is read as the Mac goes to sleep or locks, so what was open then is
        // known: the user may be away long enough for their tabs to have been waiting.
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.screensDidSleepNotification] {
            center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.readSwitchOnlyBrowsers() }
            }
        }
        DistributedNotificationCenter.default().addObserver(forName: Notification.Name("com.apple.screenIsLocked"), object: nil,
                                                            queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.readSwitchOnlyBrowsers() }
        }
        for browser in Browser.allCases where status[browser] != .notInstalled { startWatching(browser) }
        judge = makeJudge()
        backfillHistory()
        frontAppChanged(bundleID: NSWorkspace.shared.frontmostApplication?.bundleIdentifier)   // rings the front browser
        for browser in Browser.allCases where browser != judge.frontBrowser { ring(browser) }
    }

    /// Once, as Later starts: when the history says saved pages were opened, for those it doesn't say yet and wasn't already
    /// asked about at an earlier launch.
    func backfillHistory() { lookUpHistory(library.linksMissingHistory(), remembersMisses: true) }

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

    private func makeJudge() -> Judge { Judge(library: library, watched: watchedBrowsers, savesFeedHomes: savesFeedHomes) }

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
        if started, bells[browser] == nil, !switchOnly.contains(browser), status[browser] == .notRunning { startWatching(browser) }
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
            Perf.log.error("ring \(browser.rawValue, privacy: .public) failed: \(String(describing: error), privacy: .public)")
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
        guard !saveScheduled, store != nil else { return }
        saveScheduled = true
        // One-shot, armed only after a change: merges a burst of changes into one write.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            MainActor.assumeIsolated { self?.saveNow() }
        }
    }

    private func saveNow() {
        saveScheduled = false
        guard let store else { return }
        let data = library.data
        writer.async { [weak self] in
            let ok: Bool
            do { try store.save(data); ok = true } catch {
                Perf.log.error("save failed: \(String(describing: error), privacy: .public)")
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
        }
    }
}
