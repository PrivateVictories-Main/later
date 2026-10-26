import Foundation
import Observation
import os
import Testing
@testable import Later
@testable import LaterCore

/// The library window's model across a close: the panes keep what they last drew while the window is hidden, and
/// opening it redraws them only when that's out of date.
@Suite @MainActor struct LibraryModelTests {
    /// Noon in the Mac's own calendar, which the list's headings go by.
    private let noon = Calendar.current.date(from: DateComponents(year: 2027, month: 6, day: 16, hour: 12))!
    private var tomorrow: Date { Calendar.current.date(byAdding: .day, value: 1, to: noon)! }

    /// Two pages saved this morning, shown at noon, then the window closed.
    private func closedWindow() -> (LibraryModel, Library) {
        let library = Library()
        for (host, kind) in [("rei.com", Kind.shopping), ("theatlantic.com", .reading)] {
            library.save(url: "https://\(host)/page", title: host, verdict: Verdict(isSpecific: true, kind: kind, pageType: host),
                         browser: .helium, now: noon.addingTimeInterval(-2 * 3_600))
        }
        let model = LibraryModel(engine: Engine(testing: library), undo: UndoManager())
        model.now = noon
        model.wake(at: noon)
        _ = (model.listing, model.counts)   // the panes draw
        model.isVisible = false             // as LibraryWindow.hide() leaves it
        return (model, library)
    }

    /// Reads what the panes read, as their bodies do while the window is hidden; the result says whether anything since
    /// would have redrawn them.
    private func draw(_ model: LibraryModel) -> () -> Bool {
        let redrawn = OSAllocatedUnfairLock(initialState: false)
        withObservationTracking { _ = (model.listing, model.counts) } onChange: { redrawn.withLock { $0 = true } }
        return { redrawn.withLock { $0 } }
    }

    private func id(_ library: Library, _ host: String) throws -> UUID {
        try #require(library.data.items.first { $0.host == host }).id
    }

    @Test func reopeningWithNothingChangedRedrawsNothing() {
        let (model, _) = closedWindow()
        let redrawn = draw(model)
        model.wake(at: noon.addingTimeInterval(3_600))
        #expect(!redrawn())
    }

    @Test func reopeningOnANewDayRedrawsTheHeadings() throws {
        let (model, _) = closedWindow()
        try #require(model.effectiveGrouping == .date)
        #expect(model.listing.groups.map(\.title) == ["Today"])
        model.now = Calendar.current.startOfDay(for: tomorrow)   // midnight passes, as LibraryWindow's observer sees it
        let redrawn = draw(model)
        #expect(model.listing.groups.map(\.title) == ["Today"])   // still what was drawn, while hidden
        model.wake(at: tomorrow.addingTimeInterval(-3 * 3_600))
        #expect(redrawn())
        #expect(model.listing.groups.map(\.title) == ["Yesterday"])
        #expect(model.listing.summary.newToday == 0)
    }

    @Test func aPageSavedWhileClosedShowsOnReopening() {
        let (model, library) = closedWindow()
        library.save(url: "https://youtube.com/watch", title: "A video", verdict: Verdict(isSpecific: true, kind: .videos,
                     pageType: "youtube.com"), browser: .helium, now: noon.addingTimeInterval(600))
        let redrawn = draw(model)
        model.wake(at: noon.addingTimeInterval(3_600))
        #expect(redrawn())
        #expect(model.listing.items.count == 3)
        #expect(model.counts[.kind(.videos)] == 1)
    }

    @Test func aPageMarkedDoneWhileClosedIsGoneOnReopening() throws {
        let (model, library) = closedWindow()
        library.setDone(try id(library, "rei.com"), true, now: noon.addingTimeInterval(600))
        let redrawn = draw(model)
        model.wake(at: noon.addingTimeInterval(3_600))
        #expect(redrawn())
        #expect(model.listing.items.map(\.host) == ["theatlantic.com"])
        #expect(model.counts[.done] == 1)
    }

    @Test func aPageMovedToAnotherKindWhileClosedIsCountedThereOnReopening() throws {
        let (model, library) = closedWindow()
        library.move(try id(library, "rei.com"), to: .videos)
        let redrawn = draw(model)
        model.wake(at: noon.addingTimeInterval(3_600))
        #expect(redrawn())
        #expect(model.counts[.kind(.videos)] == 1)
        #expect(model.counts[.kind(.shopping)] == nil)
    }

    @Test func aSectionSearchOrFoldChosenWhileClosedShowsOnReopening() {
        let changes: [(LibraryModel) -> Void] = [
            { $0.section = .kind(.reading) },
            { $0.searchChanged("rei") },
            { $0.collapsed.insert($0.foldKey($0.listing.groups[0])) },
        ]
        let expected: [[String]] = [["theatlantic.com"], ["rei.com"], []]
        for (change, hosts) in zip(changes, expected) {
            let (model, _) = closedWindow()
            change(model)
            let redrawn = draw(model)
            model.wake(at: noon.addingTimeInterval(3_600))
            #expect(redrawn())
            let rows = Set(model.listing.rowIDs)
            #expect(model.listing.items.filter { rows.contains($0.id) }.map(\.host).sorted() == hosts)
        }
    }

    // MARK: Sorting

    private func fresh(_ model: LibraryModel) -> LibraryModel {
        model.keepsChoices = false   // a test never writes the app's settings
        return model
    }

