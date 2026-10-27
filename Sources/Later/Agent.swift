import AppKit
import LaterCore
import notify

/// `Later --agent`, started by launchd: the same watching as the window app, with no windows and no menu. It runs the
/// engine only while it holds the engine lock. When the app opens it posts the handoff notification: the agent stops its
/// engine, writes the list, lets go of the lock and sleeps in the kernel until the app quits or dies.
/// Logs counts and times only.
@MainActor
final class AgentDelegate: NSObject, NSApplicationDelegate {
    private var engine: Engine?
    private var tenure: AgentTenure?
    private var handoffToken: Int32 = 0
    private var terminateSignal: DispatchSourceSignal?

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let lock = Handoff.lock() else {
            Perf.log.error("agent: no lock file, exiting")
            exit(0)   // a clean exit, so launchd doesn't start it again in a loop
        }
        let tenure = AgentTenure(lock: lock) { [weak self] in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.begin() } }
        }
        self.tenure = tenure
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
        tenure.start()
        Perf.log.info("agent ready")
    }

    private func begin() {
        guard let tenure, tenure.isHolding, engine == nil else { return }
        let engine = Engine()
        engine.start()
        self.engine = engine
        Perf.log.info("agent engine started items=\(engine.library.data.items.count, privacy: .public)")
    }

    private func yield() {
        let start = ContinuousClock.now
        if let engine {
            do { try engine.stop() } catch { Perf.log.error("agent: final save failed") }
            Perf.log.info("agent engine stopped items=\(engine.library.data.items.count, privacy: .public)")
            self.engine = nil
        }
        tenure?.release()
        DispatchQueue.main.async { _ = malloc_zone_pressure_relief(nil, 0) }   // the dropped engine's pages go back now
        Perf.log.info("agent yielded \(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)ms")
    }

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
        library.save(url: "https://example.com/dev/\(n)", title: "Dev page \(n)",
                     verdict: Verdict(isSpecific: true, kind: .shopping, pageType: "example.com/dev/*"), browser: .chrome, now: .now)
        Perf.log.info("dev page added items=\(self.library.data.items.count, privacy: .public)")
    }
}
#endif
