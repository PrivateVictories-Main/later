import AppKit
import LaterCore
import SwiftUI

/// The details pane: the selected page and what the user can do with it, what several selected pages can do at once, or,
/// with nothing selected, an overview of what's in the list.
struct InspectorPane: View {
    @Bindable var model: LibraryModel

    var body: some View {
        let items = model.selectedItems
        let actions = ItemActions(model: model)
        switch items.count {
        case 0: Overview(model: model)
        case 1: ItemDetail(engine: model.engine, item: items[0], copies: model.listing.copies[items[0].id] ?? 0, now: model.now,
                           actions: actions)
        default: SelectionSummary(items: items, actions: actions)
        }
    }
}

struct ItemDetail: View {
    let engine: Engine
    let item: Item
    /// Tabs holding the page right now (0: closed).
    let copies: Int
    /// The list's clock, so "Today at 9:41 AM" turns into "Yesterday at 9:41 AM" at midnight.
    let now: Date
    let actions: ItemActions
    @State private var copied = false
    @Environment(SiteIcons.self) private var icons
    @Environment(\.displayScale) private var scale
    private static let tile: CGFloat = 64

    var body: some View {
        let done = item.status == .done
        let browser = Opener.browser(preferred: engine.lastBrowser)
        let icon = icons.icons[item.host]
        let hue = Color(nsColor: icon?.tint ?? .site(item.host))
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                VStack(alignment: .leading, spacing: 12) {
                    SiteTile(host: item.host, size: Self.tile)
                        .shadow(color: hue.opacity(0.35), radius: 16, y: 7)
                    Text(item.displayTitle)
                        .font(.system(size: 19, weight: .bold))
                        .lineLimit(6)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                        .accessibilityAddTraits(.isHeader)
                    HStack(spacing: 8) {
                        HStack(spacing: 6) {
                            // An icon too small for the tile above (which shows the site's letter instead) sits by the
                            // site's name, as in an address bar: up to 16 pt, when it's crisp at 11 or more.
                            if let icon, SiteTile.side(of: icon, in: Self.tile, scale: scale) == nil,
                               let side = SiteTile.side(of: icon, in: 32, scale: scale) {
                                SiteIconImage(icon: icon, side: side)
                            }
                            Text(item.host)
                                .foregroundStyle(Color.secondaryText)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 4)
                        KindMenu(kind: item.kind) { actions.move([item], to: $0) }
                            .disabled(!actions.canEdit)
                    }
                    Label(why, systemImage: item.regular == true ? "repeat" : "sparkle.magnifyingglass")
                        .font(.callout)
                        .foregroundStyle(Color.secondaryText)
                }
                VStack(spacing: 8) {
                    Button { actions.open([item]) } label: {
                        Label(browser.map { "Open in \($0.displayName)" } ?? "Open", systemImage: "arrow.up.forward.app")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.glassProminent)
                    .controlSize(.extraLarge)
                    .tint(.brand)
                    GlassEffectContainer(spacing: 8) {
                        HStack(spacing: 8) {
                            Button {
                                actions.copyLinks([item])
                                withAnimation(Motion.quick) { copied = true }
                            } label: {
                                Label(copied ? "Copied" : "Copy Link", systemImage: copied ? "checkmark" : "link")
                                    .contentTransition(Motion.symbolSwap)
                                    .frame(maxWidth: .infinity)
                            }
                            if item.regular != true {
                                Button { actions.setDone([item], !done) } label: {
                                    Label(done ? "Move Back" : "Mark Done", systemImage: done ? "arrow.uturn.backward" : "checkmark.circle")
                                        .frame(maxWidth: .infinity)
                                }
                                .disabled(!actions.canEdit)
                            }
                        }
                        .buttonStyle(.glass)
                        .controlSize(.large)
                    }
                    if let regular = item.regular {
                        Button { regular ? actions.keepInList([item]) : actions.markRegular([item]) } label: {
                            Label(regular ? "Keep in My List" : "This Is a Regular",
                                  systemImage: regular ? "tray.and.arrow.down" : "repeat").frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.glass)
                        .controlSize(.large)
                        .help(regular ? "Move this page, and pages like it, back to your list (Edit ▸ Undo undoes it)"
                                      : "Move this page, and pages like it, out of your list (Edit ▸ Undo brings them back)")
                        .disabled(!actions.canEdit)
                    }
                }
                DetailsCard {
                    // The browser's history knows when a page was first opened; without it, it's when Later first saw it.
                    fact(item.openedAt == nil ? "First seen" : "Opened", Self.format(item.opened))
                    if item.openedAt != nil, abs(item.opened.timeIntervalSince(item.firstSaved)) > 60 {
                        fact("Saved", Self.format(item.firstSaved))
                    }
                    fact("Last seen", Self.format(item.lastSeen))
                    if let doneAt = item.doneAt { fact("Done", Self.format(doneAt)) }
                    fact("Visits", item.totalVisits == 1 ? "Once" : "\(item.totalVisits) times")
                    if copies > 0 { fact("Open now", copies == 1 ? "In 1 tab" : "In \(copies) tabs") }
                    fact("From", item.browser.displayName)
                    DetailsRow(label: "Link", divider: false) {
                        Text(Self.readable(item.url))
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .textSelection(.enabled)
                            .help(item.url)
                    }
                }
                HStack(spacing: 8) {
                    Button { actions.notAnIdea([item]) } label: { Label("Not an Idea", systemImage: "hand.thumbsdown") }
                        .help("Remove this page and stop keeping pages like it (Edit ▸ Undo brings it back)")
                    Spacer(minLength: 0)
                    Button(role: .destructive) { actions.delete([item]) } label: {
                        Label("Delete", systemImage: "trash").foregroundStyle(Color.destructive)
                    }
                    .help("Delete this page (Edit ▸ Undo brings it back)")
                }
                .buttonStyle(.glass)
                .disabled(!actions.canEdit)
            }
            .padding(.horizontal, 20)
            .padding(.top, 14)
            .padding(.bottom, 24)
        }
        .background(alignment: .top) { AmbientWash(color: hue) }
        .onChange(of: item.id) { copied = false }   // the pane stays and updates in place as the selection moves
    }

    /// One line on why the page is where it is: from the site's days and the user's own choice for its page type.
    private var why: String {
        let library = engine.library
        return Familiarity.why(regular: item.regular, days: library.visitDays(site: item.host, today: Familiarity.dayNumber(of: now)),
                               correction: library.data.corrections[item.pageType])
    }

    private func fact(_ label: String, _ value: String) -> some View {
        // One line: a long date tightens a little at the inspector's narrowest instead of wrapping.
        DetailsRow(label: label) { Text(value).lineLimit(1).allowsTightening(true).minimumScaleFactor(0.85) }
    }

    /// The link as people read it, for one line: rei.com/product/229153/… without https://, www. or a closing slash.
    static func readable(_ url: String) -> String {
        var text = Substring(url)
        if let scheme = text.range(of: "://") { text = text[scheme.upperBound...] }
        if text.hasPrefix("www.") { text = text.dropFirst(4) }
        if text.hasSuffix("/") { text = text.dropLast() }
        return text.removingPercentEncoding ?? String(text)
    }

    /// "Today at 9:41 AM", "Yesterday at 9:05 PM", "Oct 17, 2026 at 3:00 PM": exact, so it never goes stale. "Today" is
    /// worked out as it's formatted; a new time zone makes a new formatter.
    static func format(_ date: Date) -> String {
        if formatter.timeZone != TimeZone.current { formatter = makeFormatter() }
        return formatter.string(from: date)
    }

    private static var formatter = makeFormatter()

    private static func makeFormatter() -> DateFormatter {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        formatter.doesRelativeDateFormatting = true
        return formatter
    }
}