    @Test func theSortIsPartOfWhatTheListIsCachedBy() {
        let (model, _) = closedWindow()
        fresh(model).isVisible = true
        _ = model.listing   // drawn under Last Seen
        model.sort = .site
        #expect(model.listing.items.map(\.host) == ["rei.com", "theatlantic.com"])
        model.sortReversed = true
        #expect(model.listing.items.map(\.host) == ["theatlantic.com", "rei.com"])
        model.sort = .title
        model.sortReversed = false
        #expect(model.listing.items.map(\.title) == ["rei.com", "theatlantic.com"])
    }

    @Test func aSortChosenWhileClosedShowsOnReopening() {
        let (model, _) = closedWindow()
        fresh(model).sort = .site
        model.sortReversed = true
        let redrawn = draw(model)
        model.wake(at: noon.addingTimeInterval(3_600))
        #expect(redrawn())
        #expect(model.listing.items.map(\.host) == ["theatlantic.com", "rei.com"])
    }

    @Test func reopeningWithTheSameSortRedrawsNothing() {
        let (model, _) = closedWindow()
        fresh(model).sort = .title
        model.wake(at: noon.addingTimeInterval(1_800))   // draws the new sort
        _ = (model.listing, model.counts)
        model.isVisible = false
        let redrawn = draw(model)
        model.wake(at: noon.addingTimeInterval(3_600))
        #expect(!redrawn())
    }

    @Test func openPagesAreCountedBySortingAndTabOrderListsThemFirst() async throws {
        let fake = FakeBrowsers()
        let page = Tab(url: "https://www.theatlantic.com/page", title: "theatlantic.com", isActive: true, windowIndex: 0, tabIndex: 2, windowID: 5)
        fake.running = [.helium]
        fake.tabs[.helium] = [page, Tab(url: "https://theatlantic.com/page/", title: "theatlantic.com", isActive: false, windowIndex: 0, tabIndex: 3, windowID: 5)]
        let library = Library()
        for host in ["rei.com", "theatlantic.com"] {
            library.save(url: "https://\(host)/page", title: host, verdict: Verdict(isSpecific: true, kind: .reading, pageType: host),
                         browser: .helium, now: noon.addingTimeInterval(-3_600))
        }
        let engine = Engine(testing: library, io: fake.io)
        let model = fresh(LibraryModel(engine: engine, undo: UndoManager()))
        model.now = noon
        model.wake(at: noon)
        engine.frontAppChanged(bundleID: "net.imput.helium")
        engine.watchSwitchOnly(.helium)
        engine.frontAppChanged(bundleID: "com.apple.finder")   // leaving Helium reads it
        await engine.settle()
        let atlantic = try #require(library.data.items.first { $0.host == "theatlantic.com" })
        #expect(model.listing.copies == [atlantic.id: 2])
        #expect(atlantic.tabIndex == 2 && atlantic.windowRank == 0)
        model.sort = .tabOrder
        #expect(model.listing.groups.map(\.title) == ["Open", "Closed"])
        #expect(model.listing.items.map(\.host) == ["theatlantic.com", "rei.com"])
        #expect(!model.groupingActive)
        #expect(model.foldKey(model.listing.groups[1]) == "tabs|Closed")
    }

    @Test func labelsForVisitsAndOpenCopies() {
        let page = Verdict(isSpecific: true, kind: .reading, pageType: "a")
        let library = Library()
        library.save(url: "https://a.com/1", title: "One", verdict: page, browser: .helium, now: noon)
        var item = library.data.items[0]
        #expect(ItemRow.visitsLabel(item) == nil)
        item.visits = 3
        #expect(ItemRow.visitsLabel(item) == "3 visits")
        item.historyVisits = 40
        #expect(ItemRow.visitsLabel(item) == "40 visits")
        #expect(ItemRow.openLabel(0) == nil && ItemRow.openLabel(1) == nil)
        #expect(ItemRow.openLabel(2) == "2 open")
    }

    @Test func theHistoryIsAskedOnlyAboutHeliumPagesWithoutAnOpeningTime() async throws {
        let fake = FakeBrowsers()
        let library = Library()
        let page = Verdict(isSpecific: true, kind: .reading, pageType: "a")
        for (url, browser) in [("https://a.com/1", Browser.helium), ("https://a.com/2", .helium), ("https://a.com/3", .chrome)] {
            library.save(url: url, title: url, verdict: page, browser: browser, now: noon)
        }
        _ = library.takeAwaitingHistory()
        library.applyHistory(["https://a.com/2": HistoryLookup.Result(firstVisit: noon.addingTimeInterval(-86_400), visitCount: 2)])
        fake.historyAnswers["https://a.com/1"] = HistoryLookup.Result(firstVisit: noon.addingTimeInterval(-9 * 86_400), visitCount: 6)
        let engine = Engine(testing: library, io: fake.io)
        engine.backfillHistory()
        await engine.settle()
        #expect(fake.historyAsked == ["https://a.com/1"])
        #expect(library.data.items[0].openedAt == noon.addingTimeInterval(-9 * 86_400) && library.data.items[0].historyVisits == 6)
        #expect(library.data.items[2].openedAt == nil)   // Chrome: first seen only
        engine.backfillHistory()   // once more: nothing is asked about a page that has its answer
        await engine.settle()
        #expect(fake.historyAsked == ["https://a.com/1"])
    }
}
