import Foundation
import Testing
@testable import LaterCore

let shopHome = "https://www.amazon.com/"   // a habit site's home: general by its own rule
let productA = "https://shop.example.com/products/1000001"
let productB = "https://shop.example.com/products/1000002"

func tab(_ url: String, _ title: String = "Walnut desk organizer", active: Bool = false, window: Int = 0) -> Tab {
    Tab(url: url, title: title, isActive: active, windowIndex: window)
}

@MainActor
final class Harness {
    let library: Library
    let judge: Judge
    let watched: Set<Browser>
    var now = Date(timeIntervalSince1970: 1_800_000_000)

    init(library: Library = Library(), watched: Set<Browser> = [.helium]) {
        self.library = library
        self.watched = watched
        judge = Judge(library: library, watched: watched)
        judge.setFrontBrowser(.helium, now: now)
    }

    func wait(_ seconds: TimeInterval) { now += seconds }
    func show(_ tabs: Tab...) { judge.ingest(Snapshot(browser: .helium, tabs: tabs), now: now) }
    var saved: [String] { library.data.items.map(\.url) }
}

@Suite @MainActor struct JudgeTests {
    @Test func researchingSavesNothing() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true))
        for product in [productA, productB] {
            h.wait(1); h.show(tab(product, active: true))
            h.wait(20)
        }
        h.show(tab(shopHome, "Shop", active: true))
        #expect(h.saved.isEmpty)
    }

    @Test func spendingTimeSavesAtTheNextSnapshot() {
        let h = Harness()
        h.show(tab(productA, active: true))
        h.wait(120); h.show(tab(productA, active: true))   // the browser's next write: same tabs
        #expect(h.saved == [productA])
        h.wait(5); h.show(tab(shopHome, "Shop", active: true))   // the same tab goes back to the homepage
        #expect(h.saved == [productA])
    }

    @Test func backgroundTabLeftOpenIsSaved() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(61); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved == [productA])
    }

    @Test func backgroundTabClosedAfterAMinuteIsStillSaved() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(61); h.show(tab(shopHome, "Shop", active: true))
        #expect(h.saved == [productA])
    }

    @Test func quickLookThenCloseIsNotSaved() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true))
        h.wait(1); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        h.wait(15); h.show(tab(shopHome, "Shop", active: true))
        #expect(h.saved.isEmpty)
        #expect(h.library.data.dwellSamples == [15])
    }

    @Test func comingBackSavesImmediately() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true))
        h.wait(1); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        h.wait(10); h.show(tab(shopHome, "Shop", active: true))
        h.wait(3600); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        #expect(h.saved == [productA])
    }

    @Test func homepagesOpenAllDayAreNeverSaved() {
        let h = Harness()
        let amazon = tab("https://www.amazon.com/", "Amazon.com. Spend less. Smile more.", active: true)
        let feed = tab("https://x.com/home", "Home / X")
        h.show(amazon, feed)
        h.wait(86_400); h.show(amazon, feed)
        #expect(h.saved.isEmpty)
    }

    @Test func incognitoWindowsAreNeverSaved() throws {
        let h = Harness()
        let modes = [1: "incognito", 2: "normal"]
        let windows = [1: WindowTabs(activeIndex: 1, urls: [productA], titles: ["Walnut desk organizer"]),
                       2: WindowTabs(activeIndex: 1, urls: [shopHome], titles: ["Shop"])]
        let snapshot = try Snapshot.collect(browser: .helium, windowIDs: [1, 2],
                                            mode: { modes[$0]! }, tabs: { windows[$0]! })
        h.judge.ingest(snapshot, now: h.now)
        h.wait(61); h.judge.ingest(snapshot, now: h.now)
        #expect(h.saved.isEmpty)
    }

    @Test func linksWithSecretsAreNeverSaved() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA + "?token=abc"))
        h.wait(61); h.show(tab(shopHome, "Shop", active: true), tab(productA + "?token=abc"))
        #expect(h.saved.isEmpty)
    }

    @Test func pagesLeftOpenWhileUsingAnotherAppAreSaved() {
        let h = Harness()
        h.show(tab(productA, active: true))
        h.judge.setFrontBrowser(nil, now: h.now)
        h.wait(61); h.judge.setFrontBrowser(.helium, now: h.now)
        #expect(h.saved == [productA])
    }

    @Test func visitsCountReturnsButNotTheFirstReadAfterLaunch() {
        let library = Library()
        let h = Harness(library: library)
        h.show(tab(productA, active: true))
        h.wait(120); h.show(tab(productA, active: true))
        let relaunched = Harness(library: library)
        relaunched.now = h.now + 10
        relaunched.show(tab(productA, active: true))
        #expect(library.data.items.first?.visits == 1)
        relaunched.wait(5); relaunched.show(tab(shopHome, "Shop", active: true))
        relaunched.wait(5); relaunched.show(tab(productA, active: true))
        #expect(library.data.items.first?.visits == 2)
    }

    @Test func doneItemsAreNotSavedAgainAutomatically() throws {
        let h = Harness()
        h.show(tab(productA, active: true)); h.wait(120); h.show(tab(productA, active: true))
        h.library.setDone(try #require(h.library.data.items.first?.id), true, now: h.now)
        h.wait(1); h.show(tab(shopHome, "Shop", active: true))
        h.wait(1); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        h.wait(200); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        #expect(h.library.data.items.count == 1)
        #expect(h.library.data.items.first?.status == .done)
    }

    @Test func notAnIdeaStopsThatKindOfPage() {
        let h = Harness()
        h.show(tab(productA, active: true))
        h.library.notAnIdea(pageType: "shop.example.com/products/*", url: nil)
        h.wait(1); h.show(tab(productB, active: true))
        h.wait(300); h.show(tab(productB, active: true))
        #expect(h.saved.isEmpty)
    }

    @Test func aPageOpenInTwoTabsCountsOnce() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA), tab(productA, window: 1))
        h.wait(61); h.show(tab(shopHome, "Shop", active: true), tab(productA), tab(productA, window: 1))
        #expect(h.saved == [productA])
    }

    @Test func aTitleThatIsStillLoadingDoesNotResetThePage() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(30); h.show(tab(shopHome, "Shop", active: true), tab(productA, ""))   // reload: empty title
        h.wait(31); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved == [productA])
    }

    /// An unchanged tab isn't judged again, but the user's corrections still apply to it on every read: here, undoing
    /// Not an Idea while its tab stays open.
    @Test func aCorrectionMadeAfterTheTabWasJudgedStillApplies() {
        let h = Harness()
        h.library.notAnIdea(pageType: "shop.example.com/products/*", url: nil)
        h.show(tab(productA, active: true))
        h.wait(130); h.show(tab(productA, active: true))
        #expect(h.saved.isEmpty)
        h.library.setCorrection(Correction(kind: .school), for: "shop.example.com/products/*")
        h.show(tab(productA, active: true))
        h.wait(130); h.show(tab(productA, active: true))
        #expect(h.saved == [productA])
        #expect(h.library.data.items.first?.kind == .school)
    }

    @Test func notAnIdeaWinsOverAPageAlreadyBeingTimed() throws {
        let h = Harness()
        h.show(tab(productB, active: true)); h.wait(120); h.show(tab(productB, active: true))
        h.show(tab(productB, active: true), tab(productA))
        h.wait(30); h.library.notAnIdea(try #require(h.library.data.items.first?.id))
        h.wait(31); h.show(tab(productB, active: true), tab(productA))
        #expect(h.saved.isEmpty)
    }

    @Test func comingBackCountsWhenTheTitleArrivesLate() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true))
        h.wait(1); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        h.wait(10); h.show(tab(shopHome, "Shop", active: true))
        h.wait(3600); h.show(tab(shopHome, "Shop"), tab(productA, "", active: true))
        h.wait(1); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        #expect(h.saved == [productA])
    }

    @Test func leftOpenTimeCountsFromBeforeTheTitleArrived() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA, ""))
        h.wait(61); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved == [productA])
    }

    @Test func aMinuteLeftOpenCountsWhenThePageComesOnScreenAsItsTitleArrives() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA, ""))
        h.wait(61); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        h.wait(1); h.show(tab(shopHome, "Shop", active: true))
        #expect(h.saved == [productA])
    }

    @Test(arguments: [false, true])
    func aMinuteLeftOpenCountsWhenThePageComesOnScreenBeforeItsTitle(returnsToBrowser: Bool) {
        let h = Harness()
        if returnsToBrowser {
            h.judge.setFrontBrowser(nil, now: h.now)
            h.show(tab(productA, "", active: true))
            h.wait(61); h.judge.setFrontBrowser(.helium, now: h.now)
        } else {
            h.show(tab(shopHome, "Shop", active: true), tab(productA, ""))
            h.wait(61); h.show(tab(shopHome, "Shop"), tab(productA, "", active: true))
        }
        #expect(h.saved.isEmpty)
        h.wait(5); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        #expect(h.saved == [productA])
    }

    @Test func halfAMinuteInTheBackgroundBeforeItsTitleIsNotSaved() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA, ""))
        h.wait(30); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        h.wait(10); h.show(tab(shopHome, "Shop", active: true))
        #expect(h.saved.isEmpty)
    }

    @Test func notAnIdeaWinsOverALeftOpenMinuteBeforeTheTitle() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA, ""))
        h.wait(61); h.show(tab(shopHome, "Shop"), tab(productA, "", active: true))
        h.library.notAnIdea(pageType: "shop.example.com/products/*", url: nil)
        h.wait(5); h.show(tab(shopHome, "Shop"), tab(productA, active: true))
        h.wait(1); h.show(tab(shopHome, "Shop", active: true))
        #expect(h.saved.isEmpty)
    }

    @Test func comingBackCountsOnTheFirstReadAfterLaunch() {
        let library = Library()
        library.markSeen(productA, now: Date(timeIntervalSince1970: 1_800_000_000 - 3600))
        let h = Harness(library: library)
        h.show(tab(productA, active: true))
        #expect(h.saved == [productA])
        #expect(library.data.items.first?.visits == 1)
    }

    @Test func timeWhileTheBrowserIsClosedDoesNotCount() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(30); h.judge.browserQuit(.helium, now: h.now)
        h.wait(7200); h.judge.setFrontBrowser(.helium, now: h.now)
        h.show(tab(shopHome, "Shop", active: true), tab(productA))   // relaunch restores the same tabs
        #expect(h.saved.isEmpty)
        h.wait(59); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved.isEmpty)
        h.wait(2); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved == [productA])
    }

    @Test func aMinuteLeftOpenBeforeQuittingIsKept() {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(61); h.judge.browserQuit(.helium, now: h.now)
        #expect(h.saved == [productA])
    }

    @Test func aRestoredSessionIsNotAReturnVisit() {
        let h = Harness()
        h.show(tab(productA, active: true))
        h.wait(120); h.show(tab(productA, active: true), tab(productB))   // A saved; B opened 0 s ago
        h.wait(10); h.judge.browserQuit(.helium, now: h.now)
        h.wait(7200); h.judge.setFrontBrowser(.helium, now: h.now)
        h.show(tab(productA, active: true), tab(productB))
        #expect(h.saved == [productA])
        #expect(h.library.data.items.first?.visits == 1)
        #expect(!h.library.wasSeen(productB, now: h.now))
    }

    @Test func quittingOneBrowserLeavesTheOtherAlone() {
        let h = Harness()
        h.judge.ingest(Snapshot(browser: .chrome, tabs: [tab(productB)]), now: h.now)
        h.show(tab(productA, active: true))
        h.wait(30); h.judge.browserQuit(.helium, now: h.now)
        h.wait(31); h.judge.ingest(Snapshot(browser: .chrome, tabs: [tab(productB)]), now: h.now)
        #expect(h.saved == [productB])
        #expect(h.judge.frontBrowser == nil)
    }

    @Test(arguments: [false, true])
    func pagesClosedTogetherAreJudgedByTheSameThreshold(reversed: Bool) {
        let h = Harness()
        for _ in 0..<19 { h.library.addDwellSample(10) }
        func show(_ aActive: Bool) {
            let tabs = [tab(productA, active: aActive), tab(productB, active: !aActive)]
            h.judge.ingest(Snapshot(browser: .helium, tabs: reversed ? tabs.reversed() : tabs), now: h.now)
        }
        for i in 0..<4 { show(i % 2 == 0); h.wait(35) }
        h.show(tab(shopHome, "Shop", active: true))
        #expect(h.saved.isEmpty)
    }
}

