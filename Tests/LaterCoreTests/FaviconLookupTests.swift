import Foundation
import SQLite3
import Testing
@testable import LaterCore

@Suite struct FaviconLookupTests {
    private let fm = FileManager.default

    /// Runs `body` with a fresh temp folder standing in for Helium's data folder, always removed afterwards.
    private func withFolder(_ body: (String) throws -> Void) throws {
        let root = fm.temporaryDirectory.appendingPathComponent("LaterFavicons-\(UUID().uuidString)").path
        defer { try? fm.removeItem(atPath: root) }
        try fm.createDirectory(atPath: root, withIntermediateDirectories: true)
        try body(root)
    }

    /// A profile's icon cache with Chromium's tables. `icons` names each icon's bitmaps (width → stand-in PNG bytes);
    /// `pages` maps page links to icon names.
    @discardableResult
    private func makeCache(in root: String, profile: String = "Default", icons: [String: [Int: String]],
                           pages: [String: String]) throws -> String {
        try fm.createDirectory(atPath: "\(root)/\(profile)", withIntermediateDirectories: true)
        let path = "\(root)/\(profile)/Favicons"
        try? fm.removeItem(atPath: path)
        var db: OpaquePointer?
        defer { sqlite3_close(db) }
        try #require(sqlite3_open(path, &db) == SQLITE_OK)
        var sql = """
            CREATE TABLE icon_mapping(id INTEGER PRIMARY KEY, page_url LONGVARCHAR NOT NULL, icon_id INTEGER);
            CREATE INDEX icon_mapping_page_url_idx ON icon_mapping(page_url);
            CREATE TABLE favicons(id INTEGER PRIMARY KEY, url LONGVARCHAR NOT NULL, icon_type INTEGER DEFAULT 1);
            CREATE TABLE favicon_bitmaps(id INTEGER PRIMARY KEY, icon_id INTEGER NOT NULL, image_data BLOB,
                                         width INTEGER DEFAULT 0, height INTEGER DEFAULT 0);
            """
        let ids = Dictionary(uniqueKeysWithValues: icons.keys.sorted().enumerated().map { ($1, $0 + 1) })
        for (name, bitmaps) in icons {
            sql += "INSERT INTO favicons(id, url) VALUES(\(ids[name]!), 'https://icons.example/\(name)');"
            for (width, bytes) in bitmaps {
                sql += "INSERT INTO favicon_bitmaps(icon_id, image_data, width, height) VALUES(\(ids[name]!), CAST('\(bytes)' AS BLOB), \(width), \(width));"
            }
        }
        for (page, name) in pages { sql += "INSERT INTO icon_mapping(page_url, icon_id) VALUES('\(page)', \(ids[name]!));" }
        try #require(sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK)
        return path
    }

    private func lookUp(_ host: String, _ pages: [String], root: String) -> FaviconLookup.Outcome? {
        FaviconLookup.icons(for: [host: pages], in: FaviconLookup.databases(in: root))[host]
    }

    private func found(_ text: String) -> FaviconLookup.Outcome { .found(Data(text.utf8)) }

    @Test func anExactPageMatchComesFirst() throws {
        try withFolder { root in
            try makeCache(in: root, icons: ["shop": [32: "shop32"], "home": [32: "home32"]],
                          pages: ["https://www.a.com/p/1": "shop", "https://www.a.com/": "home", "https://www.a.com/x": "home"])
            #expect(lookUp("a.com", ["https://www.a.com/p/1"], root: root) == found("shop32"))
        }
    }

    @Test func otherwiseTheIconMostOfTheHostsPagesUse() throws {
        try withFolder { root in
            try makeCache(in: root, icons: ["one": [32: "one32"], "two": [32: "two32"], "near": [32: "near32"]],
                          pages: ["https://b.com/1": "one", "https://b.com/2": "two", "https://b.com/3": "two",
                                  // neighbours in sort order that aren't b.com, so the range must leave them out
                                  "https://b.company.com/1": "near", "https://b.company.com/2": "near",
                                  "https://b.company.com/3": "near", "https://b.co/": "near"])
            #expect(lookUp("b.com", ["https://b.com/not-in-the-cache"], root: root) == found("two32"))
        }
    }

    @Test func theHomePagesIconBeatsAVariantMostPagesShare() throws {
        try withFolder { root in
            try makeCache(in: root, icons: ["home": [32: "home32"], "variant": [32: "variant32"]],
                          pages: ["https://www.e.com/": "home", "https://www.e.com/watch?v=1": "variant",
                                  "https://www.e.com/watch?v=2": "variant", "https://www.e.com/watch?v=3": "variant"])
            #expect(lookUp("e.com", ["https://www.e.com/watch?v=9"], root: root) == found("home32"))
        }
    }

    @Test func findsTheHostWithOrWithoutWww() throws {
        try withFolder { root in
            try makeCache(in: root, icons: ["rei": [32: "rei32"], "plain": [16: "plain16"]],
                          pages: ["https://www.rei.com/c/jackets": "rei", "https://plain.org/a": "plain"])
            #expect(lookUp("rei.com", ["https://rei.com/product/1"], root: root) == found("rei32"))
            #expect(lookUp("plain.org", ["https://www.plain.org/b"], root: root) == found("plain16"))
            #expect(lookUp("other.com", ["https://www.other.com/"], root: root) == .missing)
        }
    }

