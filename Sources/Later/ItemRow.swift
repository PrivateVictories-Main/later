import AppKit
import LaterCore
import SwiftUI

/// One saved page: its site tile, the title on up to two lines, then its kind, site and visits. The date it's listed by
/// sits at the trailing edge and gives way to Open and Done under the pointer, in the same space, so nothing shifts.
/// Equal rows aren't redrawn: moving the selection redraws two rows, not the whole list.
struct ItemRow: View, @MainActor Equatable {
    let item: Item
    let now: Date
    let selected: Bool
    /// Drawn in the section's color: the window is key and the list has focus. Otherwise the selection is a quiet gray.
    let emphasized: Bool
    /// The kind tag, where pages of several kinds are listed together, and the site, unless a heading already names it.
    let showsKind: Bool
    var showsHost = true
    /// What the list is ordered by: the date at the trailing edge, and whether the opening time shows, follow it.
    let sort: LibrarySort
    /// Tabs holding the page right now (0: it's closed).
    let copies: Int
    /// A hint at what sets this page apart from another with the same site and title.
    let hint: String?
    let tint: Color
    /// The selection pill's identity: shared by single selections, so it glides from row to row.
    let pillID: AnyHashable
    let namespace: Namespace.ID
    /// What the row's buttons and menu act on.
    let model: LibraryModel
    let tap: () -> Void
    /// A press from VoiceOver or another assistive tool: selects the row and focuses the list, as a click does.
    let press: () -> Void
    @State private var hovering = false
    @Environment(\.colorScheme) private var scheme

    static func == (a: Self, b: Self) -> Bool {
        a.item == b.item && a.now == b.now && a.selected == b.selected && a.emphasized == b.emphasized
            && a.showsKind == b.showsKind && a.showsHost == b.showsHost && a.sort == b.sort && a.copies == b.copies
            && a.hint == b.hint && a.tint == b.tint && a.pillID == b.pillID
            && a.namespace == b.namespace
    }

    private func open() { ItemActions(model: model).open([item]) }
    private func toggleDone() { ItemActions(model: model).setDone([item], item.status != .done) }

    var body: some View {
        let done = item.status == .done
        let date = LibraryQuery.shortDate(LibraryQuery.groupDate(item, sort), now: now)
        HStack(alignment: .center, spacing: 12) {
            SiteTile(host: item.host, size: 40)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.displayTitle)
                    .font(.system(size: 13.5, weight: .semibold))
                    .lineLimit(2)
                    .foregroundStyle(.primary)
                HStack(spacing: 10) {
                    // The site and hint give way first: the opened text and the counts keep their full width.
                    HStack(spacing: 6) {
                        meta
                            .lineLimit(1)
                        if let text = openedText {
                            if showsKind || showsHost || hint != nil { Text("·").foregroundStyle(.tertiary) }
                            Text(text).foregroundStyle(Color.secondaryText).lineLimit(1).fixedSize().layoutPriority(1)
                        }
                    }
                    Spacer(minLength: 0)
                    counts
                }
                .font(.callout)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            ZStack(alignment: .trailing) {
                Text(date)
                    .font(.callout)
                    .monospacedDigit()
                    .foregroundStyle(Color.secondaryText.opacity(hovering ? 0 : 1))
                if hovering {
                    GlassEffectContainer(spacing: 6) {
                        HStack(spacing: 6) {
                            Button(action: open) { Image(systemName: "arrow.up.forward") }
                                .help("Open in your browser")
                            Button(action: toggleDone) { Image(systemName: done ? "arrow.uturn.backward" : "checkmark") }
                                .help(done ? "Move back to Later" : "Mark Done")
                        }
                        .buttonStyle(.glass)
                        .buttonBorderShape(.circle)
                        .controlSize(.small)
                        .fontWeight(.semibold)
                    }
                    .transition(Motion.hover)
                }
            }
            .frame(minWidth: 64, alignment: .trailing)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .background { background.animation(Motion.selection, value: selected) }
        .contentShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .onHover { inside in withAnimation(Motion.hoverFade) { hovering = inside } }
        .onTapGesture(perform: tap)
        .contextMenu { RowMenu(item: item, model: model) }
        // One element, instead of SwiftUI combining the row's texts (which costs every new row a query of each one):
        // read as "title, kind, site" with the time saved as its value. Pressing it selects it, as a click does; the
        // hover buttons and the context menu are covered by the named actions, which take the same undoable paths.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(item.displayTitle), \(item.kind.title), \(item.host)" + (hint.map { ", \($0)" } ?? ""))
        .accessibilityValue(accessibilityValue(saved: date))
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
        .accessibilityAction(.default, press)
        .accessibilityActions { RowActions(item: item, model: model) }
    }

