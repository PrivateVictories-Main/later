import Foundation
import Testing
@testable import LaterCore

@Suite struct LibraryQueryTests {
    let now = Date(timeIntervalSince1970: 1_800_000_000)

    /// The URL holds no title, so a title match is a title match. `doneDaysAgo` marks the item done.
    func item(_ title: String, kind: Kind = .shopping, daysAgo: Double, doneDaysAgo: Double? = nil,
              host: String = "a.com") -> Item {
        let seen = now.addingTimeInterval(-daysAgo * 86_400)
        return Item(id: UUID(), url: "https://\(host)/p/1", title: title, host: host, kind: kind,
                    pageType: "\(host)/p/*", browser: .helium, firstSaved: seen, lastSeen: seen, visits: 1,
                    status: doneDaysAgo == nil ? .active : .done, doneAt: doneDaysAgo.map { now.addingTimeInterval(-$0 * 86_400) })
    }

    /// Deliberately out of order, so every expected order below comes from sorting.
    func library(_ items: [Item]) -> LibraryData {
        var d = LibraryData()
        d.items = items
        return d
    }

    var data: LibraryData {
        library([
            item("Lamp", daysAgo: 20),
            item("Essay", kind: .school, daysAgo: 2, host: "canvas.school.edu"),
            item("Chair", daysAgo: 3, doneDaysAgo: 3),
            item("Desk", daysAgo: 1),
        ])
    }

    @Test func sectionsSplitByKindAgeAndStatus() {
        #expect(LibraryQuery.items(data, section: .all, search: "", now: now).map(\.title) == ["Desk", "Essay"])
        #expect(LibraryQuery.items(data, section: .kind(.school), search: "", now: now).map(\.title) == ["Essay"])
        #expect(LibraryQuery.items(data, section: .older, search: "", now: now).map(\.title) == ["Lamp"])
        #expect(LibraryQuery.items(data, section: .done, search: "", now: now).map(\.title) == ["Chair"])
    }

