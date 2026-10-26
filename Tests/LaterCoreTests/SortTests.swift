import Foundation
import Testing
@testable import LaterCore

@Suite struct SortTests {
    static let now = Date(timeIntervalSince1970: 1_800_000_000)
    var now: Date { Self.now }
    private static func ago(_ days: Double) -> Date { now.addingTimeInterval(-days * 86_400) }
    private func ago(_ days: Double) -> Date { Self.ago(days) }

    /// `saved` and `seen` are days ago; `opened` days ago, nil: not known. `place` is the page's window rank and tab.
    static func entry(_ title: String, host: String, saved: Double, seen: Double? = nil, opened: Double? = nil,
               place: (window: Int, tab: Int)? = nil, browser: Browser = .helium) -> Item {
        Item(id: UUID(), url: "https://\(host)/\(title)", title: title, host: host, kind: .reading, pageType: "\(host)/*",
             browser: browser, firstSaved: ago(saved), lastSeen: ago(seen ?? saved), visits: 1, status: .active, doneAt: nil,
             openedAt: opened.map { ago($0) }, tabIndex: place?.tab, windowRank: place?.window)
    }

    // Open: Zeta (window 0, tab 0), alpha (0, 2), Mid (1, 0). Closed: Beta, Gamma.
    let zeta: Item, alpha: Item, mid: Item, beta: Item, gamma: Item
    let data: LibraryData
    let open: Set<UUID>

    init() {
        zeta = Self.entry("Zeta", host: "a.com", saved: 5, opened: 30, place: (0, 0))
        alpha = Self.entry("alpha", host: "b.com", saved: 6, seen: 1, place: (0, 2))
        mid = Self.entry("Mid", host: "c.com", saved: 3, opened: 10, place: (1, 0))
        beta = Self.entry("Beta", host: "a.com", saved: 2, opened: 20)
        gamma = Self.entry("Gamma", host: "b.com", saved: 4, opened: 4.5)
        var data = LibraryData()
        data.items = [zeta, alpha, mid, beta, gamma]   // not in any order the tests expect
        self.data = data
        open = [zeta.id, alpha.id, mid.id]
    }

    func titles(_ sort: LibrarySort, reversed: Bool = false) -> [String] {
        LibraryQuery.items(data, section: .all, search: "", now: now, sort: sort, reversed: reversed, open: open).map(\.title)
    }

    @Test func tabOrderListsOpenPagesByWindowThenTabThenClosedNewestFirst() {
        #expect(titles(.tabOrder) == ["Zeta", "alpha", "Mid", "Beta", "Gamma"])
    }

    @Test func rightToLeftReversesWindowsAndTabsButNotTheClosedPages() {
        #expect(titles(.tabOrderReversed) == ["Mid", "alpha", "Zeta", "Beta", "Gamma"])
    }