    /// When the page was opened, shown when that's what the list is ordered by or it differs from when it was saved:
    /// "Opened 3 weeks ago", or "First seen" where the browser's history can't say.
    private var openedText: String? {
        guard hint?.hasPrefix("Opened") != true else { return nil }   // the hint already says when
        let ago = { LibraryQuery.ago(item.opened, now: now) }
        if item.openedAt != nil, sort == .dateOpened || abs(item.opened.timeIntervalSince(item.firstSaved)) > 86_400 {
            return "Opened " + ago()
        }
        return item.openedAt == nil && sort == .dateOpened ? "First seen " + ago() : nil
    }

    /// The kind (in its color, after its symbol), then the site and what sets it apart: one text, which a new row builds
    /// far faster than a label and several texts. When it opened is its own text beside it, so that is never cut.
    private var meta: Text {
        let details = [showsHost ? item.host : nil, hint].compactMap { $0 }
        let rest = Text(details.joined(separator: " · ")).foregroundStyle(Color.secondaryText)
        guard showsKind else { return rest }
        let symbol = Text(Image(systemName: item.kind.badgeSymbol)).font(.system(size: 9.5, weight: .bold))
        let kind = Text("\(symbol) \(item.kind.title)").fontWeight(.semibold).foregroundStyle(item.kind.labelColor)
        return details.isEmpty ? kind : Text("\(kind)  \(Text("·").foregroundStyle(.tertiary))  \(rest)")
    }

    /// On the right of the detail line: a dot when the page is open right now (with how many tabs, past one), then how
    /// many times it has been visited.
    @ViewBuilder private var counts: some View {
        if copies > 0 {
            HStack(spacing: 5) {
                Circle().fill(Color.green.gradient).frame(width: 6, height: 6)
                if let label = Self.openLabel(copies) { Text(label).monospacedDigit() }
            }
            .foregroundStyle(Color.secondaryText)
            .help(copies > 1 ? "Open in \(copies) tabs now" : "Open in a tab now")
            .fixedSize()
            .layoutPriority(1)
        }
        if let label = Self.visitsLabel(item) {
            Text(label).monospacedDigit().foregroundStyle(Color.secondaryText).lineLimit(1).fixedSize().layoutPriority(1)
        }
    }

    /// "2 open" for a page held by several tabs; one tab is only the dot.
    static func openLabel(_ copies: Int) -> String? { copies > 1 ? "\(copies) open" : nil }

    /// "12 visits", counting what the browser's history knows too; a page seen once says nothing.
    static func visitsLabel(_ item: Item) -> String? { item.totalVisits > 1 ? "\(item.totalVisits) visits" : nil }

    private func accessibilityValue(saved date: String) -> String {
        // Under Date Opened the row's date is when it was opened, not saved.
        var parts = [sort == .dateOpened ? (item.openedAt == nil ? "First seen \(date)" : "Opened \(date)") : "Saved \(date)"]
        if sort != .dateOpened, let openedText { parts.append(openedText) }
        if let visits = Self.visitsLabel(item) { parts.append(visits) }
        if copies > 0 { parts.append(copies > 1 ? "Open in \(copies) tabs" : "Open now") }
        return parts.joined(separator: ", ")
    }

