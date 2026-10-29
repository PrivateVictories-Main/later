import Foundation
import SQLite3

/// When a page was first opened and how often, from a Chromium browser's own `History` file: each profile's, opened
/// read-only and immutable like the icon cache (no locks, no journal, nothing written), only to ask about saved pages'
/// links, never to list the history. Opened for one batch and closed right after.
public enum HistoryLookup {
    public struct Result: Equatable, Sendable {
        /// The earliest visit to the page, in any profile.
        public var firstVisit: Date
        /// The browser's visit count for the page, all profiles together.
        public var visitCount: Int

        public init(firstVisit: Date, visitCount: Int) {
            self.firstVisit = firstVisit
            self.visitCount = visitCount
        }
    }

    /// The `History` files of a Chromium data folder's profiles.
    public static func databases(in folder: String) -> [String] { FaviconLookup.databases(in: folder, file: "History") }

    /// For each link, what the history says about it; links it has never seen are left out. The link itself is looked up
    /// first, then its `www.`/bare and trailing-slash spellings. A file that can't be read (missing, locked while the
    /// browser writes, another schema) adds nothing.
    public static func lookup(_ links: [String], in databases: [String]) -> [String: Result] {
        var results: [String: Result] = [:]
        for path in databases {
            guard let database = try? Database(path: path) else { continue }
            for link in links {
                guard let found = try? database.first(of: variants(of: link)) else { continue }
                if var known = results[link] {
                    known.firstVisit = min(known.firstVisit, found.firstVisit)
                    known.visitCount += found.visitCount
                    results[link] = known
                } else {
                    results[link] = found
                }
            }
        }
        return results
    }

    /// On which days (see `Familiarity.dayNumber`, in the user's own time zone) each site was visited since `since`, from the
    /// same read-only, immutable history files: one question, asked once a day. Sites are hosts without `www.`; only web
    /// pages count, and a frame loaded inside a page does not. Visits are grouped in 15-minute steps by the database, so a
    /// busy history costs a few rows per page. Nothing is written, and a file that can't be read adds nothing.
    public static func visitDays(in databases: [String], since: Date, calendar: Calendar = .current) -> [String: Set<Int>] {
        let step: Int64 = 900_000_000   // 15 minutes in microseconds: every time zone's midnight falls on a step
        let from = Int64((since.timeIntervalSince1970 + 11_644_473_600) * 1_000_000)
        var days: [String: Set<Int>] = [:]
        var dayOfStep: [Int64: Int] = [:]
        var siteOfLink: [String: String?] = [:]
        for path in databases {
            var db: OpaquePointer?
            var query: OpaquePointer?
            defer {
                sqlite3_finalize(query)
                sqlite3_close_v2(db)
            }
            let uri = URL(fileURLWithPath: path).absoluteString + "?immutable=1&mode=ro"
            guard sqlite3_open_v2(uri, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK,
                  sqlite3_prepare_v2(db, """
                      SELECT u.url, v.visit_time / ?2 FROM visits v JOIN urls u ON u.id = v.url
                      WHERE v.visit_time >= ?1 AND (v.transition & 255) NOT IN (3, 4) GROUP BY v.url, v.visit_time / ?2
                      """, -1, &query, nil) == SQLITE_OK else { continue }
            sqlite3_bind_int64(query, 1, from)
            sqlite3_bind_int64(query, 2, step)
            while sqlite3_step(query) == SQLITE_ROW {
                guard let text = sqlite3_column_text(query, 0) else { continue }
                let link = String(cString: text)
                let site: String?
                if let known = siteOfLink[link] { site = known } else {
                    site = Self.site(ofLink: link)
                    siteOfLink[link] = .some(site)
                }
                guard let site else { continue }
                let bucket = sqlite3_column_int64(query, 1)
                let day: Int
                if let known = dayOfStep[bucket] { day = known } else {
                    day = Familiarity.dayNumber(of: date(chromiumTime: bucket * step), calendar: calendar)
                    dayOfStep[bucket] = day
                }
                days[site, default: []].insert(day)
            }
        }
        return days
    }

    /// The host of a web link without `www.`, lowercased; nil for anything that isn't http or https.
    static func site(ofLink link: String) -> String? {
        guard let marker = link.range(of: "://"), ["http", "https"].contains(link[..<marker.lowerBound].lowercased()) else { return nil }
        var host = link[marker.upperBound...].prefix { !"/?#".contains($0) }
        if let at = host.lastIndex(of: "@") { host = host[host.index(after: at)...] }
        if let colon = host.firstIndex(of: ":") { host = host[..<colon] }
        let site = host.lowercased()
        let bare = site.hasPrefix("www.") ? String(site.dropFirst(4)) : site
        return bare.isEmpty ? nil : bare
    }

    /// The link as written, then with the slash toggled, then the same two on the other `www.` spelling.
    static func variants(of link: String) -> [String] {
        guard var parts = URLComponents(string: link), let host = parts.host else { return [link] }
        var spellings = [link]
        func toggledSlash(_ text: String) -> String { text.hasSuffix("/") ? String(text.dropLast()) : text + "/" }
        if parts.query == nil, parts.fragment == nil { spellings.append(toggledSlash(link)) }
        parts.host = host.hasPrefix("www.") ? String(host.dropFirst(4)) : "www." + host
        if let other = parts.string {
            spellings.append(other)
            if parts.query == nil, parts.fragment == nil { spellings.append(toggledSlash(other)) }
        }
        return spellings
    }

    /// Chromium counts microseconds from 1601-01-01.
    static func date(chromiumTime micros: Int64) -> Date {
        Date(timeIntervalSince1970: Double(micros) / 1_000_000 - 11_644_473_600)
    }
}

/// One profile's history, open for one batch. Closed when released, also when opening it failed halfway.
private final class Database {
    private var db: OpaquePointer?
    private var page: OpaquePointer?

    init(path: String) throws {
        let uri = URL(fileURLWithPath: path).absoluteString + "?immutable=1&mode=ro"
        let opened = sqlite3_open_v2(uri, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil)
        guard opened == SQLITE_OK else { throw SQLiteError(code: opened) }
        let code = sqlite3_prepare_v2(db, """
            SELECT u.visit_count, COALESCE((SELECT MIN(v.visit_time) FROM visits v WHERE v.url = u.id), u.last_visit_time)
            FROM urls u WHERE u.url = ?1
            """, -1, &page, nil)
        guard code == SQLITE_OK else { throw SQLiteError(code: code) }
    }

    deinit {
        sqlite3_finalize(page)
        sqlite3_close_v2(db)
    }

    /// The first spelling the history knows.
    func first(of spellings: [String]) throws -> HistoryLookup.Result? {
        for spelling in spellings {
            defer {
                sqlite3_reset(page)
                sqlite3_clear_bindings(page)
            }
            sqlite3_bind_text(page, 1, spelling, -1, unsafeBitCast(-1, to: sqlite3_destructor_type.self))
            switch sqlite3_step(page) {
            case SQLITE_ROW:
                return HistoryLookup.Result(firstVisit: HistoryLookup.date(chromiumTime: sqlite3_column_int64(page, 1)),
                                            visitCount: Int(sqlite3_column_int(page, 0)))
            case SQLITE_DONE:
                continue
            case let code:
                throw SQLiteError(code: code)
            }
        }
        return nil
    }
}
