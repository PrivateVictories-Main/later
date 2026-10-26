import Foundation
import Testing
@testable import LaterCore

/// When a page was opened (from the browser's history) and where its tab is.
@Suite @MainActor struct OpenedAndPlacesTests {
    let now = Date(timeIntervalSince1970: 1_800_000_000)
    let verdict = Verdict(isSpecific: true, kind: .reading, pageType: "a.com/*")

    private func result(_ days: Double, _ visits: Int) -> HistoryLookup.Result {
        HistoryLookup.Result(firstVisit: now.addingTimeInterval(-days * 86_400), visitCount: visits)
    }

    @Test func historyFillsOpenedAtOnlyWhenItIsMissing() throws {
        let library = Library()
        library.save(url: "https://a.com/1", title: "One", verdict: verdict, browser: .helium, now: now)
        #expect(library.linksMissingHistory() == ["https://a.com/1"])
        #expect(library.applyHistory(["https://a.com/1": result(30, 5)]) == 1)
        let first = try #require(library.data.items.first)
        #expect(first.openedAt == now.addingTimeInterval(-30 * 86_400) && first.historyVisits == 5)
        #expect(library.linksMissingHistory().isEmpty)
        // A later answer never moves the opening time; the visit count follows the history.
        #expect(library.applyHistory(["https://a.com/1": result(2, 9)]) == 1)
        let second = try #require(library.data.items.first)
        #expect(second.openedAt == first.openedAt && second.historyVisits == 9)
        // The same answer again changes nothing, and nothing is announced.
        let revision = library.revision
        #expect(library.applyHistory(["https://a.com/1": result(2, 9)]) == 0)
        #expect(library.revision == revision)
    }

    @Test func answersAreMatchedByPageKeyAndUnknownLinksAreIgnored() {
        let library = Library()
        library.save(url: "https://a.com/1", title: "One", verdict: verdict, browser: .helium, now: now)
        #expect(library.applyHistory(["https://www.a.com/1/": result(3, 2), "https://zzz.example/": result(1, 1)]) == 1)
        #expect(library.data.items[0].historyVisits == 2)
    }

    @Test func chromePagesAreNeverAskedAbout() {
        let library = Library()
        library.save(url: "https://a.com/1", title: "One", verdict: verdict, browser: .chrome, now: now)
        library.save(url: "https://a.com/2", title: "Two", verdict: verdict, browser: .helium, now: now)
        #expect(library.linksMissingHistory() == ["https://a.com/2"])
        #expect(library.takeAwaitingHistory() == ["https://a.com/2"])
        #expect(library.takeAwaitingHistory().isEmpty)   // each new page is asked once
    }

    @Test func totalVisitsAreTheLargerCountAndOpenedFallsBackToFirstSaved() {
        let library = Library()
        library.save(url: "https://a.com/1", title: "One", verdict: verdict, browser: .helium, now: now)
        var item = library.data.items[0]
        #expect(item.opened == item.firstSaved && item.totalVisits == 1)
        item.visits = 3
        item.historyVisits = 12
        item.openedAt = now.addingTimeInterval(-86_400)
        #expect(item.opened == item.openedAt && item.totalVisits == 12)
        item.historyVisits = 2
        #expect(item.totalVisits == 3)
    }

    @Test func placesAreRecordedAndOnlyARealMoveIsAnnounced() {
        let library = Library()
        library.save(url: "https://a.com/1", title: "One", verdict: verdict, browser: .helium, now: now)
        library.setPlaces([("https://a.com/1", 3, 1), ("https://unsaved.example/", 0, 0)])
        #expect(library.data.items[0].tabIndex == 3 && library.data.items[0].windowRank == 1)
        let revision = library.revision
        library.setPlaces([("https://a.com/1", 3, 1)])
        #expect(library.revision == revision)
        library.setPlaces([("https://a.com/1", 0, 1)])
        #expect(library.data.items[0].tabIndex == 0 && library.revision == revision + 1)
    }

