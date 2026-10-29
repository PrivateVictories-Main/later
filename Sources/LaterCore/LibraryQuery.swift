import Foundation

public enum LibrarySection: Hashable, Sendable {
    case all, kind(Kind), older, done, regulars

    func contains(_ item: Item, cutoff: Date) -> Bool {
        switch self {
        case .all: item.status == .active && item.lastSeen >= cutoff && item.regular != true
        case .kind(let kind): item.status == .active && item.kind == kind && item.lastSeen >= cutoff && item.regular != true
        case .older: item.status == .active && item.lastSeen < cutoff && item.regular != true
        case .done: item.status == .done && item.regular != true
        case .regulars: item.regular == true
        }
    }
}

/// How a list is grouped under its headings: by day (the default), by site or by kind.
public enum LibraryGrouping: String, CaseIterable, Sendable {
    case date, site, kind
}

/// What the list is ordered by. Dates run newest first and text A to Z unless the order is reversed; the two tab orders
/// are each other's reverse and ignore that.
public enum LibrarySort: String, CaseIterable, Sendable {
    case tabOrder, tabOrderReversed, dateOpened, dateSaved, lastSeen, title, site

    public var isTabOrder: Bool { self == .tabOrder || self == .tabOrderReversed }
}

public enum LibraryQuery {
    public static let olderAfter: TimeInterval = 14 * 86_400

    /// A section's items in `sort` order (newest first by default; `reversed` flips dates and text). A non-empty search
    /// looks across everything, active items first. `open` holds the pages open right now, for the tab orders.
    // shortcut: linear search over every item per keystroke; precompute lowercased text if Task 10 measures over 16 ms.
    public static func items(_ data: LibraryData, section: LibrarySection, search: String, now: Date,
                             sort: LibrarySort = .lastSeen, reversed: Bool = false, open: Set<UUID> = []) -> [Item] {
        let query = search.trimmingCharacters(in: .whitespaces)
        let cutoff = now.addingTimeInterval(-olderAfter)
        let picked = query.isEmpty
            ? data.items.filter { section.contains($0, cutoff: cutoff) }
            : data.items.filter {
                $0.title.localizedCaseInsensitiveContains(query) || $0.host.localizedCaseInsensitiveContains(query)
                    || $0.url.localizedCaseInsensitiveContains(query)
            }
        let precedes = ordering(sort, reversed: reversed, open: open)
        return picked.sorted { a, b in
            if a.status != b.status { return a.status == .active }
            return precedes(a, b)
        }
    }

    /// Whether the first item comes before the second under `sort`; equal items keep their places.
    static func ordering(_ sort: LibrarySort, reversed: Bool, open: Set<UUID>) -> (Item, Item) -> Bool {
        let newest = { (date: @escaping (Item) -> Date) in
            { (a: Item, b: Item) in reversed ? date(a) < date(b) : date(a) > date(b) }
        }
        switch sort {
        case .lastSeen: return newest(sortDate)
        case .dateOpened: return newest(\.opened)
        case .dateSaved: return newest(\.firstSaved)
        case .title:
            return { a, b in
                let order = a.title.localizedStandardCompare(b.title)
                return reversed ? order == .orderedDescending : order == .orderedAscending
            }
        case .site:
            return { a, b in
                let order = a.host.localizedStandardCompare(b.host)
                if order != .orderedSame { return reversed ? order == .orderedDescending : order == .orderedAscending }
                return sortDate(a) > sortDate(b)
            }
        case .tabOrder, .tabOrderReversed:
            let flip = sort == .tabOrderReversed
            let browsers = Browser.allCases
            let place = { (item: Item) in
                [browsers.firstIndex(of: item.browser) ?? 0, item.windowRank ?? .max, item.tabIndex ?? .max]
            }
            return { a, b in
                switch (open.contains(a.id), open.contains(b.id)) {
                case (true, false): return true
                case (false, true): return false
                case (false, false): return sortDate(a) > sortDate(b)   // closed pages: the latest first, either way
                case (true, true):
                    let (pa, pb) = (place(a), place(b))
                    // Right to left reverses the windows and the tabs, not the browsers' order.
                    for i in 0..<3 where pa[i] != pb[i] { return (i == 0 || !flip) ? pa[i] < pb[i] : pa[i] > pb[i] }
                    return false
                }
            }
        }
    }

    public static func count(_ data: LibraryData, section: LibrarySection, now: Date) -> Int {
        let cutoff = now.addingTimeInterval(-olderAfter)
        return data.items.reduce(0) { $0 + (section.contains($1, cutoff: cutoff) ? 1 : 0) }
    }

    /// Every section's count in one pass over the list, for the sidebar.
    public static func counts(_ data: LibraryData, now: Date) -> [LibrarySection: Int] {
        let cutoff = now.addingTimeInterval(-olderAfter)
        var counts: [LibrarySection: Int] = [:]
        for item in data.items {
            if item.regular == true {
                counts[.regulars, default: 0] += 1
            } else if item.status == .done {
                counts[.done, default: 0] += 1
            } else if item.lastSeen < cutoff {
                counts[.older, default: 0] += 1
            } else {
                counts[.all, default: 0] += 1
                counts[.kind(item.kind), default: 0] += 1
            }
        }
        return counts
    }