@Suite @MainActor struct DismissalTests {
    /// Saves productA as a background tab left open, then the user deletes it with the tab still open.
    func savedThenDeletedWhileOpen(watched: Set<Browser> = [.helium]) throws -> Harness {
        let h = Harness(watched: watched)
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(61); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved == [productA])
        h.library.delete(try #require(h.library.data.items.first?.id), now: h.now)
        return h
    }

    /// Later restarts: a fresh judge on the same list, whose first read finds the tab still open.
    func restarted(_ h: Harness) -> Harness {
        let r = Harness(library: h.library, watched: h.watched)
        r.now = h.now + 30
        return r
    }

    @Test func deletedPageIsNotSavedAgainWhileItsTabStaysOpen() throws {
        let h = try savedThenDeletedWhileOpen()
        h.wait(120); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved.isEmpty)
        let r = restarted(h)
        r.show(tab(shopHome, "Shop", active: true), tab(productA))
        r.wait(120); r.show(tab(shopHome, "Shop", active: true), tab(productA))   // left open
        r.wait(1); r.show(tab(shopHome, "Shop"), tab(productA, active: true))
        r.wait(300); r.show(tab(shopHome, "Shop"), tab(productA, active: true))   // spent time
        #expect(r.saved.isEmpty)
    }

    @Test func closingTheTabEndsTheDismissal() throws {
        let r = restarted(try savedThenDeletedWhileOpen())
        r.show(tab(shopHome, "Shop", active: true), tab(productA))
        r.wait(10); r.show(tab(shopHome, "Shop", active: true))   // closed
        #expect(!r.library.isDismissed(productA))
        r.wait(3600); r.show(tab(shopHome, "Shop", active: true), tab(productA))   // came back
        #expect(r.saved == [productA])
    }

    @Test func pageDeletedAfterItsTabClosedCountsNormallyWhenOpenedAgain() throws {
        let h = Harness()
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(61); h.show(tab(shopHome, "Shop", active: true))
        let item = try #require(h.library.data.items.first)
        h.library.setDone(item.id, true, now: h.now)
        h.library.emptyDone(now: h.now)
        #expect(h.library.isDismissed(productA))
        h.wait(3600); h.show(tab(shopHome, "Shop", active: true), tab(productA))   // came back
        #expect(h.saved == [productA])
        #expect(!h.library.isDismissed(productA))
    }

    func chrome(_ h: Harness, _ tabs: Tab...) { h.judge.ingest(Snapshot(browser: .chrome, tabs: tabs), now: h.now) }

    @Test func aCopyInAnotherBrowserIsNotSavedAfterTheDelete() throws {
        let h = Harness(watched: [.helium, .chrome])
        h.show(tab(shopHome, "Shop", active: true), tab(productA))
        h.wait(30); chrome(h, tab(productA))   // its own record, not yet left open long enough
        h.wait(31); h.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(h.saved == [productA])
        h.library.delete(try #require(h.library.data.items.first?.id), now: h.now)
        h.wait(120); chrome(h, tab(productA))   // Chrome's copy has now been left open
        #expect(h.saved.isEmpty)
        h.wait(1); chrome(h)   // and closing it doesn't save it either
        #expect(h.saved.isEmpty)
    }

    @Test func closingOneBrowsersCopyKeepsTheDismissal() throws {
        let h = try savedThenDeletedWhileOpen(watched: [.helium, .chrome])
        chrome(h, tab(productA))
        h.wait(10); h.show(tab(shopHome, "Shop", active: true))   // Helium's copy closed
        #expect(h.library.isDismissed(productA))
        h.wait(10); h.show(tab(shopHome, "Shop", active: true), tab(productA))   // opened anew in Helium
        #expect(h.library.isDismissed(productA))
        h.wait(10); h.show(tab(shopHome, "Shop", active: true))
        let r = restarted(h)
        r.show(tab(shopHome, "Shop", active: true))
        r.judge.ingest(Snapshot(browser: .chrome, tabs: [tab(productA)]), now: r.now)
        r.wait(120); r.judge.ingest(Snapshot(browser: .chrome, tabs: [tab(productA)]), now: r.now)
        #expect(r.saved.isEmpty)
        r.wait(1); r.judge.ingest(Snapshot(browser: .chrome, tabs: []), now: r.now)   // the last copy closed
        #expect(!r.library.isDismissed(productA))
    }

    /// After a restart Helium's copy closes before Chrome's first read, which still has it open.
    @Test func aBrowserNotYetReadKeepsTheDismissal() throws {
        let r = restarted(try savedThenDeletedWhileOpen(watched: [.helium, .chrome]))
        r.show(tab(shopHome, "Shop", active: true), tab(productA))
        r.wait(10); r.show(tab(shopHome, "Shop", active: true))   // Helium's copy closed
        #expect(r.library.isDismissed(productA))
        chrome(r, tab(productA))
        r.wait(120); chrome(r, tab(productA))
        r.wait(1); chrome(r)
        #expect(r.saved.isEmpty)
    }

    @Test func onlyHeliumWatchedClosingAndReopeningSavesAsBefore() throws {
        let r = restarted(try savedThenDeletedWhileOpen())
        r.show(tab(shopHome, "Shop", active: true), tab(productA))
        r.wait(10); r.show(tab(shopHome, "Shop", active: true))
        #expect(!r.library.isDismissed(productA))
        r.wait(3600); r.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(r.saved == [productA])
    }

    @Test func aQuitBrowserDoesNotBlockTheDismissalEnding() throws {
        let r = restarted(try savedThenDeletedWhileOpen(watched: [.helium, .chrome]))
        r.judge.browserQuit(.chrome, now: r.now)   // or never running: its read says so
        r.show(tab(shopHome, "Shop", active: true), tab(productA))
        r.wait(10); r.show(tab(shopHome, "Shop", active: true))
        #expect(!r.library.isDismissed(productA))
        r.wait(3600); r.show(tab(shopHome, "Shop", active: true), tab(productA))
        #expect(r.saved == [productA])
    }

    @Test func aRelaunchedBrowserIsUnknownUntilItsFirstRead() throws {
        let r = restarted(try savedThenDeletedWhileOpen(watched: [.helium, .chrome]))
        r.judge.browserQuit(.chrome, now: r.now)
        r.judge.browserLaunched(.chrome)   // it may be restoring a copy
        r.show(tab(shopHome, "Shop", active: true), tab(productA))
        r.wait(10); r.show(tab(shopHome, "Shop", active: true))
        #expect(r.library.isDismissed(productA))
        chrome(r)   // its first read: nothing open
        r.wait(3600); r.show(tab(shopHome, "Shop", active: true), tab(productA))   // opened anew
        #expect(r.saved == [productA])
    }

    @Test func ingestPrunesOldEntries() {
        let h = Harness()
        h.library.markSeen(productA, now: h.now)
        h.wait(31 * 86_400); h.show(tab(shopHome, "Shop", active: true))
        #expect(h.library.data.seen.isEmpty)
    }
}

