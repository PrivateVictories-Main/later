import AppKit
import LaterCore
import SwiftUI

/// The middle of the window: the section's header, then its pages under floating headings, with the keyboard and the
/// context menu. Selecting is a click; opening is a double-click, Return, a row's Open button or the inspector.
struct ListPane: View {
    @Bindable var model: LibraryModel
    @Namespace private var namespace
    @FocusState private var focused: Bool
    /// The toolbar's height: the list starts below it and scrolls up under its glass.
    @State private var toolbarInset: CGFloat = 52
    @Environment(\.controlActiveState) private var activeState

    var body: some View {
        let listing = model.listing
        let actions = ItemActions(model: model)
        let section = model.section
        let grouping = model.groupingActive ? model.effectiveGrouping : nil
        let showsKind = grouping != .kind && (model.searching || !section.isKind)
        let emphasized = focused && activeState == .key || Sample.emphasized
        let singlePill = model.selection.selected.count == 1
        let today = Calendar.current.startOfDay(for: model.now)   // rows show dates by the day: a later time redraws none
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 2, pinnedViews: [.sectionHeaders]) {
                    ListHeader(model: model, summary: listing.summary)
                    if model.showsNotice, let notice = model.engine.notice {
                        NoticeBar(text: notice) { withAnimation(Motion.standard) { model.dismissedGeneration = model.engine.noticeGeneration } }
                            .transition(Motion.notice)
                    }
                    if listing.items.isEmpty {
                        EmptyLibrary(model: model)
                            .frame(maxWidth: .infinity, minHeight: 440)
                    }
                    ForEach(listing.groups) { group in
                        let folded = model.collapsed.contains(model.foldKey(group))
                        Section {
                            if !folded {
                                ForEach(group.items) { item in
                                    ItemRow(item: item, now: today, selected: model.selection.selected.contains(item.id),
                                            emphasized: emphasized, showsKind: showsKind, showsHost: grouping != .site,
                                            sort: model.sort, copies: listing.copies[item.id] ?? 0, hint: listing.hints[item.id],
                                            tint: section.tint,
                                            pillID: singlePill ? AnyHashable("selection") : AnyHashable(item.id),
                                            namespace: namespace, model: model, tap: { tap(item, actions) }, press: { select(item) })
                                    .equatable()
                                }
                            }
                        } header: {
                            GroupHeader(group: group, grouping: grouping, folded: folded) {
                                model.toggleFold(group)
                            }
                        }
                    }
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 24)
            }
            .contentMargins(.top, toolbarInset, for: .scrollContent)
            .ignoresSafeArea(.container, edges: .top)
            .focusable()
            .focused($focused)
            .focusEffectDisabled()
            .onKeyPress(keys: [.upArrow, .downArrow]) { press in
                model.move(by: press.key == .upArrow ? -1 : 1, extending: press.modifiers.contains(.shift),
                           toEnd: press.modifiers.contains(.command))
                return .handled
            }
            .onKeyPress(.return) {
                guard !model.selection.selected.isEmpty else { return .ignored }
                actions.open(model.selectedItems)
                return .handled
            }
            .onKeyPress(.escape) {
                if model.searching { model.setSearch("") } else if !model.selection.selected.isEmpty { model.selection.select(nil) } else { return .ignored }
                return .handled
            }
            .onKeyPress(characters: .alphanumerics.union(.punctuationCharacters).union(.symbols), phases: .down) { press in
                guard press.modifiers.isSubset(of: .shift) else { return .ignored }   // shortcuts go on to the menus
                model.typeToSearch(press.characters)
                return .handled
            }
            .onCommand(#selector(NSResponder.selectAll(_:))) { model.selectAll() }
            .onDeleteCommand { actions.delete(model.selectedItems) }
            .onChange(of: model.selection.cursor) { _, id in
                if let id { proxy.scrollTo(id) }   // at once, as Mac lists do: a scroll animation redraws the list every frame
            }
        }
        .onGeometryChange(for: CGFloat.self) { $0.safeAreaInsets.top } action: { toolbarInset = $0 }
        .background(Color.listGround)
        .tint(section.tint)
        .confirmationDialog("Delete everything in Done?", isPresented: $model.confirmingEmptyDone) {
            Button("Empty Done", role: .destructive) { actions.emptyDone(listing.items) }
        } message: {
            Text("Edit ▸ Undo brings it all back.")
        }
        .confirmationDialog(model.opening.map(openTitle) ?? "", isPresented: Binding { model.opening != nil } set: { if !$0 { model.opening = nil } },
                            presenting: model.opening) { request in
            Button("Open \(request.items.count) Pages") { actions.open(request) }.keyboardShortcut(.defaultAction)
        } message: { _ in
            Text("Each page opens in a new tab.")
        }
    }

    /// One click selects (⌘ and ⇧ as everywhere on the Mac); a double-click opens.
    private func tap(_ item: Item, _ actions: ItemActions) {
        focused = true
        if NSApp.currentEvent?.clickCount == 2, NSEvent.modifierFlags.isDisjoint(with: [.command, .shift]) {
            actions.open(targets(item))
        } else {
            select(item)
        }
    }

    private func select(_ item: Item) {
        focused = true
        model.click(item.id)
    }

    /// A right-click on a selected page acts on the whole selection, on any other page just on that one.
    private func targets(_ item: Item) -> [Item] {
        model.selection.selected.contains(item.id) ? model.selectedItems : [item]
    }

    /// "Open 37 pages in Helium?"
    private func openTitle(_ request: OpenRequest) -> String {
        let browser = request.browser ?? Opener.browser(preferred: model.engine.lastBrowser)
        return "Open \(request.items.count) pages" + (browser.map { " in \($0.displayName)?" } ?? "?")
    }
}

