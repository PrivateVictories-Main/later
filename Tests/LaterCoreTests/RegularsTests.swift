import Foundation
import Testing
@testable import LaterCore

@Suite @MainActor struct RegularsTests {
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    var today: Int { Familiarity.dayNumber(of: t0) }
    let place = Verdict(isSpecific: true, kind: .tools, pageType: "dash.example.com/~", isPlace: true)
    let thing = Verdict(isSpecific: true, kind: .reading, pageType: "dash.example.com/posts/*")
    let oldPlace = Verdict(isSpecific: true, kind: .tools, pageType: "dash.example.com/~")   // as 1.3 saved it, no flag
    let home = "https://dash.example.com/"

    /// A library where `dash.example.com` was visited on `count` different days before today.
    func library(visitedDays count: Int, observed: Bool = false) -> Library {
        let lib = Library()
        let days = Set((1...max(count, 1)).prefix(count).map { today - $0 })
        if observed { lib.noteSiteDays(["dash.example.com"], days: days) } else { lib.setHistoryDays(["dash.example.com": days]) }
        return lib
    }

    // MARK: Days

    @Test func observedDaysAreStoredHashedAndNeverAsHostnames() throws {
        let lib = Library()
        lib.noteSiteDays(["dash.example.com"], days: [today])
        #expect(lib.data.siteDays.count == 1)
        let json = String(decoding: try JSONEncoder().encode(lib.data), as: UTF8.self)
        #expect(!json.contains("example.com"))
        #expect(lib.data.siteDays.keys.first == Library.hash("dash.example.com"))
    }

    @Test func notingTheSameSiteAndDayAgainChangesNothing() {
        let lib = Library()
        var changes = 0
        lib.onChange = { changes += 1 }
        lib.noteSiteDay("a.com", day: today)
        lib.noteSiteDay("a.com", day: today)
        lib.noteSiteDay("a.com", day: today + 1)
        #expect(changes == 2)
        #expect(lib.data.siteDays[Library.hash("a.com")] == [today, today + 1])
    }

    @Test func historyDaysAndObservedDaysAreMergedPerSite() {
        let lib = Library()
        lib.setHistoryDays(["dash.example.com": [today - 1, today - 2]])
        lib.noteSiteDays(["dash.example.com"], days: [today - 2, today - 3, today])
        #expect(lib.visitDays(site: "dash.example.com", today: today) == 3)   // today never counts; day -2 once
        #expect(lib.visitDays(site: "other.com", today: today) == 0)
    }

    @Test func pruneKeepsOnlyTheLast28DaysOfObservations() {
        let lib = Library()
        lib.noteSiteDays(["a.com"], days: [today - 29, today - 28, today - 1])
        lib.noteSiteDays(["old.com"], days: [today - 40])
        lib.prune(now: t0)
        #expect(lib.data.siteDays == [Library.hash("a.com"): [today - 28, today - 1]])
    }

    @Test func regularSitesAreCountedAcrossBothSourcesWithoutCountingOneTwice() {
        let lib = Library()
        lib.setHistoryDays(["a.com": [today - 1, today - 2, today - 3, today - 4], "b.com": [today - 1]])
        lib.noteSiteDays(["a.com", "c.com"], days: [today - 5, today - 6, today - 7, today - 8])   // a.com is in both
        #expect(lib.regularSiteCount(today: today) == 2)   // a.com and c.com
    }

    @Test func aFileWithoutSiteDaysOrRegularFlagsStillOpens() throws {
        let old = #"{"version":1,"items":[],"corrections":{"a.com/~":{"isSpecific":true}},"seen":{},"dwellSamples":[]}"#
        let data = try JSONDecoder().decode(LibraryData.self, from: Data(old.utf8))
        #expect(data.siteDays.isEmpty)
        #expect(data.corrections["a.com/~"] == Correction(isSpecific: true))
    }

