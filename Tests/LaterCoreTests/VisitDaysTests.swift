import Foundation
import SQLite3
import Testing
@testable import LaterCore

@Suite struct VisitDaysTests {
    private let fm = FileManager.default
    private var utc: Calendar {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }

    private func withFolder(_ body: (String) throws -> Void) throws {
        let root = fm.temporaryDirectory.appendingPathComponent("LaterVisitDays-\(UUID().uuidString)").path
        defer { try? fm.removeItem(atPath: root) }
        try fm.createDirectory(atPath: root, withIntermediateDirectories: true)
        try body(root)
    }

    private func chromiumTime(_ unix: Double) -> Int64 { Int64((unix + 11_644_473_600) * 1_000_000) }

    /// A profile's History file as Chromium writes it, with `transition`. `visits` are (link, Unix time, transition).
    private func makeHistory(in root: String, profile: String = "Default", visits: [(String, Double, Int)]) throws {
        try fm.createDirectory(atPath: "\(root)/\(profile)", withIntermediateDirectories: true)
        var db: OpaquePointer?
        defer { sqlite3_close(db) }
        try #require(sqlite3_open("\(root)/\(profile)/History", &db) == SQLITE_OK)
        var sql = """
            CREATE TABLE urls(id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER DEFAULT 0 NOT NULL,
                              typed_count INTEGER DEFAULT 0 NOT NULL, last_visit_time INTEGER NOT NULL, hidden INTEGER DEFAULT 0 NOT NULL);
            CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER NOT NULL, visit_time INTEGER NOT NULL, transition INTEGER DEFAULT 0 NOT NULL);
            """
        let links = Array(Set(visits.map(\.0))).sorted()
        for (id, link) in links.enumerated() {
            sql += "INSERT INTO urls(id, url, last_visit_time) VALUES(\(id + 1), '\(link)', 0);"
        }
        for (link, time, transition) in visits {
            let id = links.firstIndex(of: link)! + 1
            sql += "INSERT INTO visits(url, visit_time, transition) VALUES(\(id), \(chromiumTime(time)), \(transition));"
        }
        try #require(sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK)
    }

    private let day0 = 1_767_225_600.0   // 2026-01-01 00:00 UTC
    private func unix(day: Int, hour: Double = 12) -> Double { day0 + Double(day) * 86_400 + hour * 3_600 }
    private var since: Date { Date(timeIntervalSince1970: day0) }
    private func number(_ day: Int) -> Int { Familiarity.dayNumber(of: Date(timeIntervalSince1970: day0 + Double(day) * 86_400 + 3_600), calendar: utc) }

    @Test func givesTheDaysEachSiteWasVisitedMergedAcrossProfilesAndWww() throws {
        try withFolder { root in
            try makeHistory(in: root, visits: [("https://www.a.com/x", unix(day: 1), 0), ("https://a.com/y", unix(day: 1, hour: 20), 1),
                                               ("https://a.com/", unix(day: 3), 0), ("https://b.org:8080/p?q=1#f", unix(day: 2), 0)])
            try makeHistory(in: root, profile: "Profile 1", visits: [("https://a.com/z", unix(day: 5), 0)])
            let days = HistoryLookup.visitDays(in: HistoryLookup.databases(in: root), since: since, calendar: utc)
            #expect(days == ["a.com": [number(1), number(3), number(5)], "b.org": [number(2)]])
        }
    }

    @Test func leavesOutOlderVisitsFramesAndPagesThatAreNotTheWeb() throws {
        try withFolder { root in
            try makeHistory(in: root, visits: [("https://old.com/", unix(day: -1), 0), ("https://ad.example.net/frame", unix(day: 2), 3),
                                               ("https://ad.example.net/manual", unix(day: 2), 4), ("chrome://settings/", unix(day: 2), 0),
                                               ("file:///tmp/a.html", unix(day: 2), 0), ("https://kept.com/", unix(day: 2), 0x30000000 | 1)])
            let days = HistoryLookup.visitDays(in: HistoryLookup.databases(in: root), since: since, calendar: utc)
            #expect(days == ["kept.com": [number(2)]])   // the transition's qualifier bits don't hide a typed visit
        }
    }

    @Test func aVisitJustAfterMidnightBelongsToTheNewDayInTheUsersTimeZone() throws {
        try withFolder { root in
            try makeHistory(in: root, visits: [("https://a.com/", unix(day: 1, hour: 23.9), 0)])
            var tokyo = Calendar(identifier: .gregorian)
            tokyo.timeZone = TimeZone(identifier: "Asia/Tokyo")!   // UTC+9: it is already day 2, 08:54
            let days = HistoryLookup.visitDays(in: HistoryLookup.databases(in: root), since: since, calendar: tokyo)
            #expect(days == ["a.com": [number(2)]])
        }
    }

    @Test func aMissingOrUnreadableHistoryGivesNothing() throws {
        try withFolder { root in
            #expect(HistoryLookup.visitDays(in: [], since: since, calendar: utc).isEmpty)
            try fm.createDirectory(atPath: "\(root)/Default", withIntermediateDirectories: true)
            try Data("not a database".utf8).write(to: URL(fileURLWithPath: "\(root)/Default/History"))
            #expect(HistoryLookup.visitDays(in: HistoryLookup.databases(in: root), since: since, calendar: utc).isEmpty)
        }
    }

    @Test func theHistoryFileIsNeverWritten() throws {
        try withFolder { root in
            try makeHistory(in: root, visits: [("https://a.com/", unix(day: 1), 0)])
            let path = "\(root)/Default/History"
            let before = try Data(contentsOf: URL(fileURLWithPath: path))
            _ = HistoryLookup.visitDays(in: [path], since: since, calendar: utc)
            #expect(try Data(contentsOf: URL(fileURLWithPath: path)) == before)
            #expect(try fm.contentsOfDirectory(atPath: "\(root)/Default") == ["History"])   // no journal or wal left behind
        }
    }
}