extension LibraryModel {
    var showsNotice: Bool { engine.notice != nil && engine.noticeGeneration != dismissedGeneration }

    /// Whether headings follow the grouping: not in a search (which groups by status) nor in tab order (open, then closed).
    var groupingActive: Bool { !searching && !sort.isTabOrder }

    /// Grouping by kind inside one kind would make a single heading; dates stand in.
    var effectiveGrouping: LibraryGrouping { section.isKind && grouping == .kind ? .date : grouping }
}

extension LibrarySection {
    var isKind: Bool { if case .kind = self { true } else { false } }
}

/// The top of the list: the section's badge and name, and what's in it. (How it's grouped is in the toolbar: a control
/// here was measured again on every change to the list.)
struct ListHeader: View {
    @Bindable var model: LibraryModel
    let summary: LibraryModel.Summary

    var body: some View {
        let section = model.section
        HStack(alignment: .center, spacing: 14) {
            Group {
                if model.searching {
                    Image(systemName: "magnifyingglass")
                        .font(.system(size: 20, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: 46, height: 46)
                        .background(Color.brand.gradient, in: Circle())
                } else {
                    SectionBadge(section: section, size: 46)
                }
            }
            .shadow(color: (model.searching ? Color.brand : section.tint).opacity(0.35), radius: 8, y: 3)
            // No content transitions: AppKit draws them on the CPU every frame, which made typing in the search lag.
            VStack(alignment: .leading, spacing: 2) {
                Text(model.searching ? "Search" : section.title)
                    .font(.system(size: 26, weight: .bold, design: .rounded))
                Text(summaryText)
                    .font(.callout)
                    .foregroundStyle(Color.secondaryText)
                    .lineLimit(1)
            }
            Spacer(minLength: 12)
            if section == .done, !model.searching, summary.pages > 0 {
                // Clear glass with a red label, like the inspector's Delete: the list's green would fill it and dim the red.
                Button(role: .destructive) { model.confirmingEmptyDone = true } label: {
                    Label("Empty Done…", systemImage: "trash").foregroundStyle(Color.destructive)
                }
                .buttonStyle(.glass)
                .tint(nil)
                .help("Delete everything in Done (Edit ▸ Undo brings it back)")
            }
        }
        .padding(.leading, 6)
        .padding(.top, 8)
        .padding(.bottom, 4)
        .accessibilityElement(children: .contain)
    }

    private var summaryText: String {
        let pages = summary.pages == 1 ? "1 page" : "\(summary.pages) pages"
        if model.searching {
            let results = summary.pages == 1 ? "1 result" : "\(summary.pages) results"
            return "\(results) for “\(model.query)”"
        }
        guard summary.pages > 0 else { return "Nothing here yet" }
        var parts = [pages]
        if summary.newToday > 0, model.section != .done { parts.append("\(summary.newToday) new today") }
        parts.append(summary.sites == 1 ? "1 site" : "\(summary.sites) sites")
        return parts.joined(separator: " · ")
    }
}

/// A heading that floats over the list as it scrolls: a capsule of glass with the date, site or kind, its count, and a
/// chevron that folds it.
struct GroupHeader: View {
    let group: ItemGroup
    /// How the list is grouped; nil for a search, whose headings are Saved and Done.
    let grouping: LibraryGrouping?
    let folded: Bool
    let toggle: () -> Void