    @Test func flagsRoundTripAndStayOutOfFilesThatNeverUsedThem() throws {
        let lib = library(visitedDays: 5)
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        let json = String(decoding: try JSONEncoder().encode(lib.data), as: UTF8.self)
        #expect(json.contains("\"regular\":true"))
        let again = try JSONDecoder().decode(LibraryData.self, from: Data(json.utf8))
        #expect(again.items.first?.regular == true)
        let plain = Library()
        plain.save(url: home, title: "Dash", verdict: thing, browser: .helium, now: t0)
        #expect(!String(decoding: try JSONEncoder().encode(plain.data), as: UTF8.self).contains("regular"))
    }

    // MARK: Saving

    @Test func aPlaceOnARegularSiteIsSavedAsARegular() {
        let lib = library(visitedDays: 4)
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        #expect(lib.data.items[0].regular == true)
    }

    @Test func aPlaceOnANewSiteIsKeptInTheList() {
        let lib = library(visitedDays: 3)
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        #expect(lib.data.items[0].regular == false)
    }

    @Test func aThingOnARegularSiteIsKeptAndIsNotAPlace() {
        let lib = library(visitedDays: 20)
        lib.save(url: "https://dash.example.com/posts/how-we-found-the-bug", title: "Post", verdict: thing, browser: .helium, now: t0)
        #expect(lib.data.items[0].regular == nil)
    }

    @Test func aCorrectionBeatsFamiliarityWhenSaving() {
        let lib = library(visitedDays: 20)
        lib.setCorrection(Correction(isSpecific: true), for: "dash.example.com/~")
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        #expect(lib.data.items[0].regular == false)
    }

    // MARK: Migration

    @Test func migrationMarksOnlyPlacesOnRegularSitesAndDeletesNothing() {
        let lib = library(visitedDays: 6)
        lib.save(url: home, title: "Dash", verdict: oldPlace, browser: .helium, now: t0)
        lib.save(url: "https://dash.example.com/posts/how-we-found-the-bug", title: "Post", verdict: thing, browser: .helium, now: t0)
        lib.save(url: "https://new.example.org/", title: "New", verdict: thing, browser: .helium, now: t0)
        #expect(lib.data.items.allSatisfy { $0.regular == nil })
        let result = lib.migrateRegulars(today: today)
        #expect(result.of == 2 && result.migrated == 1)   // the post is a thing: not a place
        #expect(lib.data.items.map(\.regular) == [true, nil, false])
        #expect(lib.data.items.count == 3)
        #expect(lib.migrateRegulars(today: today).of == 0)   // each page is looked at once
    }

    @Test func migrationHonorsCorrections() {
        let lib = library(visitedDays: 6)
        lib.setCorrection(Correction(isSpecific: true), for: "dash.example.com/~")
        lib.save(url: home, title: "Dash", verdict: oldPlace, browser: .helium, now: t0)
        let result = lib.migrateRegulars(today: today)
        #expect(result.migrated == 0 && result.of == 1)
        #expect(lib.data.items[0].regular == false)
    }

    @Test func theDaysAreKnownOnlyOnceThereIsEnoughHistoryToGoBy() {
        let lib = Library()
        #expect(!lib.hasVisitDays(today: today))
        lib.noteSiteDay("x.com", day: today)
        #expect(!lib.hasVisitDays(today: today))   // today never counts
        lib.noteSiteDay("x.com", day: today - 1)
        #expect(!lib.hasVisitDays(today: today))   // one past day is not enough to tell a regular from a new site
        lib.noteSiteDays(["y.com"], days: [today - 2, today - 3, today - 4])
        #expect(lib.hasVisitDays(today: today))    // four different past days, over any sites
        let history = Library()
        history.setHistoryDays(["x.com": [today - 1, today - 40]])
        #expect(!history.hasVisitDays(today: today))
        history.setHistoryDays(["x.com": [today - 1, today - 2], "z.com": [today - 3, today - 9]])
        #expect(history.hasVisitDays(today: today))
    }

    @Test(arguments: [[0], [1], [0, 1], [1, 2, 3]])
    func migrationLeavesEveryFlagAloneUntilThereIsEnoughHistory(_ ago: [Int]) {
        let lib = Library()
        lib.noteSiteDays(["dash.example.com"], days: Set(ago.map { today - $0 }))
        lib.save(url: home, title: "Dash", verdict: oldPlace, browser: .chrome, now: t0)
        let result = lib.migrateRegulars(today: today)
        #expect(result.of == 0 && result.migrated == 0)
        #expect(lib.data.items[0].regular == nil)   // nil is shown as a normal page and looked at again later
    }