    @ViewBuilder private var background: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        if selected {
            let dark = scheme == .dark
            Group {
                if emphasized {
                    shape.fill(tint.opacity(dark ? 0.17 : 0.14))
                        .overlay(shape.strokeBorder(tint.opacity(dark ? 0.34 : 0.30), lineWidth: 1))
                        .shadow(color: tint.opacity(dark ? 0.12 : 0.16), radius: 8, y: 3)
                } else {
                    shape.fill(.primary.opacity(dark ? 0.12 : 0.07))
                        .overlay(shape.strokeBorder(.primary.opacity(0.08), lineWidth: 1))
                }
            }
            .matchedGeometryEffect(id: pillID, in: namespace)
        } else if hovering {
            shape.fill(.primary.opacity(scheme == .dark ? 0.06 : 0.04))
        }
    }
}

/// The named actions VoiceOver offers on a row: the context menu's, without the per-browser choices. They act on this
/// row's page alone, as a row action does for VoiceOver, so they never read the selection (a selection change redraws
/// only the rows whose selected state changed).
struct RowActions: View {
    let item: Item
    let model: LibraryModel

    var body: some View {
        let picked = [item]
        let actions = ItemActions(model: model)
        let allDone = picked.allSatisfy { $0.status == .done }
        Button("Open") { actions.open(picked) }
        Button("Copy Link") { actions.copyLinks(picked) }
        Button(allDone ? "Move Back to Later" : "Mark Done") { actions.setDone(picked, !allDone) }
        ForEach(Kind.allCases.filter { kind in !picked.allSatisfy { $0.kind == kind } }) { kind in
            Button("Move to \(kind.title)") { actions.move(picked, to: kind) }
        }
        Button("Not an Idea") { actions.notAnIdea(picked) }
        Button("Delete") { actions.delete(picked) }
    }
}

/// A row's context menu. A right-click on a selected page acts on the whole selection, on any other page just on that one.
struct RowMenu: View {
    let item: Item
    let model: LibraryModel

    var body: some View {
        let picked = model.selection.selected.contains(item.id) ? model.selectedItems : [item]
        let actions = ItemActions(model: model)
        let many = picked.count > 1
        let allDone = picked.allSatisfy { $0.status == .done }
        Button(many ? "Open \(picked.count) Pages" : "Open") { actions.open(picked) }
        ForEach(Browser.allCases.filter(\.isInstalled), id: \.self) { browser in
            Button("Open in \(browser.displayName)") { actions.open(picked, in: browser) }
        }
        Button(many ? "Copy \(picked.count) Links" : "Copy Link") { actions.copyLinks(picked) }
        Divider()
        Menu("Move To") {
            ForEach(Kind.allCases) { kind in
                Button { actions.move(picked, to: kind) } label: { Label(kind.title, systemImage: kind.symbol) }
                    .disabled(picked.allSatisfy { $0.kind == kind })
            }
        }
        Button("Not an Idea") { actions.notAnIdea(picked) }
        Button(allDone ? "Move Back to Later" : "Mark Done") { actions.setDone(picked, !allDone) }
        Divider()
        Button("Delete", role: .destructive) { actions.delete(picked) }
    }
}

/// A site's icon on a tile of its own color, like an app icon: the icon from Helium's own cache at a size it stays crisp
/// at, or the site's first letter when there's none, or when it's too small to fill the tile with more than a speck.
struct SiteTile: View {
    let host: String
    var size: CGFloat = 40
    @Environment(SiteIcons.self) private var icons
    @Environment(\.colorScheme) private var scheme
    @Environment(\.colorSchemeContrast) private var contrast
    @Environment(\.displayScale) private var scale