    /// Whether the list as drawn at `then` still looks the same at `now`: dates and headings go by the day, and the only
    /// other thing time changes is a page moving to Older, which changes the counts.
    public static func looksSame(_ data: LibraryData, at then: Date, as now: Date, calendar: Calendar = .current) -> Bool {
        calendar.isDate(then, inSameDayAs: now) && counts(data, now: then) == counts(data, now: now)
    }

    /// The date the list sorts, groups and shows an item by: when it was marked done, else when it was last seen.
    public static func sortDate(_ item: Item) -> Date {
        item.status == .done ? (item.doneAt ?? item.lastSeen) : item.lastSeen
    }

    /// Headings for items in `items(…)` order: Today, Yesterday, Previous 7 Days, Previous 30 Days, then one per
    /// month. A search mixes sections, so it groups by status instead: Saved, then Done.
    /// Dates go by the date the sort is by: when the page was opened, saved or last seen.
    public static func groups(_ items: [Item], searching: Bool, now: Date, sort: LibrarySort = .lastSeen,
                              reversed: Bool = false, calendar: Calendar = .current) -> [ItemGroup] {
        if searching {
            return [ItemGroup(title: "Saved", items: items.filter { $0.status == .active }),
                    ItemGroup(title: "Done", items: items.filter { $0.status == .done })].filter { !$0.items.isEmpty }
        }
        let today = calendar.startOfDay(for: now)
        let day = { (days: Int) in calendar.date(byAdding: .day, value: -days, to: today)! }
        let named = [(today, "Today"), (day(1), "Yesterday"), (day(7), "Previous 7 Days"), (day(30), "Previous 30 Days")]
        let year = calendar.component(.year, from: now)
        var groups: [ItemGroup] = []
        var month: (start: Date, end: Date, title: String)?   // items come newest first, so a month is looked up once
        for item in items {
            let date = groupDate(item, sort)
            let title: String
            if let match = named.first(where: { date >= $0.0 }) {
                title = match.1
            } else {
                if month.map({ date < $0.start || date >= $0.end }) ?? true,
                   let interval = calendar.dateInterval(of: .month, for: date) {
                    let style = calendar.component(.year, from: date) == year
                        ? Date.FormatStyle.dateTime.month(.wide) : .dateTime.month(.wide).year()
                    month = (interval.start, interval.end, date.formatted(style.in(calendar)))
                }
                title = month?.title ?? ""
            }
            if let i = groups.lastIndex(where: { $0.title == title }) { groups[i].items.append(item) }
            else { groups.append(ItemGroup(title: title, items: [item])) }
        }
        // Chronological whatever the sort: items of a title or site sort meet the days out of order. A day's heading
        // covers one stretch of time, so its first item's date places it.
        return groups.sorted {
            let (a, b) = (groupDate($0.items[0], sort), groupDate($1.items[0], sort))
            return reversed ? a < b : a > b
        }
    }

    /// The date a date heading and a row's date go by under `sort`.
    public static func groupDate(_ item: Item, _ sort: LibrarySort) -> Date {
        switch sort {
        case .dateOpened: item.opened
        case .dateSaved: item.firstSaved
        default: sortDate(item)
        }
    }

    /// Headings for items in `items(…)` order, by `grouping`: by date as above; by site, the first site in the order;
    /// by kind, in Kind's own order. A search always groups by status, since it mixes sections. The tab orders have no
    /// grouping: the pages open now, then the Closed ones.
    public static func groups(_ items: [Item], by grouping: LibraryGrouping, searching: Bool, now: Date,
                              sort: LibrarySort = .lastSeen, reversed: Bool = false, open: Set<UUID> = [],
                              calendar: Calendar = .current) -> [ItemGroup] {
        guard !searching else { return groups(items, searching: true, now: now, calendar: calendar) }
        if sort.isTabOrder {
            let (here, closed) = (items.filter { open.contains($0.id) }, items.filter { !open.contains($0.id) })
            return [ItemGroup(title: "Open", items: here), ItemGroup(title: "Closed", items: closed)].filter { !$0.items.isEmpty }
        }
        switch grouping {
        case .date:
            return groups(items, searching: false, now: now, sort: sort, reversed: reversed, calendar: calendar)
        case .site:
            var groups: [ItemGroup] = [], position: [String: Int] = [:]
            for item in items {
                if let i = position[item.host] { groups[i].items.append(item) } else {
                    position[item.host] = groups.count
                    groups.append(ItemGroup(title: item.host, items: [item]))
                }
            }
            return groups
        case .kind:
            let byKind = Dictionary(grouping: items, by: \.kind)
            return Kind.allCases.compactMap { kind in byKind[kind].map { ItemGroup(title: kind.title, items: $0) } }
        }
    }