    @Test func aFailedHistoryReadDoesNotForgetTheLastGoodOne() {
        let lib = Library()
        lib.setHistoryDays(["a.com": [today - 1, today - 2, today - 3, today - 4]])
        lib.setHistoryDays([:])
        #expect(lib.visitDays(site: "a.com", today: today) == 4)
    }

    @Test func pagesFlaggedByAnOlderBroaderRuleComeBackToTheListWhenTheyAreNoLongerPlaces() {
        let lib = library(visitedDays: 6)
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        lib.save(url: "https://dash.example.com/team", title: "Team", verdict: place, browser: .helium, now: t0)
        // Flagged as places by the 1.4 rule, though they are deeper pages and a page named by a query value.
        lib.save(url: "https://dash.example.com/blog/hello-world", title: "Hello World", verdict: place, browser: .helium, now: t0)
        lib.save(url: "https://dash.example.com/article.php?id=7", title: "An article", verdict: place, browser: .helium, now: t0)
        #expect(lib.data.items.map(\.regular) == [true, true, true, true])
        let moved = lib.refreshPlaces()
        #expect(moved == 2)
        #expect(lib.data.items.map(\.regular) == [true, true, nil, nil])
        #expect(lib.data.items.count == 4)
        #expect(lib.refreshPlaces() == 0)
    }

    // MARK: The user's choices

    @Test func keepInMyListMovesTheRegularsOfThatPageTypeAndKeepsFuturePages() throws {
        let lib = library(visitedDays: 6)
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        lib.save(url: "https://dash.example.com/team", title: "Team", verdict: place, browser: .helium, now: t0)
        let id = try #require(lib.data.items.first?.id)
        lib.keepInList(id)
        #expect(lib.data.items.map(\.regular) == [false, false])
        #expect(lib.data.corrections["dash.example.com/~"]?.isSpecific == true)
        lib.save(url: "https://dash.example.com/billing", title: "Billing", verdict: place, browser: .helium, now: t0)
        #expect(lib.data.items.last?.regular == false)
    }

    @Test func thisIsARegularMovesThePageTypeOutOfTheList() throws {
        let lib = library(visitedDays: 1)
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        #expect(lib.data.items[0].regular == false)
        lib.markRegular(lib.data.items[0].id)
        #expect(lib.data.items[0].regular == true)
        #expect(lib.data.corrections["dash.example.com/~"]?.regular == true)
        lib.save(url: "https://dash.example.com/billing", title: "Billing", verdict: place, browser: .helium, now: t0)
        #expect(lib.data.items.last?.regular == true)
    }

    @Test func eachChoiceUndoesAndRedoes() throws {
        let lib = library(visitedDays: 6)
        lib.save(url: home, title: "Dash", verdict: place, browser: .helium, now: t0)
        let id = lib.data.items[0].id
        let undo = UndoManager()
        undo.groupsByEvent = false
        func step(_ body: () -> Void) { undo.beginUndoGrouping(); body(); undo.endUndoGrouping() }
        step { lib.keepInList(id, undo: undo) }
        #expect(lib.data.items[0].regular == false)
        undo.undo()
        #expect(lib.data.items[0].regular == true)
        #expect(lib.data.corrections["dash.example.com/~"] == nil)
        undo.redo()
        #expect(lib.data.items[0].regular == false)
        step { lib.markRegular(id, undo: undo) }
        #expect(lib.data.items[0].regular == true)
        undo.undo()
        #expect(lib.data.items[0].regular == false)
        #expect(lib.data.corrections["dash.example.com/~"]?.regular == nil)
        #expect(lib.data.corrections["dash.example.com/~"]?.isSpecific == true)
    }

    // MARK: Why

