import Foundation
import Testing
@testable import LaterCore

/// How many tabs hold each page, across every watched browser.
@Suite @MainActor struct OpenCopiesTests {
    let wwwLink = "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
    let bareLink = "https://youtube.com/watch?v=dQw4w9WgXcQ&t=5s"

    @Test func countsCopiesAcrossTabsAndBrowsers() {
        let h = Harness(watched: [.helium, .chrome])
        h.judge.ingest(Snapshot(browser: .helium, tabs: [tab(wwwLink, "Video", active: true), tab(bareLink, "Video"), tab(productA)]), now: h.now)
        h.judge.ingest(Snapshot(browser: .chrome, tabs: [tab(wwwLink, "Video", active: true)]), now: h.now)
        let video = LinkCleaner.pageKey(LinkCleaner.clean(wwwLink)!)
        #expect(h.judge.openCopies[video] == 3)
        #expect(h.judge.openCopies[LinkCleaner.pageKey(LinkCleaner.clean(productA)!)] == 1)
    }

    @Test func closingTabsAndQuittingLowerTheCount() {
        let h = Harness(watched: [.helium, .chrome])
        h.judge.ingest(Snapshot(browser: .helium, tabs: [tab(wwwLink, "Video", active: true), tab(bareLink, "Video")]), now: h.now)
        h.judge.ingest(Snapshot(browser: .chrome, tabs: [tab(wwwLink, "Video", active: true)]), now: h.now)
        let video = LinkCleaner.pageKey(LinkCleaner.clean(wwwLink)!)
        h.judge.ingest(Snapshot(browser: .helium, tabs: [tab(wwwLink, "Video", active: true)]), now: h.now)
        #expect(h.judge.openCopies[video] == 2)
        h.judge.browserQuit(.chrome, now: h.now)
        #expect(h.judge.openCopies[video] == 1)
        h.judge.ingest(Snapshot(browser: .helium, tabs: []), now: h.now)
        #expect(h.judge.openCopies.isEmpty)
    }

    @Test func aPageOpenAsWwwAndBareIsOneItem() {
        let h = Harness()
        h.show(tab("https://example.com/", "Home", active: true), tab(wwwLink, "Video"), tab(bareLink, "Video"))
        h.wait(61); h.show(tab("https://example.com/", "Home", active: true), tab(wwwLink, "Video"), tab(bareLink, "Video"))
        #expect(h.library.data.items.filter { $0.host == "youtube.com" }.count == 1)
    }

    @Test func aPageDeletedAsWwwStaysDeletedWhenItsTabIsOpenAsBare() throws {
        let h = Harness()
        h.show(tab("https://example.com/", "Home", active: true), tab(wwwLink, "Video"))
        h.wait(61); h.show(tab("https://example.com/", "Home", active: true), tab(wwwLink, "Video"))
        h.library.delete(try #require(h.library.data.items.first { $0.host == "youtube.com" }?.id), now: h.now)
        h.wait(300); h.show(tab("https://example.com/", "Home", active: true), tab(bareLink, "Video"))
        h.wait(300); h.show(tab("https://example.com/", "Home", active: true), tab(bareLink, "Video"))
        #expect(!h.library.data.items.contains { $0.host == "youtube.com" })
    }
}