    @Test func searchLooksEverywhereWithActiveItemsFirst() {
        #expect(LibraryQuery.items(data, section: .kind(.school), search: "a", now: now).map(\.title)
                == ["Desk", "Essay", "Lamp", "Chair"])
        #expect(LibraryQuery.items(data, section: .all, search: "CANVAS", now: now).map(\.title) == ["Essay"])
    }

    @Test func searchMatchesTitlesAlone() {
        #expect(LibraryQuery.items(data, section: .done, search: " lamp ", now: now).map(\.title) == ["Lamp"])
    }

    @Test func olderStartsJustPastFourteenDays() {
        let d = library([item("Past", daysAgo: 14 + 1.0 / 86_400), item("Edge", daysAgo: 14)])
        #expect(LibraryQuery.items(d, section: .all, search: "", now: now).map(\.title) == ["Edge"])
        #expect(LibraryQuery.items(d, section: .older, search: "", now: now).map(\.title) == ["Past"])
    }

    @Test func theListLooksTheSameUntilTheDayChangesOrAPageMovesToOlder() {
        let calendar = Calendar(identifier: .gregorian)
        let morning = calendar.startOfDay(for: now).addingTimeInterval(9 * 3_600)
        let d = library([item("Recent", daysAgo: 1), item("Done", daysAgo: 3, doneDaysAgo: 3)])
        #expect(LibraryQuery.looksSame(d, at: morning, as: morning.addingTimeInterval(3_600), calendar: calendar))
        #expect(!LibraryQuery.looksSame(d, at: morning, as: morning.addingTimeInterval(16 * 3_600), calendar: calendar))
        // A page last seen 14 days before a quarter past nine moves to Older at a quarter past nine, the same day.
        let edge = library([item("Edge", daysAgo: (now.timeIntervalSince(morning) - 900 + LibraryQuery.olderAfter) / 86_400)])
        #expect(LibraryQuery.looksSame(edge, at: morning, as: morning.addingTimeInterval(600), calendar: calendar))
        #expect(!LibraryQuery.looksSame(edge, at: morning, as: morning.addingTimeInterval(1_200), calendar: calendar))
    }

    @Test func doneSortsByWhenItWasMarkedDone() {
        // Seen order is Recent, Early; done order is the reverse.
        let d = library([item("Recent", daysAgo: 1, doneDaysAgo: 2), item("Early", daysAgo: 3, doneDaysAgo: 1)])
        #expect(LibraryQuery.items(d, section: .done, search: "", now: now).map(\.title) == ["Early", "Recent"])
    }

    @Test func countsMatchSections() {
        #expect(LibraryQuery.count(data, section: .all, now: now) == 2)
        #expect(LibraryQuery.count(data, section: .kind(.shopping), now: now) == 1)
        #expect(LibraryQuery.count(data, section: .older, now: now) == 1)
        #expect(LibraryQuery.count(data, section: .done, now: now) == 1)
    }

    @Test func oneCountingPassMatchesEverySection() {
        let counts = LibraryQuery.counts(data, now: now)
        for section in [LibrarySection.all, .older, .done] + Kind.allCases.map(LibrarySection.kind) {
            #expect(counts[section, default: 0] == LibraryQuery.count(data, section: section, now: now))
        }
    }

    @Test func groupsBySiteMostRecentSiteFirst() {
        let items = [item("A1", daysAgo: 1, host: "a.com"), item("B1", daysAgo: 2, host: "b.com"), item("A2", daysAgo: 3, host: "a.com")]
        let groups = LibraryQuery.groups(items, by: .site, searching: false, now: now)
        #expect(groups.map(\.title) == ["a.com", "b.com"])
        #expect(groups.map { $0.items.map(\.title) } == [["A1", "A2"], ["B1"]])
    }

    @Test func groupsByKindInKindOrder() {
        let items = [item("Essay", kind: .school, daysAgo: 1), item("Desk", daysAgo: 2), item("Notes", kind: .school, daysAgo: 3)]
        let groups = LibraryQuery.groups(items, by: .kind, searching: false, now: now)
        #expect(groups.map(\.title) == ["Shopping", "School"])
        #expect(groups.map { $0.items.map(\.title) } == [["Desk"], ["Essay", "Notes"]])
    }

    @Test func aSearchGroupsByStatusWhateverTheGrouping() {
        let found = LibraryQuery.items(data, section: .all, search: "a", now: now)
        for grouping in LibraryGrouping.allCases {
            #expect(LibraryQuery.groups(found, by: grouping, searching: true, now: now, calendar: Self.utc).map(\.title)
                    == ["Saved", "Done"])
        }
    }

    /// Saturday 2026-10-24 12:00 UTC, in a fixed calendar so headings and dates don't depend on this Mac.
    static let saturday = Date(timeIntervalSince1970: 1_792_843_200)
    static let utc: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        calendar.locale = Locale(identifier: "en_US")
        return calendar
    }()

    func at(_ text: String) -> Date { try! Date(text + "Z", strategy: .iso8601) }

    @Test func sectionsGroupByDayThenMonth() {
        let dates = ["2026-10-24T11:00:00", "2026-10-23T23:00:00", "2026-10-21T12:00:00", "2026-10-17T00:00:00",
                     "2026-10-16T23:59:59", "2026-09-24T00:00:00", "2026-09-09T12:00:00", "2025-12-02T12:00:00"]
        let items = dates.map { date in
            Item(id: UUID(), url: "https://a.com/\(date)", title: date, host: "a.com", kind: .reading, pageType: "a.com/*",
                 browser: .helium, firstSaved: at(date), lastSeen: at(date), visits: 1, status: .active, doneAt: nil)
        }
        let groups = LibraryQuery.groups(items, searching: false, now: Self.saturday, calendar: Self.utc)
        #expect(groups.map(\.title) == ["Today", "Yesterday", "Previous 7 Days", "Previous 30 Days", "September", "December 2025"])
        #expect(groups.map { $0.items.map(\.title) } == [[dates[0]], [dates[1]], [dates[2], dates[3]], [dates[4], dates[5]],
                                                         [dates[6]], [dates[7]]])
    }

    @Test func doneGroupsByWhenItWasMarkedDone() {
        let seenLongAgo = item("Chair", daysAgo: 90, doneDaysAgo: 0)
        var done = seenLongAgo
        done.doneAt = Self.saturday.addingTimeInterval(-3600)
        #expect(LibraryQuery.groups([done], searching: false, now: Self.saturday, calendar: Self.utc).map(\.title) == ["Today"])
    }

    @Test func searchGroupsByStatus() {
        let found = LibraryQuery.items(data, section: .all, search: "a", now: now)
        let groups = LibraryQuery.groups(found, searching: true, now: now, calendar: Self.utc)
        #expect(groups.map(\.title) == ["Saved", "Done"])
        #expect(groups.map { $0.items.map(\.title) } == [["Desk", "Essay", "Lamp"], ["Chair"]])
        let saved = LibraryQuery.items(data, section: .all, search: "desk", now: now)
        #expect(LibraryQuery.groups(saved, searching: true, now: now, calendar: Self.utc).map(\.title) == ["Saved"])
    }

    @Test func shortDatesMatchTheHeadings() {
        func short(_ text: String) -> String {
            LibraryQuery.shortDate(at(text), now: Self.saturday, calendar: Self.utc).replacingOccurrences(of: "\u{202F}", with: " ")
        }
        #expect(short("2026-10-24T09:41:00") == "9:41 AM")
        #expect(short("2026-10-23T21:05:00") == "9:05 PM")
        #expect(short("2026-10-21T12:00:00") == "Wed")
        #expect(short("2026-10-18T00:00:00") == "Sun")
        #expect(short("2026-10-17T23:00:00") == "Oct 17")   // a week ago would share today's weekday
        #expect(short("2025-12-02T12:00:00") == "Dec 2, 2025")
    }
}