/// The site's own color, washed down from the top of the pane: each page brings a little of its site's color with it.
struct AmbientWash: View {
    let color: Color
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        if !reduceTransparency {
            LinearGradient(colors: [color.opacity(scheme == .dark ? 0.30 : 0.20), color.opacity(0)], startPoint: .top, endPoint: .bottom)
                .frame(height: 320)
                .ignoresSafeArea()
                .allowsHitTesting(false)
        }
    }
}

/// The page's kind as a capsule of glass in its color; choosing another moves every saved page like it.
struct KindMenu: View {
    let kind: Kind
    let move: (Kind) -> Void

    var body: some View {
        Menu {
            ForEach(Kind.allCases) { other in
                Button { move(other) } label: { Label(other.title, systemImage: other.symbol) }
                    .disabled(other == kind)
            }
        } label: {
            HStack(spacing: 5) {
                Image(systemName: kind.badgeSymbol).font(.system(size: 11, weight: .semibold))
                Text(kind.title)
                Image(systemName: "chevron.up.chevron.down").font(.system(size: 8, weight: .bold)).opacity(0.7)
            }
            .fontWeight(.semibold)
            .foregroundStyle(kind.labelColor)
            .padding(.horizontal, 11)
            .padding(.vertical, 5)
            .glassEffect(.regular.tint(kind.color.opacity(0.14)).interactive(), in: .capsule)
            .contentShape(.capsule)
            .accessibilityHidden(true)   // its symbols would read as "Shopping Bag" and "Up And Down Chevrons"
        }
        .accessibilityLabel("Kind")
        .accessibilityValue(kind.title)
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Changes the kind of every saved page like this one")
    }
}