/// v1.1: Helium restores a session as background tabs with their real links and titles.
@Suite @MainActor struct RelaunchTests {
    static let keptSites = ["https://brightlane.example.com/", "https://linkvault.example.net/video", "https://acmehq.example.com/", "https://quill.example.com/",
                            "https://search-lab.example.com/", "https://github.com/octocat", "https://pinecone.example.com/", "https://example.org/",
                            "https://github.com/login", "https://chatgpt.com/", "https://console.acme.example.com/login?returnTo=%2F"]
    static let ignored = ["https://www.google.com/search?q=swift", "https://mail.google.com/mail/u/0/", "http://localhost:3000/",
                          "https://www.youtube.com/", "https://www.reddit.com/", "https://x.com/home",
                          "chrome://newtab/", "chrome://settings/", "https://accounts.google.com/signin"]

    @Test func aRestoredSessionSavesTheKeptSitesAfterAMinuteOffScreen() {
        let h = Harness()
        let tabs = Self.keptSites.map { tab($0, "Title of \($0)") } + Self.ignored.map { tab($0, "Other") }
            + [tab("https://x.com/home", "Home / X", active: true)]
        #expect(tabs.count == 21)
        h.judge.browserLaunched(.helium)
        h.judge.ingest(Snapshot(browser: .helium, tabs: tabs), now: h.now)
        h.wait(30); h.judge.ingest(Snapshot(browser: .helium, tabs: tabs), now: h.now)
        #expect(h.saved.isEmpty)
        h.wait(35); h.judge.ingest(Snapshot(browser: .helium, tabs: tabs), now: h.now)
        #expect(Set(h.saved) == Set(Self.keptSites))
        #expect(h.library.data.items.allSatisfy { $0.kind == .tools })
    }

