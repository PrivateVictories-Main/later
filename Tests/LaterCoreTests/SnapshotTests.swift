import Testing
@testable import LaterCore

/// Records which windows had their tabs requested.
private final class Calls { var tabs: [Int] = [] }

/// Two windows, normal 1 and incognito 2, kept in front-to-back order. Reporting the ids swaps them,
/// as a click between two reads would, so anything addressed by position would hit the other window.
private final class FlippingBrowser {
    private var windows: [(id: Int, mode: String, tabs: WindowTabs)]
    private(set) var flipped = false
    private(set) var modeCalls: [(id: Int, mode: String)] = []
    private(set) var tabCalls: [Int] = []

    init(front: Int) {
        let normal = (id: 1, mode: "normal", tabs: WindowTabs(activeIndex: 1, urls: ["https://n.com/"], titles: ["N"]))
        let secret = (id: 2, mode: "incognito", tabs: WindowTabs(activeIndex: 1, urls: ["https://secret.example/"], titles: ["S"]))
        windows = front == 1 ? [normal, secret] : [secret, normal]
    }

    func windowIDs() -> [Int] {
        defer { windows.reverse(); flipped = true }
        return windows.map(\.id)
    }

    func mode(_ id: Int) -> String {
        let mode = windows.first { $0.id == id }!.mode
        modeCalls.append((id, mode))
        return mode
    }

    func tabs(_ id: Int) -> WindowTabs {
        tabCalls.append(id)
        return windows.first { $0.id == id }!.tabs
    }
}

@Suite struct SnapshotTests {
    private func collect(_ ids: [Int], modes: [Int: String], tabs: [Int: WindowTabs],
                         calls: Calls = Calls()) throws -> Snapshot {
        try Snapshot.collect(browser: .helium, windowIDs: ids,
                             mode: { modes[$0]! },
                             tabs: { calls.tabs.append($0); return tabs[$0]! })
    }

    @Test func collectsWindowsFrontFirstWithActiveFlags() throws {
        let s = try collect([7, 3], modes: [7: "normal", 3: "normal"], tabs: [
            7: WindowTabs(activeIndex: 2, urls: ["https://a.com/1", "https://a.com/2"], titles: ["A1", "A2"]),
            3: WindowTabs(activeIndex: 1, urls: ["https://b.com/"], titles: ["B"]),
        ])
        #expect(s.tabs == [
            Tab(url: "https://a.com/1", title: "A1", isActive: false, windowIndex: 0, tabIndex: 0, windowID: 7),
            Tab(url: "https://a.com/2", title: "A2", isActive: true, windowIndex: 0, tabIndex: 1, windowID: 7),
            Tab(url: "https://b.com/", title: "B", isActive: true, windowIndex: 1, tabIndex: 0, windowID: 3),
        ])
    }

    @Test func neverRequestsIncognitoTabsButKeepsRealWindowIndexes() throws {
        let calls = Calls()
        let s = try collect([5, 9], modes: [5: "incognito", 9: "normal"], tabs: [
            5: WindowTabs(activeIndex: 1, urls: ["https://secret.example/"], titles: ["S"]),
            9: WindowTabs(activeIndex: 1, urls: ["https://b.com/x"], titles: ["B"]),
        ], calls: calls)
        #expect(s.tabs == [Tab(url: "https://b.com/x", title: "B", isActive: true, windowIndex: 1, tabIndex: 0, windowID: 9)])
        #expect(calls.tabs == [9])
    }

    /// The reviewer's case: the windows swap order after their ids are read but before each window is.
    /// Modes and tabs are looked up by id, so the private window's tabs are never requested,
    /// and each mode answer belongs to the window it was asked about.
    @Test func reorderedWindowsNeverLeakPrivateTabs() throws {
        for front in [1, 2] {
            let browser = FlippingBrowser(front: front)
            let ids = browser.windowIDs()
            let s = try Snapshot.collect(browser: .helium, windowIDs: ids, mode: browser.mode, tabs: browser.tabs)
            #expect(browser.flipped)
            #expect(s.tabs.map(\.url) == ["https://n.com/"])
            #expect(s.tabs.map(\.windowIndex) == [ids.firstIndex(of: 1)!])
            #expect(browser.tabCalls == [1])
            #expect(browser.modeCalls.map(\.id) == ids)
            #expect(browser.modeCalls.allSatisfy { $0.mode == (($0.id == 1) ? "normal" : "incognito") })
        }
    }

    @Test func mismatchedCountsAreRejected() {
        #expect(throws: Snapshot.AssembleError.inconsistent) {
            try collect([1], modes: [1: "normal"],
                        tabs: [1: WindowTabs(activeIndex: 1, urls: ["https://a.com/"], titles: [])])
        }
    }

    @Test(arguments: ["", "guest"]) func unrecognizedModeWindowIsSkippedAndCounted(mode: String) throws {
        let calls = Calls()
        let s = try collect([1, 2, 3], modes: [1: "normal", 2: mode, 3: "incognito"], tabs: [
            1: WindowTabs(activeIndex: 1, urls: ["https://a.com/"], titles: ["A"]),
            2: WindowTabs(activeIndex: 1, urls: ["https://g.com/"], titles: ["G"]),
        ], calls: calls)
        #expect(s.tabs == [Tab(url: "https://a.com/", title: "A", isActive: true, windowIndex: 0, tabIndex: 0, windowID: 1)])
        #expect(calls.tabs == [1])
        #expect(s.skippedWindows == 1)   // incognito isn't counted
    }

    @Test func allWindowsUnrecognizedFailsTheRead() {
        let calls = Calls()
        #expect(throws: Snapshot.AssembleError.unrecognizedMode) {
            try collect([1, 2], modes: [1: "guest", 2: ""], tabs: [:], calls: calls)
        }
        #expect(calls.tabs.isEmpty)
    }

    @Test func noWindowsIsAnEmptyRead() throws {
        #expect(try collect([], modes: [:], tabs: [:]).tabs.isEmpty)
    }
}
