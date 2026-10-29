import Foundation
import Testing
@testable import LaterCore

@MainActor final class Counter { var count = 0 }

@Suite @MainActor struct LibraryTests {
    let verdict = Verdict(isSpecific: true, kind: .shopping, pageType: "a.com/p/*")
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    let link = "https://www.a.com/p/1234567"

    @Test func savesOnceAndIgnoresDuplicates() {
        let lib = Library()
        #expect(lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0))
        #expect(!lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0))
        #expect(lib.data.items.count == 1)
        #expect(lib.data.items[0].host == "a.com")
        #expect(lib.data.items[0].visits == 1)
    }

    @Test func loadingDropsSavedPagesWhoseLinkCarriesASecretInThePath() {
        let lib = Library()
        lib.save(url: "https://a.com/reset-password/Xk9fA2bQ7zLm", title: "Reset", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://a.com/invite/0b9f6c1e-52a7-4c1b-9d3e-7a4c2f1e8b60", title: "Invite", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://a.com/#/accept/Xk9fA2bQ7zLm", title: "Accept", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://a.com/account/settings/billing", title: "Billing", verdict: verdict, browser: .helium, now: t0)
        #expect(lib.dropSecretLinks() == 3)
        #expect(lib.data.items.map(\.title) == ["Desk", "Billing"])
        #expect(!lib.contains(url: "https://a.com/reset-password/Xk9fA2bQ7zLm"))   // the index follows
        #expect(lib.dropSecretLinks() == 0)
    }

    @Test func doneItemsAreNotSavedAgainButUnmarkingBringsThemBack() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        let id = try #require(lib.data.items.first?.id)
        lib.setDone(id, true, now: t0)
        #expect(!lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0))
        #expect(lib.data.items[0].status == .done)
        lib.setDone(id, false, now: t0 + 5)
        #expect(lib.data.items[0].status == .active)
        #expect(lib.data.items[0].doneAt == nil)
    }

    @Test func visitsOnlyCountForActiveItems() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.recordVisit(url: link, now: t0 + 60)
        #expect(lib.data.items[0].visits == 2)
        #expect(lib.data.items[0].lastSeen == t0 + 60)
        lib.setDone(try #require(lib.data.items.first?.id), true, now: t0)
        lib.recordVisit(url: link, now: t0 + 120)
        #expect(lib.data.items[0].visits == 2)
    }

