import Foundation

public enum Browser: String, Codable, CaseIterable, Sendable {
    case helium, chrome

    /// Other builds of Helium to watch, listed in the app's Info.plist by `scripts/build-app.sh`; none in a normal build or in tests.
    private static let extraHeliumIDs = Bundle.main.object(forInfoDictionaryKey: "LaterExtraHeliumBundleIDs") as? [String] ?? []

    public var bundleIDs: [String] {
        switch self {
        case .helium: ["net.imput.helium"] + Self.extraHeliumIDs
        case .chrome: ["com.google.Chrome"]
        }
    }

    public static func matching(bundleID: String?) -> Browser? {
        guard let bundleID else { return nil }
        return allCases.first { $0.bundleIDs.contains(bundleID) }
    }

    public var displayName: String {
        switch self {
        case .helium: "Helium"
        case .chrome: "Chrome"
        }
    }

    /// The browser's data folder, relative to the user's real home folder.
    public var dataFolder: String {
        switch self {
        case .helium: "Library/Application Support/net.imput.helium"
        case .chrome: "Library/Application Support/Google/Chrome"
        }
    }
}

/// One open tab as the browser reports it. Tabs from incognito windows never become a `Tab`.
public struct Tab: Equatable, Sendable {
    public var url: String
    public var title: String
    /// The active tab of its window.
    public var isActive: Bool
    /// The window's position, front window first (0), counting incognito windows too.
    public var windowIndex: Int
    /// The tab's place in its window's tab strip, left to right from 0.
    public var tabIndex: Int
    /// The browser's own id for the window; ids grow as windows are made, so they rank windows roughly by age.
    public var windowID: Int

    public init(url: String, title: String, isActive: Bool, windowIndex: Int, tabIndex: Int = 0, windowID: Int = 0) {
        self.url = url
        self.title = title
        self.isActive = isActive
        self.windowIndex = windowIndex
        self.tabIndex = tabIndex
        self.windowID = windowID
    }
}

public struct Snapshot: Equatable, Sendable {
    public var browser: Browser
    public var tabs: [Tab]
    /// Windows skipped because their mode was neither "normal" nor "incognito".
    public var skippedWindows: Int

    public init(browser: Browser, tabs: [Tab], skippedWindows: Int = 0) {
        self.browser = browser
        self.tabs = tabs
        self.skippedWindows = skippedWindows
    }

    public enum AssembleError: Error, Equatable { case inconsistent, unrecognizedMode }

    /// Builds a snapshot window by window, front window first; `windowIndex` is a window's position in `windowIDs`.
    /// A window's mode is fixed for its lifetime, so looking mode and tabs up by id can't pair them wrongly,
    /// and incognito windows' tabs are never requested, so they never reach this process.
    /// A window with any other mode is skipped like incognito (its tabs are never requested) and counted,
    /// but if every window is skipped the read fails, so a changed mode format shows instead of reading nothing.
    public static func collect(browser: Browser, windowIDs: [Int], mode: (Int) throws -> String,
                               tabs: (Int) throws -> WindowTabs) throws -> Snapshot {
        var result: [Tab] = []
        var skipped = 0
        for (window, id) in windowIDs.enumerated() {
            switch try mode(id) {
            case "normal": break
            case "incognito": continue
            default: skipped += 1; continue   // fail closed: an unknown window might be private
            }
            let w = try tabs(id)
            guard w.urls.count == w.titles.count else { throw AssembleError.inconsistent }
            for (i, url) in w.urls.enumerated() {
                result.append(Tab(url: url, title: w.titles[i], isActive: i + 1 == w.activeIndex, windowIndex: window,
                                  tabIndex: i, windowID: id))
            }
        }
        if skipped > 0 && skipped == windowIDs.count { throw AssembleError.unrecognizedMode }
        return Snapshot(browser: browser, tabs: result, skippedWindows: skipped)
    }

    /// Like `collect`, for window ids as the browser sent them (text). The closures take a window's position in `ids`; each
    /// tab's `windowID` is the window's real id, so ranking windows by it follows their age, not which one is in front.
    public static func collect(browser: Browser, windowIDTexts ids: [String], mode: (Int) throws -> String,
                               tabs: (Int) throws -> WindowTabs) throws -> Snapshot {
        let keys = windowKeys(ids)
        var snapshot = try collect(browser: browser, windowIDs: Array(ids.indices), mode: mode, tabs: tabs)
        for i in snapshot.tabs.indices { snapshot.tabs[i].windowID = keys[snapshot.tabs[i].windowID] }
        return snapshot
    }

    /// A sortable number per window id: the id itself when every id is a number, else the id's place in text order.
    static func windowKeys(_ ids: [String]) -> [Int] {
        let numbers = ids.map { Int($0.trimmingCharacters(in: .whitespaces)) }
        if numbers.allSatisfy({ $0 != nil }) { return numbers.map { $0! } }
        let order = ids.sorted()
        return ids.map { order.firstIndex(of: $0)! }
    }
}

/// One normal window's tabs, as read from the browser. `activeIndex` is 1-based, as AppleScript reports it.
public struct WindowTabs: Equatable, Sendable {
    public var activeIndex: Int
    public var urls: [String]
    public var titles: [String]

    public init(activeIndex: Int, urls: [String], titles: [String]) {
        self.activeIndex = activeIndex
        self.urls = urls
        self.titles = titles
    }
}

public enum Kind: String, Codable, CaseIterable, Sendable, Identifiable {
    case shopping, posts, school, reading, videos, tools, other