    @Test(arguments: [
        (nil as Bool?, 0, nil as Correction?, "New site"),
        (false, 2, nil, "New site"),
        (true, 18, nil, "Regular · visited 18 days this month"),
        (true, 0, nil, "Regular"),
        (nil, 9, nil, "A specific page on a regular site"),
        (false, 9, nil, "Kept in your list"),
        (false, 9, Correction(isSpecific: true), "Kept because you said so"),
        (true, 1, Correction(regular: true), "A regular because you said so"),
    ])
    func theInspectorSaysWhy(_ regular: Bool?, _ days: Int, _ correction: Correction?, _ line: String) {
        #expect(Familiarity.why(regular: regular, days: days, correction: correction) == line)
    }
}

@Suite @MainActor struct JudgeRegularsTests {
    let dash = "https://dash.example.com/"
    var today: Int { Familiarity.dayNumber(of: Date(timeIntervalSince1970: 1_800_000_000)) }

    func harness(visitedDays count: Int) -> Harness {
        let library = Library()
        library.setHistoryDays(["dash.example.com": Set((1...count).map { today - $0 })])
        return Harness(library: library)
    }

    func readOnePage(_ h: Harness, _ url: String, title: String = "Dash") {
        h.show(tab(url, title, active: true))
        h.wait(120); h.show(tab(url, title, active: true))
    }

    @Test func aPlaceOnARegularSiteThatHeldAttentionIsSavedAsARegular() {
        let h = harness(visitedDays: 5)
        readOnePage(h, dash)
        #expect(h.library.data.items.map(\.regular) == [true])
    }

    @Test func aPlaceOnANewSiteThatHeldAttentionIsKept() {
        let h = harness(visitedDays: 1)
        readOnePage(h, dash)
        #expect(h.library.data.items.map(\.regular) == [false])
    }

    @Test func aRegularStillNeedsAttentionToBeSaved() {
        let h = harness(visitedDays: 20)
        h.show(tab(dash, "Dash", active: true))
        h.wait(5); h.show(tab(dash, "Dash", active: true))
        #expect(h.saved.isEmpty)
    }

    @Test func aSpecificPageOnARegularSiteIsKeptInTheList() {
        let h = harness(visitedDays: 20)
        readOnePage(h, "https://dash.example.com/posts/how-we-found-the-bug", title: "How we found the bug")
        #expect(h.library.data.items.map(\.regular) == [nil])
    }

    @Test func thePageTypesCorrectionsDecideForNewPages() {
        let h = harness(visitedDays: 20)
        h.library.setCorrection(Correction(isSpecific: true), for: "dash.example.com/~")
        readOnePage(h, dash)
        #expect(h.library.data.items.map(\.regular) == [false])
    }

    func chrome() -> Harness {
        let h = Harness(watched: [.helium, .chrome])
        h.judge.setFrontBrowser(.chrome, now: h.now)
        return h
    }

    @Test func chromeSitesAreNotedFromTheActiveTabOfTheFrontWindowOnly() {
        let h = chrome()
        h.show(in: .chrome, tab("https://www.dash.example.com/a/b", "A", active: true),
               tab("https://standing.example.org/", "Standing"), tab("https://other.example.net/", "Other", active: true, window: 1))
        #expect(Set(h.library.data.siteDays.keys) == [Library.hash("dash.example.com")])
        #expect(h.library.data.siteDays.values.allSatisfy { $0 == [today] })
    }

    @Test func aStandingBackgroundTabNeverMakesItsSiteARegular() {
        let h = chrome()
        for _ in 0..<6 {
            h.show(in: .chrome, tab("https://front.example.com/", "Front", active: true), tab("https://standing.example.org/", "Standing"))
            h.wait(86_400)
        }
        #expect(h.library.visitDays(site: "standing.example.org", today: today + 6) == 0)
        #expect(h.library.visitDays(site: "front.example.com", today: today + 6) == 6)
    }

    @Test func chromeInTheBackgroundNotesNothing() {
        let h = chrome()
        h.judge.setFrontBrowser(.helium, now: h.now)
        h.show(in: .chrome, tab("https://dash.example.com/", "Dash", active: true))
        #expect(h.library.data.siteDays.isEmpty)
    }

