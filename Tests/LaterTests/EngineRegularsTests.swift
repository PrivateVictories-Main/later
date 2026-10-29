import Foundation
import Testing
@testable import Later
@testable import LaterCore

/// Once a day the engine asks the browser's history which days each site was visited, then moves old saved places on
/// regular sites to Regulars.
@Suite @MainActor struct EngineRegularsTests {
    private let noon = Calendar.current.date(from: DateComponents(year: 2027, month: 6, day: 16, hour: 12))!
    private var today: Int { Familiarity.dayNumber(of: noon) }
    private let fake = FakeBrowsers()

    private func library() -> Library {
        let library = Library()
        let oldPlace = Verdict(isSpecific: true, kind: .tools, pageType: "dash.example.com/~")   // saved before Regulars: no flag
        library.save(url: "https://dash.example.com/", title: "Dash", verdict: oldPlace, browser: .helium, now: noon)
        return library
    }

    @Test func theDaysAreAskedOnceAWindowBackAndOldPlacesOnRegularSitesMove() async {
        let library = library()
        fake.visitDayAnswers = ["dash.example.com": Set((1...5).map { today - $0 })]
        let engine = Engine(testing: library, io: fake.io)
        engine.refreshVisitDays(now: noon)
        await engine.settle()
        #expect(library.data.items[0].regular == true)
        #expect(fake.visitDaysAsked.count == 1)
        let first = Calendar.current.startOfDay(for: noon)
        #expect(fake.visitDaysAsked[0] == Calendar.current.date(byAdding: .day, value: -Familiarity.window, to: first))
        #expect(library.visitDays(site: "dash.example.com", today: today) == 5)
    }

    @Test func itIsNotAskedAgainTheSameDayButIsOnTheNextOne() async throws {
        let engine = Engine(testing: library(), io: fake.io)
        engine.refreshVisitDays(now: noon)
        engine.refreshVisitDays(now: noon.addingTimeInterval(3_600))
        await engine.settle()
        #expect(fake.visitDaysAsked.count == 1)
        engine.refreshVisitDays(now: try #require(Calendar.current.date(byAdding: .day, value: 1, to: noon)))
        await engine.settle()
        #expect(fake.visitDaysAsked.count == 2)
    }

    @Test func withNoDaysToGoByNothingIsMigrated() async {
        let library = library()
        let engine = Engine(testing: library, io: fake.io)
        engine.refreshVisitDays(now: noon)
        await engine.settle()
        #expect(library.data.items[0].regular == nil)   // not marked "kept": it is looked at again when there are days
    }

    @Test func theFirstReadOnANewDayAsksAgain() async {
        let engine = Engine(testing: Library(), io: fake.io)
        fake.running = [.helium]
        fake.tabs[.helium] = [Tab(url: "https://dash.example.com/", title: "Dash", isActive: true, windowIndex: 0)]
        engine.watchSwitchOnly(.helium)
        engine.frontAppChanged(bundleID: "net.imput.helium")
        await engine.settle()
        #expect(fake.visitDaysAsked.count == 1)
        engine.frontAppChanged(bundleID: "com.apple.finder")
        await engine.settle()
        #expect(fake.visitDaysAsked.count == 1)   // the same day: not again
    }
}

@Suite @MainActor struct StandInEngineTests {
    @Test func noTestBuiltEnginePointsAtARealBrowserFolder() {
        let fake = FakeBrowsers()
        let engines = [Engine(testing: Library()), Engine(testing: Library(), io: fake.io), Engine(testing: Library(), readOnly: true)]
        #expect(engines.allSatisfy { $0.historyFolder == nil })
        #expect(BrowserIO().historyFolder == nil)
    }

    @Test func onlyTheRealIOReadsHeliumsFolder() {
        #expect(BrowserIO.real.historyFolder == RealHome.path + "/" + Browser.helium.dataFolder)
    }

    @Test func aStandInAnswersNothingAboutAnyonesHistory() async {
        let io = BrowserIO()
        #expect(io.history(["https://example.com/"]).isEmpty)
        #expect(io.visitDays(Date()).isEmpty)
        #expect(!io.isRunning(.helium) && !io.isRunning(.chrome))
        #expect(await io.permission(.helium) == .notRunning)
        await #expect(throws: ReadError.self) { try await io.read(.helium) }
    }
}

@Suite struct VisitDaysCacheTests {
    private let day1 = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func theHistoryIsAskedOncePerDayHoweverManyEnginesAsk() {
        let cache = VisitDaysCache()
        var asked = 0
        let ask: (Date) -> [String: Set<Int>] = { _ in asked += 1; return ["a.com": [1, 2]] }
        #expect(cache.days(since: day1, ask: ask) == ["a.com": [1, 2]])
        #expect(cache.days(since: day1, ask: ask) == ["a.com": [1, 2]])
        #expect(asked == 1)
        _ = cache.days(since: day1.addingTimeInterval(86_400), ask: ask)
        #expect(asked == 2)
    }

    @Test func anEmptyAnswerIsNotRemembered() {
        let cache = VisitDaysCache()
        var asked = 0
        let ask: (Date) -> [String: Set<Int>] = { _ in asked += 1; return [:] }
        _ = cache.days(since: day1, ask: ask)
        _ = cache.days(since: day1, ask: ask)
        #expect(asked == 2)
    }
}
