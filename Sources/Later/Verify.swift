import AppKit
import LaterCore
import os

/// `Later --verify`: checks that each browser can be watched and read, then logs every ring
/// with timings and counts only.
@MainActor
final class Verifier {
    private let log = Logger(subsystem: "com.ryan.later", category: "verify")
    private var bells: [Browser: Doorbell] = [:]

    func start() {
        for browser in Browser.allCases {
            let name = browser.rawValue
            let installed = browser.isInstalled
            let folder = RealHome.path + "/" + browser.dataFolder
            let listable = (try? FileManager.default.contentsOfDirectory(atPath: folder)) != nil
            let bell = Doorbell { [weak self] in self?.ring(browser) }
            let watching = bell.start(folder: folder)
            bells[browser] = bell
            let instances = browser.bundleIDs.flatMap(NSRunningApplication.runningApplications(withBundleIdentifier:)).count
            log.notice("\(name, privacy: .public) installed=\(installed) listable=\(listable) watching=\(watching) instances=\(instances)")
            Task {
                // A browser Later can't watch is switch-only: only Allow in Settings or onboarding may raise its prompt.
                let error = await (watching ? BrowserReader.requestPermission(browser) : BrowserReader.checkPermission(browser))
                self.log.notice("\(name, privacy: .public) permission=\(error.map { "\($0)" } ?? "granted", privacy: .public)")
                self.ring(browser)
            }
        }
    }

    private func ring(_ browser: Browser) {
        let start = ContinuousClock.now
        Task {
            do {
                let snapshot = try await BrowserReader.read(browser)
                let ms = Perf.ms(since: start)
                let active = snapshot.tabs.filter(\.isActive).count
                log.notice("ring \(browser.rawValue, privacy: .public) read=\(ms, format: .fixed(precision: 1), privacy: .public)ms tabs=\(snapshot.tabs.count) activeTabs=\(active)")
            } catch {
                log.notice("ring \(browser.rawValue, privacy: .public) error=\(String(describing: error), privacy: .public)")
            }
        }
    }
}