/// Facts about a page in one rounded card, label on the left, value on the right.
struct DetailsCard<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        VStack(spacing: 0) { content }
            .font(.callout)
            .background(.primary.opacity(0.045), in: shape)
            .overlay(shape.strokeBorder(.primary.opacity(0.07), lineWidth: 0.5))
    }
}

struct DetailsRow<Value: View>: View {
    let label: String
    var divider = true
    @ViewBuilder let value: Value

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text(label).foregroundStyle(Color.secondaryText)
                Spacer(minLength: 8)
                value.multilineTextAlignment(.trailing)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 9)
            if divider { Divider().padding(.leading, 14) }
        }
    }
}

struct SelectionSummary: View {
    let items: [Item]
    let actions: ItemActions

    var body: some View {
        let allDone = items.allSatisfy { $0.status == .done }
        let sites = Set(items.map(\.host)).count
        ScrollView {
            VStack(spacing: 20) {
                ZStack {
                    ForEach(Array(items.prefix(3).enumerated()).reversed(), id: \.element.id) { index, item in
                        SiteTile(host: item.host, size: 60)
                            .shadow(color: .black.opacity(0.16), radius: 6, y: 3)
                            .rotationEffect(.degrees(Double(index) * 9 - 9))
                            .offset(x: CGFloat(index) * 26 - 26, y: index == 1 ? -4 : 0)
                    }
                }
                .frame(height: 84)
                .padding(.top, 10)
                VStack(spacing: 3) {
                    Text("\(items.count) Pages").font(.system(size: 22, weight: .bold, design: .rounded))
                    Text(sites == 1 ? "From 1 site" : "From \(sites) sites").foregroundStyle(Color.secondaryText)
                }
                VStack(spacing: 8) {
                    Button { actions.open(items) } label: {
                        Label("Open \(items.count) Pages", systemImage: "arrow.up.forward.app").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.glassProminent)
                    .controlSize(.extraLarge)
                    .tint(.brand)
                    GlassEffectContainer(spacing: 8) {
                        HStack(spacing: 8) {
                            Button { actions.copyLinks(items) } label: {
                                Label("Copy Links", systemImage: "link").frame(maxWidth: .infinity)
                            }
                            Button { actions.setDone(items, !allDone) } label: {
                                Label(allDone ? "Move Back" : "Mark Done", systemImage: allDone ? "arrow.uturn.backward" : "checkmark.circle")
                                    .frame(maxWidth: .infinity)
                            }
                            .disabled(!actions.canEdit)
                        }
                        .buttonStyle(.glass)
                        .controlSize(.large)
                    }
                }
                HStack(spacing: 8) {
                    Button { actions.notAnIdea(items) } label: { Label("Not Ideas", systemImage: "hand.thumbsdown") }
                        .help("Remove these pages and stop keeping pages like them (Edit ▸ Undo brings them back)")
                    Spacer(minLength: 0)
                    Button(role: .destructive) { actions.delete(items) } label: {
                        Label("Delete \(items.count)", systemImage: "trash").foregroundStyle(Color.destructive)
                    }
                    .help("Delete these pages (Edit ▸ Undo brings them back)")
                }
                .buttonStyle(.glass)
                .disabled(!actions.canEdit)
            }
            .padding(.horizontal, 20)
            .padding(.top, 14)
            .padding(.bottom, 24)
        }
        .background(alignment: .top) { AmbientWash(color: .brand) }
    }
}

/// With nothing selected: what's in this list, by kind and by site. A kind or a site takes the user straight to its pages.
/// In a search, the same for its results, which only describe them: the list is the answer there.
struct Overview: View {
    let model: LibraryModel

    var body: some View {
        let items = model.listing.items
        let searching = model.searching
        let kinds = items.reduce(into: [Kind: Int]()) { $0[$1.kind, default: 0] += 1 }
        let sites = items.reduce(into: [String: Int]()) { $0[$1.host, default: 0] += 1 }
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Overview").font(.system(size: 19, weight: .bold))
                    Text(caption(searching: searching, empty: items.isEmpty))
                        .font(.callout)
                        .foregroundStyle(Color.secondaryText)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if !items.isEmpty, searching || !model.section.isKind {
                    KindBreakdown(counts: kinds, show: searching ? nil : { model.section = .kind($0) }).equatable()
                }
                // Only once some site has more than one page here; until then it would just list every page's site.
                if sites.values.contains(where: { $0 > 1 }) {
                    TopSites(sites: TopSites.ranked(sites), search: searching ? nil : { model.setSearch($0) }).equatable()
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)   // short content stays at the leading edge, not centered
            .padding(.horizontal, 20)
            .padding(.top, 14)
            .padding(.bottom, 24)
        }
        .background(alignment: .top) { AmbientWash(color: searching ? .brand : model.section.tint) }
    }

    private func caption(searching: Bool, empty: Bool) -> String {
        switch (searching, empty) {
        case (true, true): "Nothing matches “\(model.query)”."
        case (true, false): "What “\(model.query)” found. Select a result to see where it came from."
        case (false, true): "Nothing to show here."
        case (false, false): "Select a page to see where it came from and what you can do with it."
        }
    }
}