    @Test func anEmptyTitleSitePageIsSavedUnderItsHostAndRetitledWhenTheTitleArrives() {
        let h = Harness()
        h.show(tab("https://acmehq.example.com/", "", active: false), tab("https://x.com/home", "Home / X", active: true))
        h.wait(65); h.show(tab("https://acmehq.example.com/", ""), tab("https://x.com/home", "Home / X", active: true))
        #expect(h.library.data.items.map(\.title) == ["acmehq.example.com"])
        h.wait(5); h.show(tab("https://acmehq.example.com/", "Acme HQ"), tab("https://x.com/home", "Home / X", active: true))
        #expect(h.library.data.items.map(\.title) == ["Acme HQ"])
    }

    @Test func notAnIdeaOnASiteLevelPageStopsThatHostsShallowPagesFromBeingSaved() throws {
        let h = Harness()
        h.show(tab("https://linkvault.example.net/", "Link Vault", active: true))
        h.wait(30); h.show(tab("https://linkvault.example.net/", "Link Vault"), tab("https://x.com/home", "Home / X", active: true))
        h.wait(65); h.show(tab("https://linkvault.example.net/", "Link Vault"), tab("https://x.com/home", "Home / X", active: true))
        let item = try #require(h.library.data.items.first)
        #expect(item.pageType == "linkvault.example.net/~")
        h.library.notAnIdea(item.id)
        #expect(h.library.data.items.isEmpty)
        h.wait(1); h.show(tab("https://linkvault.example.net/video", "Link Vault Video"), tab("https://x.com/home", "Home / X", active: true))
        h.wait(65); h.show(tab("https://linkvault.example.net/video", "Link Vault Video"), tab("https://x.com/home", "Home / X", active: true))
        #expect(h.saved.isEmpty)
        // Other hosts are unaffected.
        h.show(tab("https://acmehq.example.com/", "Acme"), tab("https://x.com/home", "Home / X", active: true))
        h.wait(65); h.show(tab("https://acmehq.example.com/", "Acme"), tab("https://x.com/home", "Home / X", active: true))
        #expect(h.saved == ["https://acmehq.example.com/"])
    }
}