    @Test func tabOrderKeepsBrowsersTogether() {
        let chrome = Self.entry("Chrome low", host: "a.com", saved: 1, place: (0, 0), browser: .chrome)
        let helium = Self.entry("Helium high", host: "a.com", saved: 1, place: (5, 9))
        var data = LibraryData()
        data.items = [chrome, helium]
        let open: Set<UUID> = [chrome.id, helium.id]
        for sort in [LibrarySort.tabOrder, .tabOrderReversed] {
            #expect(LibraryQuery.items(data, section: .all, search: "", now: now, sort: sort, open: open).map(\.title)
                    == ["Helium high", "Chrome low"])
        }
    }

    @Test func dateOrdersRunNewestFirstAndFlip() {
        // Opened, or saved where that isn't known: Gamma 4.5, alpha 6 (saved), Mid 10, Beta 20, Zeta 30.
        #expect(titles(.dateOpened) == ["Gamma", "alpha", "Mid", "Beta", "Zeta"])
        #expect(titles(.dateOpened, reversed: true) == ["Zeta", "Beta", "Mid", "alpha", "Gamma"])
        // Saved: Beta 2, Mid 3, Gamma 4, Zeta 5, alpha 6.
        #expect(titles(.dateSaved) == ["Beta", "Mid", "Gamma", "Zeta", "alpha"])
        #expect(titles(.dateSaved, reversed: true) == ["alpha", "Zeta", "Gamma", "Mid", "Beta"])
        // Last seen: alpha 1, Beta 2, Mid 3, Gamma 4, Zeta 5.
        #expect(titles(.lastSeen) == ["alpha", "Beta", "Mid", "Gamma", "Zeta"])
        #expect(titles(.lastSeen, reversed: true) == ["Zeta", "Gamma", "Mid", "Beta", "alpha"])
    }

    @Test func titleAndSiteOrderAToZ() {
        #expect(titles(.title) == ["alpha", "Beta", "Gamma", "Mid", "Zeta"])
        #expect(titles(.title, reversed: true) == ["Zeta", "Mid", "Gamma", "Beta", "alpha"])
        // a.com: Beta (2) then Zeta (5); b.com: alpha (1) then Gamma (4); c.com: Mid.
        #expect(titles(.site) == ["Beta", "Zeta", "alpha", "Gamma", "Mid"])
        #expect(titles(.site, reversed: true) == ["Mid", "alpha", "Gamma", "Beta", "Zeta"])
    }

    @Test func tabOrderGroupsAreOpenThenClosed() {
        let items = [zeta, beta]
        let groups = LibraryQuery.groups(items, by: .date, searching: false, now: now, sort: .tabOrder, open: [zeta.id])
        #expect(groups.map(\.title) == ["Open", "Closed"])
        #expect(groups.map { $0.items.map(\.title) } == [["Zeta"], ["Beta"]])
        #expect(LibraryQuery.groups([beta], by: .date, searching: false, now: now, sort: .tabOrder).map(\.title) == ["Closed"])
        // A search still groups by status.
        #expect(LibraryQuery.groups(items, by: .date, searching: true, now: now, sort: .tabOrder, open: [zeta.id]).map(\.title) == ["Saved"])
    }

    @Test func dateHeadingsGoByTheDateTheSortIsBy() {
        let calendar = Calendar(identifier: .gregorian)
        let page = Self.entry("Old but seen today", host: "a.com", saved: 0.1, opened: 40)
        func heading(_ sort: LibrarySort) -> String {
            LibraryQuery.groups([page], by: .date, searching: false, now: now, sort: sort, calendar: calendar)[0].title
        }
        #expect(heading(.lastSeen) == "Today")
        #expect(heading(.dateSaved) == "Today")
        #expect(heading(.dateOpened) != "Today")   // opened 40 days ago: a month heading
        #expect(LibraryQuery.groupDate(page, .dateOpened) == page.openedAt)
        #expect(LibraryQuery.groupDate(page, .title) == page.lastSeen)
    }

    private func school(_ path: String, _ title: String = "Discussion", host: String = "learn.school.edu") -> Item {
        Item(id: UUID(), url: "https://\(host)\(path)", title: title, host: host, kind: .school, pageType: "x",
             browser: .helium, firstSaved: ago(2), lastSeen: ago(2), visits: 1, status: .active, doneAt: nil)
    }

    @Test func idsAreNeverShownAndAWordIsHumanized() {
        let a = school("/ultra/courses/_48213_1/discussion"), b = school("/ultra/courses/_51877_1/discussion")
        let other = school("/ultra/courses/_1_1/outline", "Outline")
        let hints = LibraryQuery.differentiators([a, b, other], now: now)
        // Only course ids tell these apart, so each says when it was opened instead.
        #expect(hints[a.id]?.hasPrefix("Opened ") == true && hints[b.id]?.hasPrefix("Opened ") == true)
        #expect(hints[a.id]?.contains("_48213_1") == false)
        #expect(hints[other.id] == nil)   // alone with its title

        let bio = school("/webapps/blackboard/execute/launcher?type=Course&id=_48213_1&course=intro-to_biology")
        let chem = school("/webapps/blackboard/execute/launcher?type=Course&id=_51877_1&course=organic-chemistry")
        let named = LibraryQuery.differentiators([bio, chem], now: now)
        #expect(named[bio.id] == "Intro To Biology" && named[chem.id] == "Organic Chemistry")

        // Hex, UUIDs and long ids are dropped like the numbered ones; a long name is cut to 24 characters.
        let uuid = school("/c/9f1c2d3e-1111-4222-8333-444455556666/home"), hex = school("/c/deadbeefcafe/home")
        let long = school("/c/an-unreasonably-long-course-name-for-a-hint/home")
        let all = LibraryQuery.differentiators([uuid, hex, long], now: now)
        #expect(all[uuid.id]?.hasPrefix("Opened ") == true && all[hex.id]?.hasPrefix("Opened ") == true)
        #expect(all[long.id] == "An Unreasonably Long Co…" && all[long.id]!.count <= 24)
        #expect(LibraryQuery.differentiators([a, school("/ultra/courses/_48213_1/discussion", host: "other.edu")], now: now).isEmpty)
    }

    @Test func aHintTwoRowsShareIsReplacedByTheOpenedTime() {
        let a = school("/c/intro-biology/home?tab=x"), b = school("/c/intro-biology/home?tab=y"), c = school("/c/organic-chem/home")
        let hints = LibraryQuery.differentiators([a, b, c], now: now)
        #expect(hints[c.id] == "Organic Chem")
        #expect(hints[a.id]?.hasPrefix("Opened ") == true)
    }

    @Test func linksThatDifferOnlyInTheirQueryValueAreToldApartByIt() {
        func video(_ id: String) -> Item {
            Item(id: UUID(), url: "https://www.youtube.com/watch?v=\(id)", title: "YouTube TV", host: "youtube.com", kind: .videos,
                 pageType: "x", browser: .helium, firstSaved: ago(2), lastSeen: ago(2), visits: 1, status: .active, doneAt: nil)
        }
        let a = video("news-live"), b = video("dQw4w9WgXcQ")   // a channel name; a video id, which is never shown
        let hints = LibraryQuery.differentiators([a, b], now: now)
        #expect(hints[a.id] == "News Live" && hints[b.id]?.hasPrefix("Opened ") == true)
    }

    @Test func withNothingInTheLinkToTellThemApartTheOpenedDateDoes() {
        func page(opened: Double) -> Item {
            Item(id: UUID(), url: "https://a.com/same", title: "Same", host: "a.com", kind: .other, pageType: "x", browser: .helium,
                 firstSaved: ago(1), lastSeen: ago(1), visits: 1, status: .active, doneAt: nil, openedAt: ago(opened))
        }
        let a = page(opened: 40), b = page(opened: 3)
        let hints = LibraryQuery.differentiators([a, b], now: now)
        #expect(hints[a.id]?.hasPrefix("Opened ") == true)
        #expect(hints[a.id] != hints[b.id])
    }

    private func data(_ items: [Item]) -> LibraryData {
        var data = LibraryData()
        data.items = items
        return data
    }

    @Test func dateHeadingsStayChronologicalUnderATitleOrSiteSort() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!   // the test's "now" is 08:00 UTC
        let old = Self.entry("Apple", host: "z.com", saved: 10)     // Previous 30 Days
        let week = Self.entry("Banana", host: "y.com", saved: 3)     // Previous 7 Days
        let today = Self.entry("Cherry", host: "x.com", saved: 0.1)  // Today
        for sort in [LibrarySort.title, .site] {
            let items = LibraryQuery.items(data([old, week, today]), section: .all, search: "", now: now, sort: sort)
            let groups = LibraryQuery.groups(items, by: .date, searching: false, now: now, sort: sort, calendar: calendar)
            #expect(groups.map(\.title) == ["Today", "Previous 7 Days", "Previous 30 Days"], "\(sort)")
            let flipped = LibraryQuery.items(data([old, week, today]), section: .all, search: "", now: now, sort: sort, reversed: true)
            #expect(LibraryQuery.groups(flipped, by: .date, searching: false, now: now, sort: sort, reversed: true, calendar: calendar).map(\.title)
                    == ["Previous 30 Days", "Previous 7 Days", "Today"], "\(sort) reversed")
        }
        // Inside a heading the chosen key still decides: A to Z by title.
        let also = Self.entry("Aardvark", host: "w.com", saved: 0.2)
        let items = LibraryQuery.items(data([old, today, also]), section: .all, search: "", now: now, sort: .title)
        let groups = LibraryQuery.groups(items, by: .date, searching: false, now: now, sort: .title, calendar: calendar)
        #expect(groups.first?.items.map(\.title) == ["Aardvark", "Cherry"])
    }

    @Test func agoCountsWholeDays() {
        let calendar = Calendar(identifier: .gregorian)
        func say(_ days: Int) -> String {
            LibraryQuery.ago(calendar.date(byAdding: .day, value: -days, to: now)!, now: now, calendar: calendar)
        }
        #expect(say(0) == "today")
        #expect(say(1) == "yesterday")
        #expect(say(4) == "4 days ago")
        #expect(say(7) == "1 week ago")
        #expect(say(21) == "3 weeks ago")
        #expect(say(61) == "2 months ago")
        #expect(say(800) == "2 years ago")
    }
}