/// How the list splits by kind: one bar in the kinds' colors, then each kind with its count. Drawn again only when the
/// counts change, so typing a search that finds the same pages costs it nothing.
struct KindBreakdown: View, @MainActor Equatable {
    let counts: [Kind: Int]
    /// Taken to that kind's section; nil for a search's results, which the rows only describe.
    let show: ((Kind) -> Void)?

    static func == (a: Self, b: Self) -> Bool { a.counts == b.counts && (a.show == nil) == (b.show == nil) }

    var body: some View {
        let kinds = Kind.allCases.filter { counts[$0] != nil }
        let total = counts.values.reduce(0, +)
        VStack(alignment: .leading, spacing: 12) {
            Text("By kind").font(.headline)
            // One drawing rather than a view per kind: it's redrawn on every change to the list.
            Canvas { context, size in
                context.clip(to: Capsule().path(in: CGRect(origin: .zero, size: size)))
                let room = size.width - CGFloat(kinds.count - 1) * 2
                var x: CGFloat = 0
                for kind in kinds {
                    let width = max(4, room * CGFloat(counts[kind]!) / CGFloat(total))
                    context.fill(Path(CGRect(x: x, y: 0, width: width, height: size.height)), with: .style(kind.color.gradient))
                    x += width + 2
                }
            }
            .frame(height: 10)
            .accessibilityHidden(true)
            VStack(spacing: 2) {
                ForEach(kinds) { kind in
                    let row = HStack(spacing: 10) {
                        Image(systemName: kind.badgeSymbol)
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(kind.labelColor)
                            .frame(width: 18)
                        Text(kind.title)
                        Spacer()
                        Text(counts[kind]!, format: .number).monospacedDigit().foregroundStyle(Color.secondaryText)
                    }
                    .padding(.horizontal, 8)
                    .padding(.vertical, 5)
                    if let show {
                        Button { show(kind) } label: { row.contentShape(Rectangle()) }
                            .buttonStyle(OverviewRowStyle())
                            .help("Show \(kind.title)")
                    } else {
                        row.accessibilityElement(children: .combine)
                    }
                }
            }
        }
    }
}

/// The sites with the most pages here, each a click away from its own search (in a search, just listed).
struct TopSites: View, @MainActor Equatable {
    struct Site: Equatable { let host: String, count: Int }
    let sites: [Site]
    let search: ((String) -> Void)?

    static func == (a: Self, b: Self) -> Bool { a.sites == b.sites && (a.search == nil) == (b.search == nil) }

    /// The six sites with the most pages, then by name.
    static func ranked(_ counts: [String: Int]) -> [Site] {
        counts.map { Site(host: $0.key, count: $0.value) }
            .sorted { $0.count != $1.count ? $0.count > $1.count : $0.host < $1.host }
            .prefix(6)
            .map { $0 }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Top sites").font(.headline)
            VStack(spacing: 2) {
                ForEach(sites, id: \.host) { site in
                    let row = HStack(spacing: 10) {
                        SiteTile(host: site.host, size: 26)
                        Text(site.host).lineLimit(1)
                        Spacer()
                        Text(site.count, format: .number).monospacedDigit().foregroundStyle(Color.secondaryText)
                    }
                    .padding(.horizontal, 6)
                    .padding(.vertical, 4)
                    if let search {
                        Button { search(site.host) } label: { row.contentShape(Rectangle()) }
                            .buttonStyle(OverviewRowStyle())
                            .help("Search for \(site.host)")
                    } else {
                        row.accessibilityElement(children: .combine)
                    }
                }
            }
        }
    }
}

/// A plain row that lights up under the pointer, like a menu item.
struct OverviewRowStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        OverviewRow(configuration: configuration)
    }

    private struct OverviewRow: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .background(.primary.opacity(configuration.isPressed ? 0.1 : hovering ? 0.05 : 0),
                            in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .onHover { hovering = $0 }
        }
    }
}