    var body: some View {
        let icon = icons.icons[host]
        let dark = scheme == .dark, strong = contrast == .increased
        // The site's color: its icon's own, a color of its own when it has no icon, and none for a gray or many-colored icon.
        let site: NSColor? = icon == nil ? .site(host) : icon?.tint
        let hue = site.map { Color(nsColor: $0) }
        let shape = RoundedRectangle(cornerRadius: size * 0.27, style: .continuous)
        ZStack {
            if let icon, let side = Self.side(of: icon, in: size, scale: scale) {
                SiteIconImage(icon: icon, side: side)
            } else {
                Text(Self.letter(host))
                    .font(.system(size: size * 0.44, weight: .bold, design: .rounded))
                    .foregroundStyle(site.map { Color(nsColor: $0.legibleOnTint) } ?? Color.primary.opacity(0.8))
            }
        }
        .frame(width: size, height: size)
        .background {
            // Opaque underneath, so a selected row's color never shows through the tint.
            shape.fill(Color(nsColor: .controlBackgroundColor))
            if let hue {
                shape.fill(LinearGradient(colors: [hue.opacity(dark ? 0.26 : 0.10), hue.opacity(dark ? 0.42 : 0.24)],
                                          startPoint: .top, endPoint: .bottom))
            } else {
                shape.fill(LinearGradient(colors: [.primary.opacity(dark ? 0.08 : 0.03), .primary.opacity(dark ? 0.16 : 0.08)],
                                          startPoint: .top, endPoint: .bottom))
            }
        }
        // Light along the top edge and a hairline all round: a tile of glass rather than a flat swatch.
        .overlay(shape.strokeBorder(LinearGradient(colors: [.white.opacity(dark ? 0.22 : 0.75), .white.opacity(0)],
                                                   startPoint: .top, endPoint: .center), lineWidth: 1))
        .overlay(shape.strokeBorder(.primary.opacity(strong ? 0.45 : dark ? 0.14 : 0.09), lineWidth: strong ? 1 : 0.5))
        .drawingGroup()   // one layer instead of five: switching sections builds a dozen of these at once
        .shadow(color: .black.opacity(dark ? 0.3 : 0.08), radius: 1.5, y: 1)
        .accessibilityHidden(true)
    }

    /// The site's name label: amazon.co.uk → A, en.wikipedia.org → W, x.com → X.
    static func letter(_ host: String) -> String {
        var labels = host.split(separator: ".")
        while labels.count > 1, let last = labels.last, last.count <= 3 { labels.removeLast() }
        return labels.last?.first.map { String($0).uppercased() } ?? "?"
    }

    /// How big an icon is drawn on a tile this size: as large as it stays crisp, up to half the tile. Nil when that's
    /// under about a third of the tile (on a Retina screen: a 32 px icon on the inspector's tile, a 16 px one in a row),
    /// where it looks lost and the letter reads better.
    static func side(of icon: SiteIcons.Icon, in size: CGFloat, scale: CGFloat) -> CGFloat? {
        let crisp = icon.pixels / max(scale, 1)
        return crisp >= size * 0.35 ? min(crisp, size * 0.5) : nil
    }
}

/// A site's icon by itself, never drawn past its own pixels: a gray or black mark in the label color, so it reads in
/// light and dark alike, and any other icon as it is.
struct SiteIconImage: View {
    let icon: SiteIcons.Icon
    let side: CGFloat
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let image = Image(nsImage: icon.image).resizable().interpolation(.high)
        Group {
            if icon.isMark { image.renderingMode(.template).foregroundStyle(Color.primary.opacity(0.85)) } else { image }
        }
        .aspectRatio(contentMode: .fit)
        // A square icon (a logo on its own square) sits like a small app icon; a mark keeps its own shape.
        .clipShape(RoundedRectangle(cornerRadius: !icon.isMark && icon.image.size.width == icon.image.size.height ? side * 0.2 : 0,
                                    style: .continuous))
        .frame(width: side, height: side)
        .shadow(color: .black.opacity(scheme == .dark ? 0.3 : 0.12), radius: 0.5, y: 0.5)
        .accessibilityHidden(true)
    }
}