    @Test func aV11FileWithoutTheNewFieldsLoads() throws {
        let json = """
            {"id":"\(UUID().uuidString)","url":"https://a.com/1","title":"One","host":"a.com","kind":"reading","pageType":"a.com/*",
             "browser":"helium","firstSaved":700000000,"lastSeen":700000100,"visits":2,"status":"active"}
            """
        let item = try JSONDecoder().decode(Item.self, from: Data(json.utf8))
        #expect(item.openedAt == nil && item.historyVisits == nil && item.tabIndex == nil && item.windowRank == nil)
    }

    @Test func mergingKeepsTheEarliestOpeningAndTheLargestHistoryCount() {
        var data = LibraryData()
        let base = Item(id: UUID(), url: "https://a.com/1", title: "One", host: "a.com", kind: .reading, pageType: "a.com/*",
                        browser: .helium, firstSaved: now, lastSeen: now, visits: 1, status: .active, doneAt: nil)
        var www = base, bare = base
        www.url = "https://www.a.com/1"
        www.id = UUID()
        www.openedAt = now.addingTimeInterval(-500)
        www.historyVisits = 4
        bare.openedAt = now.addingTimeInterval(-900)
        bare.historyVisits = 2
        data.items = [www, bare]
        let library = Library(data: data)
        #expect(library.mergeDuplicates() == 1)
        #expect(library.data.items[0].openedAt == now.addingTimeInterval(-900) && library.data.items[0].historyVisits == 4)
    }

    // MARK: Tab places on ingest

    private func place(_ url: String, window: Int, tab: Int, id: Int, active: Bool = false) -> Tab {
        Tab(url: url, title: "Walnut desk organizer", isActive: active, windowIndex: window, tabIndex: tab, windowID: id)
    }

    @Test func ingestRecordsTheTabIndexAndRanksWindowsByAscendingId() throws {
        let h = Harness()
        // Window 40 is in front (index 0) but older ids rank first: 12 is rank 0, 40 is rank 1.
        func read(_ tabs: Tab...) { h.judge.ingest(Snapshot(browser: .helium, tabs: tabs), now: h.now) }
        read(place(shopHome, window: 0, tab: 0, id: 40, active: true), place(productA, window: 1, tab: 3, id: 12))
        h.wait(61)
        read(place(shopHome, window: 0, tab: 0, id: 40, active: true), place(productA, window: 1, tab: 3, id: 12))
        let item = try #require(h.library.data.items.first)
        #expect(item.tabIndex == 3 && item.windowRank == 0)
        // Moved to the second tab of the newer window; with the older window gone, it is the first.
        read(place(shopHome, window: 0, tab: 0, id: 40, active: true), place(productA, window: 0, tab: 1, id: 40))
        #expect(h.library.data.items[0].tabIndex == 1 && h.library.data.items[0].windowRank == 0)
    }

    @Test func aPageOpenTwiceKeepsItsLeftmostTabInTheOldestWindow() throws {
        let h = Harness()
        func read() {
            h.judge.ingest(Snapshot(browser: .helium, tabs: [
                place(shopHome, window: 0, tab: 0, id: 40, active: true), place(productA, window: 0, tab: 1, id: 40),
                place(productA, window: 1, tab: 5, id: 12),
            ]), now: h.now)
        }
        read(); h.wait(61); read()
        let item = try #require(h.library.data.items.first)
        #expect(item.windowRank == 0 && item.tabIndex == 5)   // window 12 is the older, whatever its tab
        #expect(h.judge.openCopies.values.max() == 2)
    }

    // MARK: Window ranks follow the windows' ids, not which window is in front

