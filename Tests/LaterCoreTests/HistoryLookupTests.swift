import Foundation
import SQLite3
import Testing
@testable import LaterCore

@Suite struct HistoryLookupTests {
    private let fm = FileManager.default

    /// Runs `body` with a fresh temp folder standing in for Helium's data folder, always removed afterwards.
    private func withFolder(_ body: (String) throws -> Void) throws {
        let root = fm.temporaryDirectory.appendingPathComponent("LaterHistory-\(UUID().uuidString)").path
        defer { try? fm.removeItem(atPath: root) }
        try fm.createDirectory(atPath: root, withIntermediateDirectories: true)
        try body(root)
    }

    private func chromiumTime(_ unix: Double) -> Int64 { Int64((unix + 11_644_473_600) * 1_000_000) }

    /// A profile's History file with Chromium's two tables. `pages` maps a link to its visit count and visit times (Unix).
    private func makeHistory(in root: String, profile: String = "Default", pages: [String: (count: Int, visits: [Double])]) throws {
        try fm.createDirectory(atPath: "\(root)/\(profile)", withIntermediateDirectories: true)
        var db: OpaquePointer?
        defer { sqlite3_close(db) }
        try #require(sqlite3_open("\(root)/\(profile)/History", &db) == SQLITE_OK)
        var sql = """
            CREATE TABLE urls(id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER DEFAULT 0 NOT NULL,
                              typed_count INTEGER DEFAULT 0 NOT NULL, last_visit_time INTEGER NOT NULL, hidden INTEGER DEFAULT 0 NOT NULL);
            CREATE INDEX urls_url_index ON urls(url);
            CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER NOT NULL, visit_time INTEGER NOT NULL);
            """
        for (id, entry) in pages.sorted(by: { $0.key < $1.key }).enumerated() {
            let last = entry.value.visits.max() ?? 0
            sql += "INSERT INTO urls(id, url, visit_count, last_visit_time) VALUES(\(id + 1), '\(entry.key)', \(entry.value.count), \(chromiumTime(last)));"
            for time in entry.value.visits { sql += "INSERT INTO visits(url, visit_time) VALUES(\(id + 1), \(chromiumTime(time)));" }
        }
        try #require(sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK)
    }

    private let jan1 = 1_767_225_600.0   // 2026-01-01 00:00 UTC

    @Test func giveTheFirstVisitAndTheVisitCount() throws {
        try withFolder { root in
            try makeHistory(in: root, pages: ["https://a.com/p": (7, [jan1 + 500, jan1, jan1 + 9_000])])
            let found = HistoryLookup.lookup(["https://a.com/p", "https://a.com/other"], in: HistoryLookup.databases(in: root))
            #expect(found == ["https://a.com/p": HistoryLookup.Result(firstVisit: Date(timeIntervalSince1970: jan1), visitCount: 7)])
        }
    }

    @Test func wwwAndTrailingSlashSpellingsAreTriedWhenThereIsNoExactMatch() throws {
        try withFolder { root in
            try makeHistory(in: root, pages: ["https://www.a.com/p/": (2, [jan1]), "https://b.com/": (3, [jan1 + 60]),
                                              "https://www.c.com/p": (4, [jan1 + 120])])
            let links = ["https://a.com/p", "https://www.b.com", "https://c.com/p/"]
            let found = HistoryLookup.lookup(links, in: HistoryLookup.databases(in: root))
            #expect(found.mapValues(\.visitCount) == ["https://a.com/p": 2, "https://www.b.com": 3, "https://c.com/p/": 4])
        }
    }

    @Test func anExactMatchBeatsASpelling() throws {
        try withFolder { root in
            try makeHistory(in: root, pages: ["https://a.com/p": (1, [jan1 + 10]), "https://www.a.com/p": (9, [jan1])])
            #expect(HistoryLookup.lookup(["https://a.com/p"], in: HistoryLookup.databases(in: root))["https://a.com/p"]?.visitCount == 1)
        }
    }

    @Test func profilesCombineEarliestVisitAndAllVisits() throws {
        try withFolder { root in
            try makeHistory(in: root, pages: ["https://a.com/p": (2, [jan1 + 100])])
            try makeHistory(in: root, profile: "Profile 1", pages: ["https://a.com/p": (3, [jan1])])
            let found = HistoryLookup.lookup(["https://a.com/p"], in: HistoryLookup.databases(in: root))
            #expect(found["https://a.com/p"] == HistoryLookup.Result(firstVisit: Date(timeIntervalSince1970: jan1), visitCount: 5))
        }
    }

    @Test func aMissingOrUnreadableFileAddsNothing() throws {
        try withFolder { root in
            #expect(HistoryLookup.databases(in: root).isEmpty)
            #expect(HistoryLookup.lookup(["https://a.com/"], in: ["\(root)/Default/History"]).isEmpty)
            try fm.createDirectory(atPath: "\(root)/Default", withIntermediateDirectories: true)
            try Data("not a database".utf8).write(to: URL(fileURLWithPath: "\(root)/Default/History"))
            #expect(HistoryLookup.lookup(["https://a.com/"], in: HistoryLookup.databases(in: root)).isEmpty)
        }
    }

    @Test func theFileIsNeverChanged() throws {
        try withFolder { root in
            try makeHistory(in: root, pages: ["https://a.com/p": (1, [jan1])])
            let path = "\(root)/Default/History"
            let before = try Data(contentsOf: URL(fileURLWithPath: path))
            _ = HistoryLookup.lookup(["https://a.com/p"], in: [path])
            #expect(try Data(contentsOf: URL(fileURLWithPath: path)) == before)
            #expect(try fm.contentsOfDirectory(atPath: "\(root)/Default") == ["History"])   // no journal or lock files
        }
    }
}