    @Test func heliumDaysComeFromItsHistoryNeverFromWhatWasOpen() {
        let h = Harness()
        h.show(tab("https://dash.example.com/", "Dash", active: true), tab("https://other.example.org/", "Other"))
        #expect(h.library.data.siteDays.isEmpty)
    }
}

@Suite @MainActor struct RegularsSectionTests {
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)

    func data() -> LibraryData {
        let lib = Library()
        let saved = Verdict(isSpecific: true, kind: .reading, pageType: "a.com/p/*")
        let place = Verdict(isSpecific: true, kind: .tools, pageType: "dash.example.com/~", isPlace: true)
        lib.setHistoryDays(["dash.example.com": [1, 2, 3, 4, 5].reduce(into: Set<Int>()) { $0.insert(Familiarity.dayNumber(of: t0) - $1) }])
        lib.save(url: "https://a.com/p/1", title: "Post", verdict: saved, browser: .helium, now: t0)
        lib.save(url: "https://dash.example.com/", title: "Dash", verdict: place, browser: .helium, now: t0)
        lib.save(url: "https://new.example.org/", title: "New tool", verdict: Verdict(isSpecific: true, kind: .tools, pageType: "new.example.org/~", isPlace: true),
                 browser: .helium, now: t0)
        return lib.data
    }

    func titles(_ section: LibrarySection) -> [String] {
        LibraryQuery.items(data(), section: section, search: "", now: t0).map(\.title).sorted()
    }

    @Test func regularsShowOnlyInTheirOwnSection() {
        #expect(titles(.regulars) == ["Dash"])
        #expect(titles(.all) == ["New tool", "Post"])
        #expect(titles(.kind(.tools)) == ["New tool"])
        #expect(titles(.older).isEmpty && titles(.done).isEmpty)
    }

    @Test func aDonePageShowsInDoneWhetherOrNotItIsARegular() throws {
        var d = data()
        let i = try #require(d.items.firstIndex { $0.regular == true })
        d.items[i].status = .done
        d.items[i].lastSeen = t0.addingTimeInterval(-90 * 86_400)
        #expect(LibraryQuery.items(d, section: .done, search: "", now: t0).map(\.title) == ["Dash"])
        #expect(LibraryQuery.items(d, section: .older, search: "", now: t0).isEmpty)
        #expect(LibraryQuery.items(d, section: .regulars, search: "", now: t0).isEmpty)
        let counts = LibraryQuery.counts(d, now: t0)
        #expect(counts[.done] == 1 && counts[.regulars] == nil)
    }

    @Test func emptyDoneDeletesExactlyWhatTheDoneListShows() throws {
        let lib = Library(data: data())
        let i = try #require(lib.data.items.firstIndex { $0.regular == true })
        lib.setDone(lib.data.items[i].id, true, now: t0)   // a Done page that is also a regular
        let shown = Set(LibraryQuery.items(lib.data, section: .done, search: "", now: t0).map(\.id))
        #expect(shown.count == 1)
        let before = Set(lib.data.items.map(\.id))
        lib.emptyDone(now: t0)
        #expect(before.subtracting(Set(lib.data.items.map(\.id))) == shown)
    }

    @Test func countsFollowTheSections() {
        let counts = LibraryQuery.counts(data(), now: t0)
        #expect(counts[.regulars] == 1 && counts[.all] == 2 && counts[.kind(.tools)] == 1 && counts[.done] == nil)
        #expect(LibraryQuery.count(data(), section: .regulars, now: t0) == 1)
    }

    @Test func searchStillFindsARegular() {
        #expect(LibraryQuery.items(data(), section: .all, search: "dash", now: t0).map(\.title) == ["Dash"])
    }
}

@Suite struct SiteDaysFileTests {
    @Test func aListThatNeverNotedASiteIsWrittenWithoutTheField() throws {
        let json = String(decoding: try JSONEncoder().encode(LibraryData()), as: UTF8.self)
        #expect(!json.contains("siteDays"))
        var data = LibraryData()
        data.siteDays = ["abc": [1, 2]]
        let again = try JSONDecoder().decode(LibraryData.self, from: JSONEncoder().encode(data))
        #expect(again.siteDays == ["abc": [1, 2]])
    }
}