    @Test func windowRanksStayPutWhenTheFrontWindowChanges() throws {
        let h = Harness()
        /// Two windows with ids 40 and 12; `front` is the one listed first, as the browser lists them by focus.
        func read(front: [String]) throws {
            let snapshot = try Snapshot.collect(browser: .helium, windowIDTexts: front, mode: { _ in "normal" }, tabs: { i in
                front[i] == "40"
                    ? WindowTabs(activeIndex: 1, urls: [shopHome], titles: ["Shop"])
                    : WindowTabs(activeIndex: 0, urls: [productA], titles: ["Walnut desk organizer"])
            })
            h.judge.ingest(snapshot, now: h.now)
        }
        try read(front: ["40", "12"]); h.wait(61); try read(front: ["40", "12"])
        let item = try #require(h.library.data.items.first)
        #expect(item.windowRank == 0)   // window 12 is the older one
        let revision = h.library.revision
        h.wait(5); try read(front: ["12", "40"])   // The user clicks the other window: the list order flips, nothing else does
        #expect(h.library.data.items[0].windowRank == 0 && h.library.revision == revision)
    }

    @Test func windowIDsAreNumbersWhenTheyParseElseTheirTextOrder() {
        #expect(Snapshot.windowKeys(["40", "12", "7"]) == [40, 12, 7])
        #expect(Snapshot.windowKeys([" 5 "]) == [5])
        #expect(Snapshot.windowKeys(["w-b", "w-a"]) == [1, 0])   // stable whichever is in front
        #expect(Snapshot.windowKeys(["w-a", "w-b"]) == [0, 1])
        #expect(Snapshot.windowKeys(["12", "main"]) == [0, 1])
    }

    // MARK: A page open in both browsers

    @Test func aPageInBothBrowsersTakesItsPlaceFromTheBrowserItWasSavedFrom() throws {
        let h = Harness(watched: [.helium, .chrome])
        func helium(_ tabNumber: Int) { h.judge.ingest(Snapshot(browser: .helium, tabs: [place(shopHome, window: 0, tab: 0, id: 1, active: true),
                                                                                         place(productA, window: 0, tab: tabNumber, id: 1)]), now: h.now) }
        func chrome(_ tabNumber: Int) { h.judge.ingest(Snapshot(browser: .chrome, tabs: [place(productA, window: 0, tab: tabNumber, id: 9)]), now: h.now) }
        helium(1); h.wait(61); helium(1)
        #expect(h.library.data.items.first?.browser == .helium && h.library.data.items.first?.tabIndex == 1)
        chrome(7)   // Chrome's tab is elsewhere; Helium's read still stands
        #expect(h.library.data.items[0].tabIndex == 1)
        helium(2); chrome(7); helium(2); chrome(8)
        #expect(h.library.data.items[0].tabIndex == 2)   // follows Helium, never flip-flops
        h.judge.ingest(Snapshot(browser: .helium, tabs: [place(shopHome, window: 0, tab: 0, id: 1, active: true)]), now: h.now)
        chrome(8)   // Helium's copy closed: Chrome's is the only one
        #expect(h.library.data.items[0].tabIndex == 8)
    }

    // MARK: History backfill

    @Test func aMissIsRememberedSoTheNextLaunchDoesNotAskAgain() {
        let library = Library()
        library.save(url: "https://a.com/1", title: "One", verdict: verdict, browser: .helium, now: now)
        library.save(url: "https://a.com/2", title: "Two", verdict: verdict, browser: .helium, now: now)
        library.applyHistory(["https://a.com/2": result(5, 1)])
        #expect(library.linksMissingHistory() == ["https://a.com/1"])
        library.markHistoryChecked(["https://a.com/1", "https://a.com/2"])
        #expect(library.linksMissingHistory().isEmpty)
        // Persisted: a list loaded from the file still knows.
        let reloaded = Library(data: library.data)
        #expect(reloaded.linksMissingHistory().isEmpty)
        // Only when the item's link is no longer the one asked about is it asked again.
        var data = library.data
        data.items[0].url = "https://a.com/1-moved"
        #expect(Library(data: data).linksMissingHistory() == ["https://a.com/1-moved"])
        let revision = library.revision
        library.markHistoryChecked(["https://a.com/1"])
        #expect(library.revision == revision)   // nothing new to remember: nothing saved
    }
}
