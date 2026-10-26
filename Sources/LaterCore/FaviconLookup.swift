import Foundation
import SQLite3

/// Site icons from a Chromium browser's own icon cache: each profile's `Favicons` file, opened read-only and immutable
/// (no locks, no journal, nothing written). It's asked only about saved pages' hosts and never lists the cache.
public enum FaviconLookup {
    public enum Outcome: Equatable, Sendable {
        /// PNG bytes of the host's icon, its largest bitmap.
        case found(Data)
        /// No profile has an icon for the host.
        case missing
        /// A read failed, as an immutable read can while the browser writes; ask again later.
        case retryLater
    }

    /// The `file` (`Favicons`, or `History`) of each of a Chromium data folder's profiles: Default, then Profile 1, Profile 2, …
    public static func databases(in folder: String, file: String = "Favicons") -> [String] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder)) ?? []
        return names.filter { $0 == "Default" || $0.hasPrefix("Profile ") }
            .sorted { $0 == "Default" || ($1 != "Default" && $0.localizedStandardCompare($1) == .orderedAscending) }
            .map { "\(folder)/\($0)/\(file)" }
            .filter { FileManager.default.fileExists(atPath: $0) }
    }

    /// Each host's icon: one of its saved pages' own, else its home page's, else the icon most of the host's pages use,
    /// with or without `www.`. `pages` maps a host to its saved links, newest first. The first profile with an icon wins.
    /// A bitmap `accept` turns down (one that won't decode) is passed over for the next one: a smaller size, then the
    /// next fallback, then the next profile.
    public static func icons(for pages: [String: [String]], in databases: [String],
                             accept: (Data) -> Bool = { _ in true }) -> [String: Outcome] {
        var outcomes = pages.mapValues { _ in Outcome.missing }
        for path in databases {
            let open = outcomes.filter { $0.value == .missing || $0.value == .retryLater }.keys
            guard !open.isEmpty else { break }
            do {
                let database = try Database(path: path)
                for host in open {
                    if let data = try database.icon(host: host, pages: pages[host] ?? [], accept: accept) {
                        outcomes[host] = .found(data)
                    }
                }
            } catch let error as SQLiteError where error.isTransient {
                for host in open where outcomes[host] == .missing { outcomes[host] = .retryLater }
            } catch {
                continue   // not a usable icon cache (no such file, another schema): it has no icons to give
            }
        }
        return outcomes
    }
}

struct SQLiteError: Error {
    let code: Int32

    /// Failures a read can hit while the browser is writing the file, which pass once it's done.
    var isTransient: Bool {
        [SQLITE_BUSY, SQLITE_LOCKED, SQLITE_IOERR, SQLITE_CORRUPT, SQLITE_PROTOCOL, SQLITE_SCHEMA, SQLITE_NOTADB]
            .contains(code & 0xff)
    }
}

/// One profile's icon cache, open for one lookup. Closed when released, also when opening it failed halfway.
private final class Database {
    private var db: OpaquePointer?
    private var exact: OpaquePointer?
    private var site: OpaquePointer?

    init(path: String) throws {
        let uri = URL(fileURLWithPath: path).absoluteString + "?immutable=1&mode=ro"
        let opened = sqlite3_open_v2(uri, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil)
        guard opened == SQLITE_OK else { throw SQLiteError(code: opened) }
        exact = try prepare("""
            SELECT b.image_data FROM icon_mapping m JOIN favicon_bitmaps b ON b.icon_id = m.icon_id
            WHERE m.page_url = ?1 AND b.width > 0 AND length(b.image_data) > 0
            ORDER BY b.width DESC
            """)
        // The icon most of the host's pages map to, with and without www. counted together, then its largest bitmap.
        // The ranges keep to the page_url index.
        site = try prepare("""
            SELECT b.image_data FROM favicon_bitmaps b JOIN (
                SELECT icon_id, count(*) AS pages FROM icon_mapping
                WHERE (page_url >= ?1 AND page_url < ?2) OR (page_url >= ?3 AND page_url < ?4) GROUP BY icon_id
            ) m ON b.icon_id = m.icon_id
            WHERE b.width > 0 AND length(b.image_data) > 0
            ORDER BY m.pages DESC, b.width DESC
            """)
    }

    deinit {
        sqlite3_finalize(exact)
        sqlite3_finalize(site)
        sqlite3_close_v2(db)
    }

    func icon(host: String, pages: [String], accept: (Data) -> Bool) throws -> Data? {
        let scheme = pages.lazy.compactMap { URL(string: $0)?.scheme }.first ?? "https"
        let names = [host, "www." + host]
        // A saved page's own icon, then the site's home page's: its usual icon, where a page here and there can carry
        // a variant (a badge, a player state) that even most of the host's pages share.
        for page in pages.prefix(3) + names.map({ "\(scheme)://\($0)/" }) {
            if let data = try first(exact, accept, page) { return data }
        }
        // Every link on the host sorts between "https://host/" and "https://host0" ("0" follows "/").
        return try first(site, accept, names.flatMap { ["\(scheme)://\($0)/", "\(scheme)://\($0)0"] })
    }

    private func first(_ statement: OpaquePointer?, _ accept: (Data) -> Bool, _ values: String...) throws -> Data? {
        try first(statement, accept, values)
    }

    /// The first bitmap the statement finds that `accept` takes.
    private func first(_ statement: OpaquePointer?, _ accept: (Data) -> Bool, _ values: [String]) throws -> Data? {
        defer {
            sqlite3_reset(statement)
            sqlite3_clear_bindings(statement)
        }
        for (i, value) in values.enumerated() {
            sqlite3_bind_text(statement, Int32(i + 1), value, -1, Self.transient)
        }
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                guard let bytes = sqlite3_column_blob(statement, 0) else { continue }
                let data = Data(bytes: bytes, count: Int(sqlite3_column_bytes(statement, 0)))
                if accept(data) { return data }
            case SQLITE_DONE:
                return nil
            case let code:
                throw SQLiteError(code: code)
            }
        }
    }

    private func prepare(_ sql: String) throws -> OpaquePointer? {
        var statement: OpaquePointer?
        let code = sqlite3_prepare_v2(db, sql, -1, &statement, nil)
        guard code == SQLITE_OK else { throw SQLiteError(code: code) }
        return statement
    }

    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
}