    public var id: String { rawValue }

    /// A kind this build doesn't know (a newer build's) reads as Other, so the list still opens.
    public init(from decoder: any Decoder) throws {
        self = Kind(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .other
    }

    public var title: String {
        switch self {
        case .shopping: "Shopping"
        case .posts: "Posts"
        case .school: "School"
        case .reading: "Reading"
        case .videos: "Videos"
        case .tools: "Tools"
        case .other: "Other"
        }
    }

    public var symbol: String {
        switch self {
        case .shopping: "bag"
        case .posts: "bubble.left.and.bubble.right"
        case .school: "graduationcap"
        case .reading: "book"
        case .videos: "play.rectangle"
        case .tools: "wrench.and.screwdriver"
        case .other: "square.grid.2x2"
        }
    }
}

public struct Verdict: Equatable, Sendable {
    public var isSpecific: Bool
    public var kind: Kind
    /// Host plus path shape, e.g. "x.com/*/status/*". Corrections are stored per page type.
    public var pageType: String
    /// A site's home, a dashboard, a feed or a plain section page: somewhere you go, not a thing. See `Familiarity`.
    public var isPlace: Bool

    public init(isSpecific: Bool, kind: Kind, pageType: String, isPlace: Bool = false) {
        self.isSpecific = isSpecific
        self.kind = kind
        self.pageType = pageType
        self.isPlace = isPlace
    }
}

/// The user's correction for one page type. Nil fields mean "no correction".
public struct Correction: Codable, Equatable, Sendable {
    public var isSpecific: Bool?
    public var kind: Kind?
    /// "This is a regular" (true) or its undoing (false) for a place page type; nil: left to `Familiarity`. Absent in older files.
    public var regular: Bool?

    public init(isSpecific: Bool? = nil, kind: Kind? = nil, regular: Bool? = nil) {
        self.isSpecific = isSpecific
        self.kind = kind
        self.regular = regular
    }
}

public struct Item: Codable, Identifiable, Equatable, Sendable {
    public enum Status: String, Codable, Sendable { case active, done }

    public var id: UUID
    public var url: String
    public var title: String
    public var host: String
    public var kind: Kind
    public var pageType: String
    public var browser: Browser
    public var firstSaved: Date
    public var lastSeen: Date
    public var visits: Int
    public var status: Status
    public var doneAt: Date?
    /// When the page was first opened, from the browser's own history (Helium only). Nil: not known, so `opened` is
    /// when Later first saw it. Absent in older files.
    public var openedAt: Date?
    /// How many times the browser's history says the page was visited. Absent in older files.
    public var historyVisits: Int?
    /// Where the page's tab last was: its place in the strip (left to right from 0) and its window's rank (0 is the oldest
    /// window). Kept after the tab closes, but only open pages are ordered by it. Absent in older files.
    public var tabIndex: Int?
    public var windowRank: Int?
    /// The link the browser's history was asked about at launch without finding the page, so it is asked again only if
    /// the link changes. Absent in older files.
    public var historyChecked: String?
    /// For a place page (a site's home, a dashboard, a feed): true when the site is a regular one, so the page shows only
    /// under Regulars; false when it is kept in the list. Nil for a page that is a thing. Absent in older files.
    public var regular: Bool?

    /// When the page was opened, else when Later first saw it.
    public var opened: Date { openedAt ?? firstSaved }
    /// The larger of what Later counted and what the browser's history says.
    public var totalVisits: Int { max(visits, historyVisits ?? 0) }
}

public struct LibraryData: Codable, Equatable, Sendable {
    public var version = 1
    public var items: [Item] = []
    public var corrections: [String: Correction] = [:]
    /// SHA-256 of cleaned links of specific pages seen recently → when. Never links or titles.
    public var seen: [String: Date] = [:]
    /// Seconds on screen per specific page, newest last, at most 200.
    public var dwellSamples: [Double] = []
    /// SHA-256 of cleaned links the user deleted → when; kept until the page's tab closes. Absent in older files.
    public var dismissed: [String: Date] = [:]
    /// SHA-256 of a site's host (without "www.") → the days (see `Familiarity.dayNumber`) of the last 28 on which a read
    /// found it open in a tab. Never hostnames. Absent in older files, which ignore it.
    public var siteDays: [String: [Int]] = [:]

    public init() {}

    private enum CodingKeys: String, CodingKey { case version, items, corrections, seen, dwellSamples, dismissed, siteDays }

    /// Like the synthesized one, but `siteDays` is only written once there is something in it: a list that never noted a
    /// site stays byte-for-byte what an older build wrote.
    public func encode(to encoder: any Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(version, forKey: .version)
        try c.encode(items, forKey: .items)
        try c.encode(corrections, forKey: .corrections)
        try c.encode(seen, forKey: .seen)
        try c.encode(dwellSamples, forKey: .dwellSamples)
        try c.encode(dismissed, forKey: .dismissed)
        if !siteDays.isEmpty { try c.encode(siteDays, forKey: .siteDays) }
    }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decode(Int.self, forKey: .version)
        items = try c.decode([Item].self, forKey: .items)
        corrections = try c.decode([String: Correction].self, forKey: .corrections)
        seen = try c.decode([String: Date].self, forKey: .seen)
        dwellSamples = try c.decode([Double].self, forKey: .dwellSamples)
        dismissed = try c.decodeIfPresent([String: Date].self, forKey: .dismissed) ?? [:]
        siteDays = try c.decodeIfPresent([String: [Int]].self, forKey: .siteDays) ?? [:]
    }
}
