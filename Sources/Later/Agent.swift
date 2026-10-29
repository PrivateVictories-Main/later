import AppKit
import LaterCore
import notify

/// `Later --agent`, started by launchd: the same watching as the window app, with no NSApplication, windows or menu. It runs the
/// engine only while it holds the engine lock. When the app opens it posts the handoff notification: the agent stops its
/// engine, writes the list, lets go of the lock and sleeps in the kernel until the app quits or dies.
/// Logs counts and times only.
@MainActor
final class AgentDelegate {
    private let lock: EngineLock?
    private let store: LibraryStore?
    private let status: AgentStatusFile?
    private let makeEngine: () -> Engine
    private(set) var engine: Engine?
    private var tenure: AgentTenure?
    private var handoffToken: Int32 = 0
    private var terminateSignal: DispatchSourceSignal?

    /// What it works on, for tests to replace. It reports trouble in `status` because it has no window to show it in.
    init(lock: EngineLock? = Handoff.lock(), store: LibraryStore? = try? LibraryStore.appDefault(),
         status: AgentStatusFile? = Handoff.statusFile(), makeEngine: @escaping () -> Engine = { Engine() }) {
        self.lock = lock
        self.store = store
        self.status = status
        self.makeEngine = makeEngine
    }

    func start() {
        guard startTenure() else {
            Perf.log.error("agent: no lock file, exiting")
            exit(0)   // a clean exit, so launchd doesn't start it again in a loop
        }
        notify_register_dispatch(Handoff.notification, &handoffToken, .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.yield() }
        }
        // Turning the agent off in Settings stops it with SIGTERM: write the list first.
        signal(SIGTERM, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        source.setEventHandler { MainActor.assumeIsolated { self.yield(); exit(0) } }
        source.resume()
        terminateSignal = source
        #if DEBUG
        listenForDevCommands()
        #endif
        Perf.log.info("agent ready")
    }

    /// Starts waiting for the engine lock (a thread asleep in the kernel); false when there is no lock file.
    @discardableResult
    func startTenure() -> Bool {
        guard let lock else { return false }
        let tenure = AgentTenure(lock: lock) { [weak self] in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.begin() } }
        }
        self.tenure = tenure
        tenure.start()
        return true
    }

    private func begin() {
        guard let tenure, tenure.isHolding, engine == nil else { return }
        guard let store else {
            report(.cantOpenStorage)
            Perf.log.error("agent idle: the storage folder can't be opened")
            return
        }
        // A damaged or missing list is recovered by the window app, which tells the user. Until it has, the agent touches nothing.
        if store.needsRecovery {
            report(.needsRecovery)
            Perf.log.info("agent idle: the list needs the app to recover it")
            return
        }
        let engine = makeEngine()
        engine.onSaveFailedChange = { [weak self, weak engine] failed in
            if failed { self?.report(.cantSave, count: engine?.library.data.items.count ?? 0) } else { self?.status?.clear() }
        }
        engine.start()
        self.engine = engine
        status?.clear()   // whatever was reported before has been dealt with
        Perf.log.info("agent engine started items=\(engine.library.data.items.count, privacy: .public)")
    }

    /// The app wants the list: stop watching, write it, then let go of the lock. A failed write is reported for the app to show.
    func yield() {
        let start = ContinuousClock.now
        if let engine {
            do {
                try engine.stop()
                status?.clear()
            } catch {
                report(.cantSave, count: engine.library.data.items.count)
                Perf.log.error("agent: final save failed")
            }
            Perf.log.info("agent engine stopped items=\(engine.library.data.items.count, privacy: .public)")
            self.engine = nil
        }
        tenure?.release()
        DispatchQueue.main.async { _ = malloc_zone_pressure_relief(nil, 0) }   // the dropped engine's pages go back now
        Perf.log.info("agent yielded \(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)ms")
    }

    private func report(_ state: AgentStatus.State, count: Int = 0) { status?.write(AgentStatus(state: state, count: count)) }

    #if DEBUG
    private var devToken: Int32 = 0
    /// Development only: `notifyutil -p com.ryan.later.dev.add` saves one made-up page, to show a handoff loses nothing.
    private func listenForDevCommands() {
        notify_register_dispatch((Bundle.main.bundleIdentifier ?? "") + ".add", &devToken, .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.engine?.addDevPage() }
        }
    }
    #endif
}

#if DEBUG
extension Engine {
    func addDevPage() {
        let n = library.data.items.count
        // Well-known hosts, so Helium has icons for some of them and the icon path runs.
        let host = ["github.com", "www.google.com", "en.wikipedia.org", "www.youtube.com"][n % 4]
        library.save(url: "https://\(host)/dev/\(n)", title: "Dev page \(n)",
                     verdict: Verdict(isSpecific: true, kind: .shopping, pageType: "example.com/dev/*"), browser: .chrome, now: .now)
        Perf.log.info("dev page added items=\(self.library.data.items.count, privacy: .public)")
    }
}
#endif
