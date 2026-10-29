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

@Suite struct StandInEngineTests {
    @Test func aStandInEngineNeverReadsAnyonesHistory() {
        #expect(BrowserIO().visitDays(Date()).isEmpty)
    }
}
