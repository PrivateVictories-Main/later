import Foundation
import Testing
@testable import LaterCore

@Suite @MainActor struct PerfTests {
    @Test(.enabled(if: ProcessInfo.processInfo.environment["LATER_PERF"] == "1"))
    func searchOver5000ItemsIsUnder16ms() {
        let now = Date()
        var data = LibraryData()
        data.items = (0..<5000).map { i in
            Item(id: UUID(), url: "https://shop\(i % 50).example.com/products/\(1_000_000 + i)",
                 title: "Walnut desk organizer number \(i)", host: "shop\(i % 50).example.com", kind: .shopping,
                 pageType: "shop.example.com/products/*", browser: .helium, firstSaved: now, lastSeen: now,
                 visits: 1, status: .active, doneAt: nil)
        }
        let start = ContinuousClock.now
        for query in ["w", "wa", "wal", "waln", "walnu", "walnut"] {
            let found = LibraryQuery.items(data, section: .all, search: query, now: now)
            _ = LibraryQuery.groups(found, searching: true, now: now)   // the list's headings, as each keystroke draws them
        }
        let perKeystroke = (ContinuousClock.now - start) / 6
        print("search per keystroke: \(perKeystroke)")
        #expect(perKeystroke < .milliseconds(16))
    }

    /// A section's date headings over 5,000 items spread across a year, so most fall into month groups.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["LATER_PERF"] == "1"))
    func groupingOver5000ItemsIsUnder16ms() {
        let now = Date()
        var data = LibraryData()
        data.items = (0..<5000).map { i in
            let seen = now.addingTimeInterval(-Double(i) * 6300)   // about a year in all
            return Item(id: UUID(), url: "https://shop\(i % 50).example.com/products/\(1_000_000 + i)",
                        title: "Walnut desk organizer number \(i)", host: "shop\(i % 50).example.com", kind: .shopping,
                        pageType: "shop.example.com/products/*", browser: .helium, firstSaved: seen, lastSeen: seen,
                        visits: 1, status: .active, doneAt: nil)
        }
        // The first call also loads the system's date-formatting data, once per process (about 15 ms here).
        _ = LibraryQuery.groups(Array(data.items.suffix(1)), searching: false, now: now)
        let start = ContinuousClock.now
        let groups = LibraryQuery.groups(LibraryQuery.items(data, section: .older, search: "", now: now), searching: false, now: now)
        let elapsed = ContinuousClock.now - start
        print("older section with headings: \(elapsed), \(groups.count) groups")
        #expect(groups.count >= 12)
        #expect(elapsed < .milliseconds(16))
    }

    /// A browser's snapshot as the judge reads it on every ring: 40 tabs across a few kinds of site, with the tracking
    /// parameters and fragments real links carry. `round` gives every tab a new page.
    private func snapshot(round: Int) -> Snapshot {
        let tabs = (0..<40).map { i in
            let n = round * 100 + i
            let url = switch i % 5 {
            case 0: "https://www.amazon.com/Walnut-Desk-Organizer/dp/B0\(1_000_000 + n)/ref=sr_1_3?keywords=desk&utm_source=x"
            case 1: "https://news.ycombinator.com/item?id=\(40_000_000 + n)"
            case 2: "https://github.com/apple/swift/issues/\(n)#issuecomment-\(n)"
            case 3: "https://www.youtube.com/watch?v=abc\(n)&t=42s&si=track"
            default: "https://example\(i).com/blog/2026/10/\(n)-a-long-post-title?fbclid=zz\(n)"
            }
            return Tab(url: url, title: "Page \(n): a title long enough to read", isActive: i == 0, windowIndex: i / 20)
        }
        return Snapshot(browser: .helium, tabs: tabs)
    }

    /// Prints the judge's time per ring for 40 tabs with 1,000 pages saved, over 20 rings that change nothing and 20 that
    /// change every tab.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["LATER_PERF"] == "1"))
    func judgeIngestOf40Tabs() {
        let now = Date()
        var saved = LibraryData()
        saved.items = (0..<1000).map { i in
            Item(id: UUID(), url: "https://shop.example.com/products/\(i)", title: "Saved \(i)", host: "shop.example.com",
                 kind: .shopping, pageType: "shop.example.com/products/*", browser: .helium, firstSaved: now, lastSeen: now,
                 visits: 1, status: .active, doneAt: nil)
        }
        for changing in [false, true] {
            let library = Library(data: saved)
            let judge = Judge(library: library)
            var now = now
            judge.ingest(snapshot(round: 0), now: now)   // the first read
            let start = ContinuousClock.now
            for round in 1...20 {
                now += 10
                judge.ingest(snapshot(round: changing ? round : 0), now: now)
            }
            let perIngest = (ContinuousClock.now - start) / 20
            print("judge ingest, 40 tabs, \(changing ? "every tab changed" : "unchanged"): \(perIngest)")
        }
    }
}