    @Test func restoreUndoesADeleteExactly() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://www.a.com/p/7654321", title: "Lamp", verdict: verdict, browser: .helium, now: t0)
        let before = lib.data
        let changes = Counter()
        lib.onChange = { changes.count += 1 }
        let desk = lib.data.items[0]
        lib.delete(desk.id, now: t0)
        #expect(!lib.contains(url: link))
        #expect(lib.isDismissed(link))
        lib.restore(desk, at: 0)
        #expect(!lib.isDismissed(link))
        #expect(lib.data == before)
        #expect(changes.count == 2)
        #expect(lib.contains(url: link))
        lib.recordVisit(url: link, now: t0 + 60)   // the url index points at the restored item again
        #expect(lib.data.items[0].visits == 2)
    }

    @Test func restoreReplacesAPageSavedAgainSinceTheDelete() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://www.a.com/p/7654321", title: "Lamp", verdict: verdict, browser: .helium, now: t0)
        lib.recordVisit(url: link, now: t0 + 60)
        let before = lib.data
        let desk = lib.data.items[0]
        lib.delete(desk.id, now: t0)
        lib.save(url: link, title: "Desk again", verdict: verdict, browser: .chrome, now: t0 + 120)   // the tab is still open
        let changes = Counter()
        lib.onChange = { changes.count += 1 }
        lib.restore(desk, at: 0)
        #expect(lib.data == before)   // the original wins, back at its old place, with one entry for the page
        #expect(changes.count == 1)
        lib.recordVisit(url: link, now: t0 + 180)
        #expect(lib.data.items[0].visits == 3)
    }

    @Test func moveTeachesThePageType() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://www.a.com/p/7654321", title: "Lamp", verdict: verdict, browser: .helium, now: t0)
        lib.move(try #require(lib.data.items.first?.id), to: .school)
        #expect(lib.data.items.allSatisfy { $0.kind == .school })
        #expect(lib.data.corrections["a.com/p/*"] == Correction(kind: .school))
    }

    @Test func notAnIdeaDeletesOnlyThatItemAndTeachesThePageType() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://www.a.com/p/7654321", title: "Lamp", verdict: verdict, browser: .helium, now: t0)
        lib.notAnIdea(try #require(lib.data.items.first?.id))
        #expect(lib.data.items.map(\.title) == ["Lamp"])
        #expect(lib.data.corrections["a.com/p/*"]?.isSpecific == false)
    }

    @Test func deleteAndEmptyDone() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.save(url: "https://www.a.com/p/7654321", title: "Lamp", verdict: verdict, browser: .helium, now: t0)
        lib.setDone(try #require(lib.data.items.first?.id), true, now: t0)
        lib.emptyDone(now: t0)
        #expect(lib.data.items.map(\.title) == ["Lamp"])
        #expect(lib.isDismissed(link))
        #expect(!lib.isDismissed("https://www.a.com/p/7654321"))
        lib.delete(try #require(lib.data.items.first?.id), now: t0)
        #expect(lib.data.items.isEmpty)
        #expect(!lib.contains(url: link))
        #expect(lib.isDismissed("https://www.a.com/p/7654321"))
        #expect(!lib.data.dismissed.keys.contains { $0.contains("a.com") })
    }

    @Test func dwellThresholdLearnsAndClamps() {
        let lib = Library()
        #expect(lib.dwellThreshold == 120)
        for _ in 0..<20 { lib.addDwellSample(10) }
        #expect(lib.dwellThreshold == 60)
        for _ in 0..<200 { lib.addDwellSample(1000) }
        #expect(lib.dwellThreshold == 300)
        #expect(lib.data.dwellSamples.count == 200)
        let mid = Library()
        for s in 1...100 { mid.addDwellSample(Double(s) * 2) }
        #expect(mid.dwellThreshold == 150)
    }

    @Test func seenStoresHashesNotLinks() {
        let lib = Library()
        lib.markSeen(link, now: t0)
        #expect(lib.wasSeen(link, now: t0 + 86_400))
        #expect(!lib.wasSeen(link, now: t0 + 31 * 86_400))
        #expect(!lib.data.seen.keys.contains { $0.contains("a.com") })
        lib.prune(now: t0 + 31 * 86_400)
        #expect(lib.data.seen.isEmpty)
    }

    @Test func pruneDropsOldSeenAndDismissalsAtMostDaily() throws {
        let lib = Library()
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.delete(try #require(lib.data.items.first?.id), now: t0)
        lib.markSeen(link, now: t0)
        let day: TimeInterval = 86_400
        lib.prune(now: t0 + 29.5 * day)   // nothing old yet
        #expect(lib.data.seen.count == 1 && lib.isDismissed(link))
        lib.prune(now: t0 + 30.25 * day)  // old now, but pruned less than a day ago: skipped
        #expect(lib.data.seen.count == 1 && lib.isDismissed(link))
        lib.prune(now: t0 + 30.5 * day + 1)
        #expect(lib.data.seen.isEmpty && !lib.isDismissed(link))
    }

    @Test func everyChangeIsReported() {
        let lib = Library()
        let counter = Counter()
        lib.onChange = { counter.count += 1 }
        lib.save(url: link, title: "Desk", verdict: verdict, browser: .helium, now: t0)
        lib.markSeen(link, now: t0)
        #expect(counter.count == 2)
    }

    @Test func unchangedSeenDoesNotReportAChange() {
        let lib = Library()
        let counter = Counter()
        lib.markSeen(link, now: t0)
        lib.onChange = { counter.count += 1 }
        lib.markSeen(link, now: t0)
        lib.prune(now: t0 + 86_400)
        #expect(counter.count == 0)
    }
}

@Suite @MainActor struct EmptyTitleTests {
    @Test func aPageSavedWithoutATitleIsTitledWithItsHostUntilTheRealOneArrives() {
        let lib = Library()
        lib.save(url: "https://www.acmehq.example.com/", title: "", verdict: Verdict(isSpecific: true, kind: .tools, pageType: "acmehq.example.com/~"), browser: .helium, now: .now)
        #expect(lib.data.items[0].title == "acmehq.example.com")
        lib.updateTitle(url: "https://www.acmehq.example.com/", title: "")
        #expect(lib.data.items[0].title == "acmehq.example.com")
        lib.updateTitle(url: "https://www.acmehq.example.com/", title: "Acme HQ")
        #expect(lib.data.items[0].title == "Acme HQ")
    }
}

@Suite @MainActor struct RefreshKindsTests {
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func uncorrectedItemsTakeTheCurrentRulesAndCorrectedOnesKeepTheUsersKind() {
        let lib = Library()
        lib.save(url: "https://github.com/apple/swift", title: "GitHub - apple/swift", verdict: Verdict(isSpecific: true, kind: .other, pageType: "github.com/*/*"), browser: .helium, now: t0)
        lib.save(url: "https://github.com/octocat", title: "octocat", verdict: Verdict(isSpecific: true, kind: .other, pageType: "github.com/*"), browser: .helium, now: t0)
        lib.save(url: "https://www.amazon.com/dp/B0F1234567", title: "Sony", verdict: Verdict(isSpecific: true, kind: .shopping, pageType: "amazon.com/dp/*"), browser: .helium, now: t0)
        lib.move(lib.data.items[1].id, to: .reading)   // The user's correction for github.com/*
        let before = lib.data.items
        lib.refreshKinds()
        #expect(lib.data.items.map(\.kind) == [.tools, .reading, .shopping])
        #expect(lib.data.items.map(\.title) == before.map(\.title) && lib.data.items.map(\.url) == before.map(\.url))
        #expect(lib.data.items.map(\.firstSaved) == before.map(\.firstSaved) && lib.data.items.count == 3)
    }

    @Test func nothingIsUnsavedWhenARuleNowCallsAPageGeneral() {
        let lib = Library()
        lib.save(url: "https://www.youtube.com/", title: "YouTube", verdict: Verdict(isSpecific: true, kind: .videos, pageType: "youtube.com/"), browser: .helium, now: t0)
        lib.refreshKinds()
        #expect(lib.data.items.count == 1)
    }

    @Test func aPageWithoutAHostKeepsItsKindAndCountsAreReported() {
        let lib = Library()
        lib.save(url: "about:blank", title: "Blank", verdict: Verdict(isSpecific: true, kind: .shopping, pageType: "/"), browser: .helium, now: t0)
        lib.save(url: "https://github.com/apple/swift", title: "swift", verdict: Verdict(isSpecific: true, kind: .other, pageType: "github.com/*/*"), browser: .helium, now: t0)
        let counts = lib.refreshKinds()
        #expect(lib.data.items.map(\.kind) == [.shopping, .tools])
        #expect(counts.items == 2 && counts.changed == 1 && counts.toTools == 1)
    }

    @Test func aWhitespaceOnlyTitleIsSavedAsTheHost() {
        let lib = Library()
        lib.save(url: "https://www.a.com/p/1234567", title: " \n\t ", verdict: Verdict(isSpecific: true, kind: .shopping, pageType: "a.com/p/*"), browser: .helium, now: t0)
        #expect(lib.data.items[0].title == "a.com")
    }
}