    var body: some View {
        HStack {
            Button(action: toggle) {
                HStack(spacing: 7) {
                    switch grouping {
                    case .site?: SiteTile(host: group.title, size: 20)
                    case .kind?:
                        if let kind = group.items.first?.kind {
                            Image(systemName: kind.badgeSymbol).font(.system(size: 11, weight: .semibold)).foregroundStyle(kind.labelColor)
                        }
                    default: EmptyView()
                    }
                    Text(group.title).font(.system(size: 13, weight: .bold))
                    Text(group.items.count, format: .number)
                        .font(.system(size: 12, weight: .semibold))
                        .monospacedDigit()
                        .foregroundStyle(Color.secondaryText)
                    Image(systemName: "chevron.down")
                        .font(.system(size: 9, weight: .heavy))
                        .foregroundStyle(.tertiary)
                        .rotationEffect(.degrees(folded ? -90 : 0))
                }
                .padding(.leading, grouping == .site ? 5 : 12)
                .padding(.trailing, 11)
                .padding(.vertical, grouping == .site ? 4 : 6)
                .glassEffect(.regular, in: .capsule)   // not .interactive(): its press effect made each heading costly to build
                .contentShape(.capsule)
            }
            .buttonStyle(.plain)
            .help(folded ? "Show these pages" : "Fold these pages away")
            .accessibilityLabel("\(group.title), \(group.items.count == 1 ? "1 page" : "\(group.items.count) pages")")
            .accessibilityValue(folded ? "Collapsed" : "Expanded")
            .accessibilityAddTraits(.isHeader)
            Spacer(minLength: 0)
        }
        .padding(.top, 14)
        .padding(.bottom, 4)
        // Pinned, the heading floats over the rows scrolling under it: they fade out behind it instead of crowding it.
        .background {
            LinearGradient(stops: [.init(color: .listGround, location: 0), .init(color: .listGround, location: 0.55),
                                   .init(color: .listGround.opacity(0), location: 1)], startPoint: .top, endPoint: .bottom)
                .padding(.horizontal, -16)
        }
    }
}

/// What an empty list says, by where the user is.
struct EmptyLibrary: View {
    let model: LibraryModel

    var body: some View {
        let section = model.section
        if model.searching {
            ContentUnavailableView.search(text: model.query)
        } else if section == .all, model.counts.isEmpty {   // nothing saved at all (the counts only list sections with pages)
            FirstRunEmptyState()
        } else {
            VStack(spacing: 12) {
                SectionBadge(section: section, size: 64)
                    .shadow(color: section.tint.opacity(0.35), radius: 14, y: 6)
                    .padding(.bottom, 4)
                Text(title).font(.system(size: 20, weight: .bold, design: .rounded))
                Text(message)
                    .font(.callout)
                    .foregroundStyle(Color.secondaryText)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 340)
                if section == .all, (model.counts[.older] ?? 0) > 0 {
                    Button("Show Older") { model.section = .older }
                        .buttonStyle(.glass)
                        .padding(.top, 6)
                }
            }
            .frame(maxWidth: .infinity)
        }
    }

