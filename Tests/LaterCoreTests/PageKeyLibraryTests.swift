import Foundation
import Testing
@testable import LaterCore

/// One page is one item, however its link was written, and the user's deletes survive the change of key.
@Suite @MainActor struct PageKeyLibraryTests {
    let verdict = Verdict(isSpecific: true, kind: .videos, pageType: "youtube.com/watch?v")
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    let wwwLink = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=abc"
    let bareLink = "http://youtube.com/watch?v=dQw4w9WgXcQ&t=30s"

    @Test func theSamePageWrittenTwoWaysIsSavedOnce() {
        let library = Library()
        #expect(library.save(url: wwwLink, title: "Video", verdict: verdict, browser: .helium, now: t0))
        #expect(!library.save(url: bareLink, title: "Video", verdict: verdict, browser: .chrome, now: t0))
        #expect(library.contains(url: bareLink))
        #expect(library.data.items.count == 1)
        #expect(library.data.items[0].url == wwwLink)   // opening it goes back to the link it was saved with
    }

    @Test func aVisitAndATitleReachThePageWhicheverLinkAsks() {
        let library = Library()
        library.save(url: wwwLink, title: "Video", verdict: verdict, browser: .helium, now: t0)
        library.recordVisit(url: bareLink, now: t0 + 60)
        library.updateTitle(url: bareLink, title: "Video (new title)")
        #expect(library.data.items[0].visits == 2)
        #expect(library.data.items[0].title == "Video (new title)")
    }

    private func item(_ url: String, title: String, saved: TimeInterval, seen: TimeInterval, visits: Int, done: TimeInterval? = nil,
                      kind: Kind = .videos, type: String = "youtube.com/watch?v") -> Item {
        Item(id: UUID(), url: url, title: title, host: "youtube.com", kind: kind, pageType: type, browser: .helium,
             firstSaved: t0 + saved, lastSeen: t0 + seen, visits: visits, status: done == nil ? .active : .done,
             doneAt: done.map { t0 + $0 })
    }

    @Test func loadingMergesPagesThatAreReallyOne() {
        var data = LibraryData()
        data.items = [
            item(wwwLink, title: "Old title", saved: 100, seen: 200, visits: 2),
            item("https://example.com/other", title: "Other", saved: 50, seen: 50, visits: 1),
            item(bareLink, title: "New title", saved: 10, seen: 900, visits: 3),
        ]
        let library = Library(data: data)
        #expect(library.mergeDuplicates() == 1)
        #expect(library.data.items.map(\.title) == ["New title", "Other"])   // the earliest save keeps its place, the latest title wins
        let merged = library.data.items[0]
        #expect(merged.firstSaved == t0 + 10)
        #expect(merged.lastSeen == t0 + 900)
        #expect(merged.visits == 5)
        #expect(merged.status == .active)
        #expect(library.mergeDuplicates() == 0)
        #expect(library.contains(url: wwwLink) && library.contains(url: bareLink))
    }

    @Test func aMergedPageIsDoneOnlyIfItWasMarkedDoneAfterTheOtherCopyWasSeen() {
        var data = LibraryData()
        // Done at 50, but the other copy was seen at 100: the user has been back to it, so it is active.
        data.items = [item(wwwLink, title: "A", saved: 0, seen: 0, visits: 1, done: 50), item(bareLink, title: "B", saved: 1, seen: 100, visits: 1)]
        let seenLater = Library(data: data)
        seenLater.mergeDuplicates()
        #expect(seenLater.data.items.count == 1 && seenLater.data.items[0].status == .active && seenLater.data.items[0].doneAt == nil)

        // Done at 80, after the other copy was last seen at 1: it stays done.
        data.items = [item(wwwLink, title: "A", saved: 0, seen: 0, visits: 1, done: 80), item(bareLink, title: "B", saved: 1, seen: 1, visits: 1)]
        let doneLater = Library(data: data)
        doneLater.mergeDuplicates()
        #expect(doneLater.data.items[0].status == .done && doneLater.data.items[0].doneAt == t0 + 80)

        data.items = [item(wwwLink, title: "A", saved: 0, seen: 0, visits: 1, done: 50), item(bareLink, title: "B", saved: 1, seen: 1, visits: 1, done: 80)]
        let allDone = Library(data: data)
        allDone.mergeDuplicates()
        #expect(allDone.data.items[0].status == .done && allDone.data.items[0].doneAt == t0 + 80)
    }

    @Test func aMergedPageKeepsTheUsersKind() {
        var data = LibraryData()
        data.items = [item(wwwLink, title: "A", saved: 0, seen: 500, visits: 1, kind: .videos),
                      item(bareLink, title: "B", saved: 1, seen: 1, visits: 1, kind: .school, type: "youtube.com/moved")]
        data.corrections["youtube.com/moved"] = Correction(kind: .school)
        let library = Library(data: data)
        library.mergeDuplicates()
        #expect(library.data.items[0].kind == .school && library.data.items[0].pageType == "youtube.com/moved")
        #expect(library.data.corrections["youtube.com/moved"]?.kind == .school)
    }

    @Test func differentPagesOnOneSiteAreNotMerged() {
        var data = LibraryData()
        data.items = [item("https://tv.example.com/watch/AAAAAAAAAAA", title: "Live", saved: 0, seen: 0, visits: 1),
                      item("https://tv.example.com/watch/BBBBBBBBBBB", title: "Live", saved: 1, seen: 1, visits: 1)]
        let library = Library(data: data)
        #expect(library.mergeDuplicates() == 0)
        #expect(library.data.items.count == 2)
    }

    @Test func aPageDeletedBeforeV12StaysDeleted() {
        // v1.1 hashed the cleaned link itself; the same link must still be dismissed under the page key.
        var data = LibraryData()
        data.dismissed[Library.hash(wwwLink)] = t0
        data.seen[Library.hash(wwwLink)] = t0
        let library = Library(data: data)
        #expect(library.isDismissed(wwwLink))
        #expect(library.wasSeen(wwwLink, now: t0 + 60))
        library.undismiss(wwwLink)
        #expect(!library.isDismissed(wwwLink))
        #expect(library.data.dismissed.isEmpty)
    }

    @Test func newDeletesAndSightingsAreHashedByPageKeyAndMatchEveryWriting() throws {
        let library = Library()
        library.save(url: wwwLink, title: "Video", verdict: verdict, browser: .helium, now: t0)
        library.delete(try #require(library.data.items.first?.id), now: t0)
        #expect(library.data.dismissed.keys.contains(Library.hash("youtube.com/watch?v=dQw4w9WgXcQ")))
        #expect(library.isDismissed(bareLink))
        library.markSeen(bareLink, now: t0)
        #expect(library.wasSeen(wwwLink, now: t0 + 1))
    }
}