    /// For rows that share a host and a title, a short hint at what sets each apart: the first part of its link the others
    /// don't have that reads as words (a course, a channel), in plain words; if there is none, when it was opened.
    /// Ids are never shown. Rows that stand alone get none.
    public static func differentiators(_ items: [Item], now: Date, calendar: Calendar = .current) -> [UUID: String] {
        var sameTitle: [String: [Item]] = [:]
        for item in items { sameTitle[item.host + "\n" + item.title.lowercased(), default: []].append(item) }
        var hints: [UUID: String] = [:]
        for same in sameTitle.values where same.count > 1 {
            let tokens = same.map { linkTokens($0.url) }
            let common = tokens.dropFirst().reduce(Set(tokens[0])) { $0.intersection($1) }
            let found = tokens.map { list in list.lazy.filter { !common.contains($0) }.compactMap { plainWords(in: String($0.dropFirst(2))) }.first }
            for (i, item) in same.enumerated() {
                // A hint two rows share tells nothing.
                let unique = found[i].flatMap { w in found.filter { $0 == w }.count == 1 ? w : nil }
                hints[item.id] = unique ?? "Opened " + openedStamp(item.opened, now: now, calendar: calendar)
            }
        }
        return hints
    }

    /// A link's path segments and query values, each marked "p:" or "q:" so equal text in different places differs.
    private static func linkTokens(_ link: String) -> [String] {
        guard let parts = URLComponents(string: link) else { return [] }
        let segments = parts.path.split(separator: "/").map { "p:" + (String($0).removingPercentEncoding ?? String($0)) }
        return segments + (parts.queryItems ?? []).compactMap { $0.value.map { "q:" + $0 } }
    }

    /// A link part as plain words ("intro-to_biology" → "Intro To Biology", at most 24 characters); nil for an id or
    /// anything without a word of three letters or more.
    private static func plainWords(in token: String) -> String? {
        let isHex = token.count >= 8 && token.allSatisfy(\.isHexDigit)
        let isNumber = token.wholeMatch(of: /_?\d+(_\d+)?/) != nil
        guard !isNumber, !isHex, !Structure.isIDLike(token) else { return nil }
        let text = token.split(whereSeparator: { $0 == "-" || $0 == "_" || $0 == "+" || $0 == " " })
            .map { $0.prefix(1).uppercased() + $0.dropFirst() }.joined(separator: " ")
        guard text.split(whereSeparator: { !$0.isLetter }).contains(where: { $0.count >= 3 }) else { return nil }
        return text.count <= 24 ? text : text.prefix(23) + "…"
    }

    /// "Thu 3:04 PM" within the week, else "Oct 3, 3:04 PM" (with the year once it isn't this one).
    private static func openedStamp(_ date: Date, now: Date, calendar: Calendar) -> String {
        let today = calendar.startOfDay(for: now)
        let style: Date.FormatStyle =
            if date >= calendar.date(byAdding: .day, value: -6, to: today)! { .dateTime.weekday(.abbreviated).hour().minute() }
            else if calendar.component(.year, from: date) == calendar.component(.year, from: now) { .dateTime.month(.abbreviated).day().hour().minute() }
            else { .dateTime.month(.abbreviated).day().year().hour().minute() }
        return date.formatted(style.in(calendar))
    }

    /// How long ago, by whole days: today, yesterday, 5 days ago, 3 weeks ago, 4 months ago, 2 years ago.
    public static func ago(_ date: Date, now: Date, calendar: Calendar = .current) -> String {
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: date), to: calendar.startOfDay(for: now)).day ?? 0
        let (count, unit) = days < 7 ? (days, "day") : days < 30 ? (days / 7, "week") : days < 365 ? (days / 30, "month") : (days / 365, "year")
        switch days {
        case ..<1: return "today"
        case 1: return "yesterday"
        default: return "\(count) \(unit)\(count == 1 ? "" : "s") ago"
        }
    }

    /// What a row shows beside those headings: the time for today and yesterday, the weekday within the week,
    /// then the day and month, with the year once it isn't this one.
    public static func shortDate(_ date: Date, now: Date, calendar: Calendar = .current) -> String {
        let today = calendar.startOfDay(for: now)
        let style: Date.FormatStyle =
            if date >= calendar.date(byAdding: .day, value: -1, to: today)! { .dateTime.hour().minute() }
            else if date >= calendar.date(byAdding: .day, value: -6, to: today)! { .dateTime.weekday(.abbreviated) }
            else if calendar.component(.year, from: date) == calendar.component(.year, from: now) { .dateTime.month(.abbreviated).day() }
            else { .dateTime.month(.abbreviated).day().year() }
        return date.formatted(style.in(calendar))
    }
}

/// A heading in the list and the items under it.
public struct ItemGroup: Identifiable, Equatable, Sendable {
    public var title: String
    public var items: [Item]
    public var id: String { title }

    public init(title: String, items: [Item]) {
        self.title = title
        self.items = items
    }
}

private extension Date.FormatStyle {
    func `in`(_ calendar: Calendar) -> Date.FormatStyle {
        var style = self
        style.calendar = calendar
        style.timeZone = calendar.timeZone
        if let locale = calendar.locale { style.locale = locale }
        return style
    }
}
