import Foundation
import Testing
@testable import Later
@testable import LaterCore

/// A stand-in for the browsers: which are running, whether Later may control them, and what they hold.
final class FakeBrowsers: @unchecked Sendable {
    var running: Set<Browser> = []
    var permission: ReadError?
    /// Per browser, instead of `permission`.
    var permissionFor: [Browser: ReadError] = [:]
    var tabs: [Browser: [Tab]] = [:]
    private(set) var reads: [Browser] = []
    /// What the browser's history says, by link, and the links it was asked about (never the real history).
    var historyAnswers: [String: HistoryLookup.Result] = [:]
    private(set) var historyAsked: [String] = []

    var io: BrowserIO {
        BrowserIO(isRunning: { self.running.contains($0) },
                  permission: { self.running.contains($0) ? self.permissionFor[$0] ?? self.permission : .notRunning },
                  read: { browser in
                      self.reads.append(browser)
                      return Snapshot(browser: browser, tabs: self.tabs[browser] ?? [])
                  },
                  history: { links in
                      self.historyAsked += links
                      return self.historyAnswers.filter { links.contains($0.key) }
                  })
    }
}

/// Chrome's data folder is closed to Later, so Chrome is read when the user switches, not when it changes.
@Suite @MainActor struct EngineSwitchOnlyTests {
    static let chromeID = "com.google.Chrome"
    let fake = FakeBrowsers()
    let page = Tab(url: "https://shop.example.com/products/1000001", title: "Walnut desk organizer", isActive: true, windowIndex: 0)

    private func engine() -> Engine {
        let engine = Engine(testing: Library(), io: fake.io)
        engine.watchSwitchOnly(.chrome)
        fake.running = [.chrome]
        fake.tabs[.chrome] = [page]
        return engine
    }

    @Test func aSwitchOnlyBrowserIsReadOnSwitchingInAndOut() async {
        let engine = engine()
        engine.frontAppChanged(bundleID: Self.chromeID)
        await engine.settle()
        #expect(fake.reads == [.chrome])
        #expect(engine.status[.chrome] == .watching)
        engine.frontAppChanged(bundleID: "com.apple.finder")
        await engine.settle()
        #expect(fake.reads == [.chrome, .chrome])   // leaving it reads what it holds now
    }

    @Test func switchingBetweenOtherAppsReadsNothing() async {
        let engine = engine()
        engine.frontAppChanged(bundleID: "com.apple.finder")
        engine.frontAppChanged(bundleID: "com.apple.Terminal")
        await engine.settle()
        #expect(fake.reads.isEmpty)
    }

    @Test func nothingIsReadFromABrowserThatIsNotRunning() async {
        let engine = engine()
        fake.running = []
        engine.frontAppChanged(bundleID: Self.chromeID)
        engine.readSwitchOnlyBrowsers()
        await engine.settle()
        #expect(fake.reads.isEmpty)
        #expect(engine.status[.chrome] == .notRunning)
    }

    @Test func withoutPermissionNothingIsReadAndSettingsAsksForIt() async {
        let engine = engine()
        fake.permission = .needsPermission   // undetermined or denied: Later never triggers macOS's prompt by itself
        engine.frontAppChanged(bundleID: Self.chromeID)
        await engine.settle()
        #expect(fake.reads.isEmpty)
        #expect(engine.status[.chrome] == .needsPermission)
        fake.permission = nil   // The user clicked Allow
        engine.frontAppChanged(bundleID: "com.apple.finder")
        await engine.settle()
        #expect(fake.reads == [.chrome])
        #expect(engine.status[.chrome] == .watching)
    }

    /// Chrome running without the user's yes is never read, but it must not stop a page they deleted, closed and reopened from
    /// being saved again: it counts as known, with nothing open.
    @Test func aBrowserWithoutPermissionDoesNotKeepADismissalAlive() async {
        let link = page.url
        var data = LibraryData()
        data.dismissed[Library.hash(LinkCleaner.pageKey(URL(string: link)!))] = .now
        let library = Library(data: data)
        let engine = Engine(testing: library, io: fake.io)
        engine.watchSwitchOnly(.helium)
        engine.watchSwitchOnly(.chrome)
        fake.running = [.helium, .chrome]
        fake.tabs[.helium] = [page]
        fake.permissionFor[.chrome] = .needsPermission
        engine.readSwitchOnlyBrowsers()
        await engine.settle()
        #expect(engine.status[.chrome] == .needsPermission && fake.reads == [.helium])   // Chrome was never read
        #expect(library.isDismissed(link))   // the page is still open in Helium
        fake.tabs[.helium] = []   // its last tab closed
        engine.readSwitchOnlyBrowsers()
        await engine.settle()
        #expect(!library.isDismissed(link))   // so a deliberate reopen counts again
        #expect(!fake.reads.contains(.chrome))
    }

    @Test func openingTheWindowReadsRunningSwitchOnlyBrowsersOnce() async {
        let engine = engine()
        engine.readSwitchOnlyBrowsers()
        await engine.settle()
        #expect(fake.reads == [.chrome])
    }

    @Test func quittingTellsTheJudgeAndDropsTheCopies() async {
        let engine = engine()
        engine.readSwitchOnlyBrowsers()
        await engine.settle()
        #expect(engine.openCopies.values.reduce(0, +) == 1)
        engine.browserTerminated(.chrome)   // still running: another copy of the app is left
        #expect(engine.status[.chrome] == .watching)
        fake.running = []
        engine.browserTerminated(.chrome)
        #expect(engine.status[.chrome] == .notRunning)
        #expect(engine.openCopies.isEmpty)
    }

    @Test func aBrowserNobodyWatchesIsNotTouched() async {
        let engine = Engine(testing: Library(), io: fake.io)
        fake.running = [.chrome]
        engine.frontAppChanged(bundleID: Self.chromeID)
        engine.browserTerminated(.chrome)
        await engine.settle()
        #expect(fake.reads.isEmpty)
    }

    @Test func theNameListReadsAsPlainEnglish() {
        #expect(BrowserStatus.names([.helium]) == "Helium")
        #expect(BrowserStatus.names([.helium, .chrome]) == "Helium and Chrome")
    }
}