    private var title: String {
        switch model.section {
        case .all: "All caught up"
        case .kind(let kind): kind.emptyTitle
        case .older: "Nothing older"
        case .done: "Nothing done yet"
        }
    }

    private var message: String {
        switch model.section {
        case .all: (model.counts[.older] ?? 0) > 0
            ? "Nothing from the last two weeks. Pages you haven't been back to wait in Older."
            : "Nothing from the last two weeks."
        case .kind(let kind): kind.emptyMessage
        case .older: "Pages you haven't been back to in two weeks move here. Later never deletes anything on its own."
        case .done: "Mark a page Done once you've dealt with it. It waits here until you empty Done."
        }
    }
}

/// The very first look at the window, before anything is saved: what Later is for, and what will show up.
struct FirstRunEmptyState: View {
    var body: some View {
        VStack(spacing: 0) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 104, height: 104)
                .shadow(color: Color.brand.opacity(0.3), radius: 20, y: 8)
                .accessibilityHidden(true)
            Text("Close tabs freely.")
                .font(.system(size: 28, weight: .bold, design: .rounded))
                .padding(.top, 18)
            Text("Later keeps the ones worth coming back to.")
                .font(.title3)
                .foregroundStyle(Color.secondaryText)
                .padding(.top, 4)
            GlassEffectContainer(spacing: 10) {
                HStack(spacing: 10) {
                    ForEach([Kind.shopping, .posts, .school, .reading, .videos, .tools]) { kind in
                        Label(kind.title, systemImage: kind.badgeSymbol)
                            .font(.callout.weight(.semibold))
                            .foregroundStyle(kind.labelColor)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 7)
                            .glassEffect(.regular.tint(kind.color.opacity(0.12)), in: .capsule)
                    }
                }
            }
            .accessibilityElement(children: .combine)
            .padding(.top, 26)
            Text("When a tab is on one specific thing and you've had it open a while, it shows up here, sorted by kind.")
                .font(.callout)
                .foregroundStyle(Color.secondaryText)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 360)
                .padding(.top, 22)
        }
        .padding(32)
        .frame(maxWidth: .infinity)
    }
}

extension Kind {
    var emptyTitle: String {
        switch self {
        case .shopping: "No shopping yet"
        case .posts: "No posts yet"
        case .school: "No schoolwork yet"
        case .reading: "Nothing to read yet"
        case .videos: "No videos yet"
        case .tools: "No tools yet"
        case .other: "Nothing else yet"
        }
    }

    var emptyMessage: String {
        switch self {
        case .shopping: "Product pages you spend a little time on show up here."
        case .posts: "Posts and threads you stop to read show up here."
        case .school: "Assignments and course pages you work on show up here."
        case .reading: "Articles and docs you spend time with show up here."
        case .videos: "Videos you watch for a while show up here."
        case .tools: "Sites, apps and projects you keep open show up here."
        case .other: "Specific pages that aren't shopping, posts, school, reading, videos or tools show up here."
        }
    }
}

/// A problem with saving, as a strip of glass across the top of the list until it's dismissed or fixed.
struct NoticeBar: View {
    let text: String
    let dismiss: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            WarningSymbol().font(.title3)
            Text(text)
            Spacer(minLength: 8)
            Button("Dismiss", action: dismiss).buttonStyle(.glass)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .glassEffect(.regular.tint(.orange.opacity(0.12)), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .padding(.vertical, 6)
    }
}

/// The one orange accent on warnings; the words stay in normal label colors so they read in light and dark.
struct WarningSymbol: View {
    var body: some View {
        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).accessibilityHidden(true)
    }
}