    /// The most-used icon counts the host's pages with and without www. together, not one name before the other.
    @Test func theMostUsedIconCountsBothNamesTogether() throws {
        try withFolder { root in
            try makeCache(in: root, icons: ["bare": [32: "bare32"], "www": [32: "www32"]],
                          pages: ["https://f.com/1": "bare", "https://f.com/2": "www",
                                  "https://www.f.com/3": "www", "https://www.f.com/4": "www"])
            #expect(lookUp("f.com", ["https://f.com/not-in-the-cache"], root: root) == found("www32"))
        }
    }

    /// A bitmap that won't decode is passed over: for a smaller one, then the next fallback, then the next profile.
    @Test func aBitmapThatWontDecodeFallsThroughToTheNextCandidate() throws {
        try withFolder { root in
            try makeCache(in: root, icons: ["sizes": [64: "bad", 16: "sizes16"], "page": [32: "bad"], "home": [32: "home32"],
                                            "only": [32: "bad"]],
                          pages: ["https://g.com/": "sizes", "https://h.com/p": "page", "https://h.com/": "home",
                                  "https://k.com/": "only"])
            try makeCache(in: root, profile: "Profile 1", icons: ["good": [32: "k32"]], pages: ["https://k.com/": "good"])
            let databases = FaviconLookup.databases(in: root)
            let icons = FaviconLookup.icons(for: ["g.com": ["https://g.com/"], "h.com": ["https://h.com/p"],
                                                  "k.com": ["https://k.com/"], "m.com": ["https://m.com/"]],
                                            in: databases) { $0 != Data("bad".utf8) }
            #expect(icons == ["g.com": found("sizes16"), "h.com": found("home32"), "k.com": found("k32"), "m.com": .missing])
            let allBad = FaviconLookup.icons(for: ["h.com": ["https://h.com/p"]], in: databases) { _ in false }
            #expect(allBad == ["h.com": .missing])
        }
    }

    @Test func picksTheLargestBitmapAndSkipsPlaceholdersAndEmptyOnes() throws {
        try withFolder { root in
            try makeCache(in: root,
                          icons: ["sizes": [0: "placeholder", 16: "sizes16", 32: "sizes32"],
                                  "blank32": [16: "blank16", 32: ""],
                                  "only0": [0: "placeholder"]],
                          pages: ["https://sizes.com/": "sizes", "https://blank.com/": "blank32", "https://zero.com/": "only0"])
            #expect(lookUp("sizes.com", ["https://sizes.com/"], root: root) == found("sizes32"))
            #expect(lookUp("blank.com", ["https://blank.com/"], root: root) == found("blank16"))
            #expect(lookUp("zero.com", ["https://zero.com/"], root: root) == .missing)
        }
    }

    @Test func searchesEveryProfileInOrder() throws {
        try withFolder { root in
            try makeCache(in: root, icons: ["x": [16: "default16"]], pages: ["https://elsewhere.com/": "x"])
            try makeCache(in: root, profile: "Profile 10", icons: ["x": [32: "ten32"]], pages: ["https://c.com/": "x"])
            try makeCache(in: root, profile: "Profile 2", icons: ["x": [32: "two32"]], pages: ["https://c.com/": "x"])
            try makeCache(in: root, profile: "System Profile", icons: ["x": [32: "system32"]], pages: ["https://c.com/": "x"])
            try fm.createDirectory(atPath: root + "/Profile 1", withIntermediateDirectories: true)   // no icon cache yet
            #expect(FaviconLookup.databases(in: root).map { $0.dropFirst(root.count) }
                    == ["/Default/Favicons", "/Profile 2/Favicons", "/Profile 10/Favicons"])
            #expect(lookUp("c.com", ["https://c.com/"], root: root) == found("two32"))
        }
    }

    @Test func aMissingCacheHasNoIcons() throws {
        try withFolder { root in
            #expect(FaviconLookup.databases(in: root + "/nothing-here").isEmpty)
            #expect(FaviconLookup.icons(for: ["a.com": ["https://a.com/"]], in: [root + "/Default/Favicons"]) == ["a.com": .missing])
            #expect(FaviconLookup.icons(for: ["a.com": ["https://a.com/"]], in: []) == ["a.com": .missing])
        }
    }

    /// An immutable read during the browser's write can see a damaged file. That's "try later", and the next try works.
    @Test func aDamagedReadIsTriedAgainLater() throws {
        try withFolder { root in
            let path = try makeCache(in: root, icons: ["d": [32: "d32"]], pages: ["https://d.com/": "d"])
            let good = try Data(contentsOf: URL(fileURLWithPath: path))
            try #require(good.count > 4096)
            // Tables intact on the first page, their contents garbage.
            try (good.prefix(4096) + Data(repeating: 0xAB, count: good.count - 4096)).write(to: URL(fileURLWithPath: path))
            #expect(lookUp("d.com", ["https://d.com/"], root: root) == .retryLater)
            try Data(repeating: 0xCD, count: good.count).write(to: URL(fileURLWithPath: path))   // not even a header
            #expect(lookUp("d.com", ["https://d.com/"], root: root) == .retryLater)
            try good.write(to: URL(fileURLWithPath: path))
            #expect(lookUp("d.com", ["https://d.com/"], root: root) == found("d32"))
            #expect(try fm.contentsOfDirectory(atPath: root + "/Default") == ["Favicons"])   // read-only: no journal, no lock files
        }
    }
}
