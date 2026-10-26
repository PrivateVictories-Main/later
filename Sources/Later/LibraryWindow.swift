import AppKit
import LaterCore
import Observation
import SwiftUI

/// The library window: the system's split view (a floating glass sidebar, the list, a glass inspector) and toolbar,
/// around three SwiftUI panes. It's built on the first open and kept when closed, so it reopens at once, as the user left it.
@MainActor
final class LibraryWindow: NSObject, NSWindowDelegate, NSToolbarDelegate, NSSearchFieldDelegate {
    let model: LibraryModel
    private let openSettings: () -> Void
    private var window: NSWindow?
    private var searchItem: NSSearchToolbarItem?

    init(engine: Engine, undo: UndoManager, openSettings: @escaping () -> Void) {
        model = LibraryModel(engine: engine, undo: undo)
        self.openSettings = openSettings
        super.init()
        model.searchFieldText = { [weak self] text, focus in self?.setSearchField(text, focus: focus) }
        // The list's dates go by the day: they move on at midnight (also posted on waking past it) and with the time zone.
        let center = NotificationCenter.default
        center.addObserver(forName: .NSCalendarDayChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.model.now = .now }
        }
        center.addObserver(forName: .NSSystemTimeZoneDidChange, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                NSTimeZone.resetSystemTimeZone()   // the process keeps the old zone until told
                self?.model.now = .now
            }
        }
    }

    var isVisible: Bool { window?.isVisible == true }

    func show(activating: Bool = true) {
        let start = ContinuousClock.now
        #if DEBUG
        let faults = Perf.faults()
        #endif
        if !model.isVisible {
            AppWindows.didOpen()
            model.wake()   // redraws the panes only if what they show changed while hidden
            model.engine.icons.update()
            model.engine.readSwitchOnlyBrowsers()   // Chrome can't say when its tabs change; look when the user comes to Later
        }
        if window == nil { build() }
        guard let window else { return }
        if activating { NSApp.activate() }
        window.makeKeyAndOrderFront(nil)
        DispatchQueue.main.async {   // next run-loop turn: the first frame is on screen
            Perf.log.info("window Later open=\(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)ms")
            #if DEBUG
            let now = Perf.faults()
            Perf.log.info("window Later faults=\(now.faults - faults.faults, privacy: .public) pageins=\(now.pageins - faults.pageins, privacy: .public) focus=\(self.focusedPane, privacy: .public)")
            #endif
        }
    }

    /// Closing only hides the window; everything in it stays ready for the next open.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        hide()
        return false
    }

    func hide() {
        guard let window, model.isVisible else { return }
        window.orderOut(nil)
        model.isVisible = false   // not observed: hiding redraws nothing
        AppWindows.didClose()
    }

    func focusSearch() { searchItem?.beginSearchInteraction() }

    func show(section: LibrarySection) {
        model.setSearch("")
        model.section = section
    }

    // MARK: Building

    /// In one pass: the window's frame and toolbar first, then the panes at the window's size, so nothing is laid out
    /// twice or at a size it doesn't end up at.
    private func build() {
        #if DEBUG
        // Before the panes exist, so they first draw this state rather than change to it.
        if let section = Sample.section { model.section = section }
        if let grouping = Sample.grouping { model.grouping = grouping }
        if let sort = Sample.sort { model.sort = sort }
        model.sortReversed = Sample.sortReversed
        if let search = Sample.search { model.searchChanged(search) }
        if Sample.select > 0 { model.selection.selectAll(Array(model.listing.rowIDs.prefix(Sample.select))) }
        #endif
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1120, height: 720),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                              backing: .buffered, defer: false)
        window.contentMinSize = NSSize(width: 940, height: 520)
        window.title = "Later"
        window.titleVisibility = .hidden   // the list's own header names the section
        window.toolbar = toolbar(identifier: "LaterLibrary")
        window.toolbarStyle = .unified
        window.titlebarSeparatorStyle = .none   // the list scrolls up under the toolbar's glass instead
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        #if DEBUG
        if let origin = Sample.origin {
            window.setFrameTopLeftPoint(NSPoint(x: origin.x, y: (NSScreen.screens.first?.frame.height ?? 0) - origin.y))
        }
        #endif
        if !Sample.enabled { window.setFrameAutosaveName("LaterWindow") }

        let split = NSSplitViewController()
        let sidebar = NSSplitViewItem(sidebarWithViewController: pane(SidebarPane(model: model, openSettings: openSettings)))
        sidebar.minimumThickness = 200
        sidebar.maximumThickness = 300
        let list = NSSplitViewItem(viewController: pane(ListPane(model: model)))
        list.minimumThickness = 440
        list.automaticallyAdjustsSafeAreaInsets = true   // the list's ground runs on under the floating sidebar
        let inspector = NSSplitViewItem(inspectorWithViewController: pane(InspectorPane(model: model)))
        inspector.minimumThickness = 290
        inspector.maximumThickness = 420
        split.splitViewItems = [sidebar, list, inspector]
        if !Sample.enabled { split.splitView.autosaveName = "LaterPanes" }   // a sample run saves no setting, not even these
        split.view.frame = window.contentView?.bounds ?? .zero   // attached at the window's size: no resizing to fit
        window.contentViewController = split
        self.window = window
        followTitle()
        #if DEBUG
        if let search = Sample.search { setSearchField(search, focus: false) }
        #endif
    }

    private func pane(_ view: some View) -> NSViewController {
        let host = NSHostingController(rootView: view.environment(model.engine.icons))
        host.sizingOptions = []   // the split view sizes the panes; nothing in them sizes the window
        return host
    }

    private func toolbar(identifier: String) -> NSToolbar {
        let toolbar = NSToolbar(identifier: identifier)
        toolbar.delegate = self
        toolbar.displayMode = .iconOnly
        toolbar.allowsUserCustomization = false
        return toolbar
    }

    /// The window's title isn't shown, but VoiceOver and Mission Control read it.
    private func followTitle() {
        window?.title = model.searching ? "Search" : model.section.title
        withObservationTracking { _ = (model.searching, model.section) } onChange: { [weak self] in
            Task { @MainActor in self?.followTitle() }
        }
    }

    /// A login launch shows nothing, so the first open would pay for building and laying out the whole window, and for
    /// loading SwiftUI, the glass and the split view. This does that once, with nothing shown, then keeps the window as
    /// if it had been closed, for the memory a closed window keeps (see the report).
    func warmUp() {
        guard window == nil else { return }
        let start = ContinuousClock.now
        build()
        window?.contentView?.layoutSubtreeIfNeeded()
        #if DEBUG
        // A test copy can start hidden; a login launch isn't, so a test's first open should show as a real one would.
        if Sample.enabled, NSApp.isHidden { NSApp.unhideWithoutActivation() }
        #endif
        DispatchQueue.main.async { _ = malloc_zone_pressure_relief(nil, 0) }   // what building it freed, as after a close
        Perf.log.info("warm-up ms=\(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)")
    }

    #if DEBUG
    /// Development only: which pane has the keyboard, for the open log.
    private var focusedPane: String {
        guard let split = window?.contentViewController as? NSSplitViewController,
              let view = window?.firstResponder as? NSView else { return "none" }
        return zip(["sidebar", "list", "inspector"], split.splitViewItems).first { view.isDescendant(of: $1.viewController.view) }?.0
            ?? (view is NSText ? "search" : "other")
    }

    /// Development only: scrolls the list down for two seconds, 14 points a frame at 120 frames a second (a quick flick
    /// on a trackpad), straight through its scroll view so no input is needed, and logs how the frames' work spread.
    func scrollTest() {
        guard let split = window?.contentViewController as? NSSplitViewController,
              let scrollView = Self.scrollView(in: split.splitViewItems[1].viewController.view) else { return }
        Perf.frame("scroll", for: 2.1, summary: true)
        Task {
            for _ in 0..<240 {
                let clip = scrollView.contentView
                clip.scroll(to: NSPoint(x: clip.bounds.origin.x, y: clip.bounds.origin.y + 14))
                scrollView.reflectScrolledClipView(clip)
                try? await Task.sleep(for: .seconds(1.0 / 120))
            }
        }
    }

    private static func scrollView(in view: NSView) -> NSScrollView? {
        if let scrollView = view as? NSScrollView { return scrollView }
        return view.subviews.lazy.compactMap(scrollView(in:)).first
    }
    #endif

    // MARK: Toolbar and search

    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.toggleSidebar, .sidebarTrackingSeparator, .flexibleSpace, .grouping, .sortBy, .search, .inspectorTrackingSeparator]
    }

    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        toolbarDefaultItemIdentifiers(toolbar)
    }

    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        if id == .grouping {
            // Finder's Group By: a pull-down, checked and enabled the same way as the View menu's (AppDelegate validates both).
            let item = NSMenuToolbarItem(itemIdentifier: id)
            item.image = NSImage(systemSymbolName: "square.grid.3x1.below.line.grid.1x2", accessibilityDescription: "Group By")
            item.label = "Group By"
            item.toolTip = "Group these pages by date, site or kind"
            item.menu = NSMenu()
            item.menu.addItem(withTitle: "Group By", action: nil, keyEquivalent: "")   // a pull-down's first item is its title, never listed
            for (index, grouping) in LibraryGrouping.allCases.enumerated() {
                item.menu.addItem(withTitle: grouping.title, action: #selector(AppDelegate.setGrouping(_:)), keyEquivalent: "").tag = index
            }
            return item
        }
        if id == .sortBy {
            // The same way: a pull-down whose first item is its title and never listed.
            let item = NSMenuToolbarItem(itemIdentifier: id)
            item.image = NSImage(systemSymbolName: "arrow.up.arrow.down", accessibilityDescription: "Sort By")
            item.label = "Sort By"
            item.toolTip = "Order these pages by tab position, date, title or site"
            item.menu = NSMenu()
            item.menu.addItem(withTitle: "Sort By", action: nil, keyEquivalent: "")
            Self.addSortItems(to: item.menu)
            return item
        }
        guard id == .search else { return nil }
        let item = NSSearchToolbarItem(itemIdentifier: id)
        item.preferredWidthForSearchField = 280
        let field = item.searchField
        field.placeholderString = "Search"
        field.delegate = self
        field.target = self
        field.action = #selector(searchFieldChanged(_:))   // also sent by its clear button and by Esc
        searchItem = item
        return item
    }

    /// The Sort By choices, for the toolbar's pull-down and the View menu alike (AppDelegate checks and enables both).
    static func addSortItems(to menu: NSMenu) {
        func add(_ sort: LibrarySort) {
            menu.addItem(withTitle: sort.title, action: #selector(AppDelegate.setSort(_:)), keyEquivalent: "").tag = LibrarySort.allCases.firstIndex(of: sort)!
        }
        add(.tabOrder); add(.tabOrderReversed)
        menu.addItem(.separator())
        add(.dateOpened); add(.dateSaved); add(.lastSeen)
        menu.addItem(.separator())
        add(.title); add(.site)
        menu.addItem(.separator())
        // Titled for the current sort when shown (see AppDelegate.validateMenuItem).
        menu.addItem(withTitle: "Newest First", action: #selector(AppDelegate.setSortDirection(_:)), keyEquivalent: "").tag = 0
        menu.addItem(withTitle: "Oldest First", action: #selector(AppDelegate.setSortDirection(_:)), keyEquivalent: "").tag = 1
    }

    /// Every keystroke, so the list filters as the user types. Typing undo stays the field editor's own.
    func controlTextDidChange(_ notification: Notification) {
        if let field = notification.object as? NSSearchField { model.searchChanged(field.stringValue) }
    }

    @objc private func searchFieldChanged(_ field: NSSearchField) { model.searchChanged(field.stringValue) }

    private func setSearchField(_ text: String, focus: Bool) {
        guard let item = searchItem else { return }
        let field = item.searchField
        if field.stringValue != text { field.stringValue = text }
        guard focus else { return }
        item.beginSearchInteraction()
        // Typing went on from the list: carry on after what's there instead of replacing it.
        field.currentEditor()?.selectedRange = NSRange(location: (text as NSString).length, length: 0)
    }
}

private extension NSToolbarItem.Identifier {
    static let search = NSToolbarItem.Identifier("search")
    static let grouping = NSToolbarItem.Identifier("grouping")
    static let sortBy = NSToolbarItem.Identifier("sortBy")
}
