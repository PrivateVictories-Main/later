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
