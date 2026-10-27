import AppKit
import LaterCore
import SwiftUI
import notify

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuItemValidation {
    /// Created only once the engine lock is held (see takeEngineLock), because creating it loads the list.
    lazy var engine = Engine(readOnly: readOnly)
    /// The agent never handed the list over: the engine only shows it (see takeEngineLock).
    private var readOnly = false
    /// Held for as long as this process lives; the kernel frees it when Later quits or dies, and the agent resumes.
    private var engineLock: EngineLock?
    /// nil until macOS sends the launch 'oapp' event; then whether that event marks a login launch.
    private var loginLaunch: Bool?
    /// Kept in memory for sample runs, so they never write the real setting.
    private var onboarded = Sample.enabled ? !Sample.onboarding : UserDefaults.standard.bool(forKey: "onboarded")
    /// The list's undo history: Delete, Not an Idea, Mark Done, Move Back, Move To and Empty Done. It belongs to no
    /// window, because AppKit empties a window's undo manager when the window closes.
    private let deleteUndo: UndoManager = {
        let manager = UndoManager()
        manager.levelsOfUndo = 50
        return manager
    }()

    private lazy var libraryWindow = LibraryWindow(engine: engine, undo: deleteUndo) { [unowned self] in self.openSettings() }

    private lazy var settingsWindow = HostedWindow(title: "Later Settings", size: NSSize(width: 520, height: 520),
                                                   resizable: false) { [unowned self] in
        AnyView(SettingsView(engine: self.engine, model: self.libraryWindow.model))
    }

    private lazy var onboardingWindow = HostedWindow(title: "Welcome to Later", size: NSSize(width: 600, height: 780),
                                                     resizable: false, transparentTitlebar: true) { [unowned self] in
        AnyView(OnboardingView(engine: self.engine) { [unowned self] in self.finishOnboarding() })
    }

    /// AppKit dispatches the launch event after this and before applicationDidFinishLaunching.
    /// Replacing AppKit's 'oapp' handler is fine: Later opens no documents, and launching still finishes (checked with a probe).
    func applicationWillFinishLaunching(_ notification: Notification) {
        NSAppleEventManager.shared().setEventHandler(self, andSelector: #selector(openApplication(_:reply:)),
                                                     forEventClass: AEEventClass(kCoreEventClass),
                                                     andEventID: AEEventID(kAEOpenApplication))
        takeEngineLock()
    }

    /// Only one process watches and writes the list. If the background agent has it, ask it to hand over and wait for it
    /// (it answers in a few milliseconds; nothing polls on its side). With no agent running, the lock is simply free.
    private func takeEngineLock() {
        guard !Sample.enabled else { return }
        guard let lock = Handoff.lock() else {
            Perf.log.error("no lock file; carrying on")   // the agent exits in the same case, so no other process is watching
            return
        }
        engineLock = lock
        let start = ContinuousClock.now
        if !lock.acquire(timeout: 0) {
            // Asked again every 0.25 s: a request that reaches the agent just before it knows it holds the lock is ignored.
            // Without the lock the app never watches or saves; an unresponsive agent leaves it showing the list read-only.
            if !lock.acquire(timeout: 5, nudgeEvery: 0.25, nudge: { notify_post(Handoff.notification) }) {
                readOnly = true
                Perf.log.error("handoff timed out; showing the list read-only")
            }
            Perf.log.info("handoff waited \(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)ms")
        }
        Perf.log.info("app took the list items=\(self.engine.library.data.items.count, privacy: .public)")
    }

    // 'lgit' marks a launch by 1.2's login item, which the first launch of 1.3 removes; the agent has no window to open.
    @objc private func openApplication(_ event: NSAppleEventDescriptor, reply: NSAppleEventDescriptor) {
        loginLaunch = event.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        installMainMenu()
        #if DEBUG
        listenForDevCommands()
        if Sample.dark { NSApp.appearance = NSAppearance(named: .darkAqua) }
        if Sample.settings { openSettings() }
        #endif
        if onboarded {
            // The copy in /Applications registers the background agent, even if onboarding ran from a development build.
            LoginItem.shared.applyDefault()
            engine.start()
            if loginLaunch == false, !Sample.noWindow, !arguments.contains("--hidden") {
                libraryWindow.show()
            } else {
                // At login (or with no launch event at all) Later stays out of sight, but gets ready to open fast.
                DispatchQueue.main.async { self.libraryWindow.warmUp() }
            }
        } else {
            onboardingWindow.show()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if onboarded { libraryWindow.show() } else { onboardingWindow.show() }
        return false
    }

    /// A window shown while Later wasn't allowed to come forward (`open -g`) isn't key when Later later activates,
    /// so ⌘W and typing would reach nothing. Make the front window key.
    func applicationDidBecomeActive(_ notification: Notification) {
        if NSApp.keyWindow == nil { NSApp.orderedWindows.first { $0.isVisible && $0.canBecomeKey }?.makeKey() }
    }

    /// The window menu commands act on: the key, main or front window that is visible, titled and closable (Later can be
    /// inactive while its menus are used, e.g. through Accessibility, and then has no key window; AppKit and SwiftUI
    /// also keep untitled helper windows around, which must not be picked).
    private var commandWindow: NSWindow? {
        ([NSApp.keyWindow, NSApp.mainWindow].compactMap { $0 } + NSApp.orderedWindows)
            .first { $0.isVisible && $0.styleMask.isSuperset(of: [.titled, .closable]) }
    }

    /// Typing in a text field undoes the typing; everything else undoes changes to the list, with or without a window.
    /// The field editor keeps its own undo history, separate from its window's.
    private func undoManager(redo: Bool) -> UndoManager {
        if let editor = commandWindow?.firstResponder as? NSTextView, let typing = editor.undoManager,
           redo ? typing.canRedo : typing.canUndo {
            return typing
        }
        return deleteUndo
    }

    @objc func closeWindow(_ sender: Any?) { commandWindow?.performClose(nil) }

    @objc func undo(_ sender: Any?) { undoManager(redo: false).undo() }
    @objc func redo(_ sender: Any?) { undoManager(redo: true).redo() }

    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(AppDelegate.undo(_:)):
            let manager = undoManager(redo: false)
            item.title = manager.undoMenuItemTitle   // plain "Undo" when there's nothing to undo
            return manager.canUndo
        case #selector(AppDelegate.redo(_:)):
            let manager = undoManager(redo: true)
            item.title = manager.redoMenuItemTitle
            return manager.canRedo
        case #selector(AppDelegate.closeWindow(_:)):
            return commandWindow != nil
        case #selector(AppDelegate.find(_:)), #selector(AppDelegate.showSection(_:)):
            return libraryWindow.isVisible
        case #selector(AppDelegate.setGrouping(_:)):
            let model = libraryWindow.model, grouping = LibraryGrouping.allCases[item.tag]
            item.state = model.effectiveGrouping == grouping && model.groupingActive ? .on : .off
            return libraryWindow.isVisible && model.groupingActive && !(grouping == .kind && model.section.isKind)
        case #selector(AppDelegate.setSort(_:)):
            item.state = libraryWindow.model.sort == LibrarySort.allCases[item.tag] ? .on : .off
            return libraryWindow.isVisible
        case #selector(AppDelegate.setSortDirection(_:)):
            let model = libraryWindow.model, titles = model.sort.directionTitles
            if let titles { item.title = item.tag == 0 ? titles.forward : titles.reversed }
            item.state = titles != nil && model.sortReversed == (item.tag == 1) ? .on : .off
            return libraryWindow.isVisible && titles != nil
        default:
            return true
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// Quitting waits for the list to be written. If that fails, ask rather than lose changes silently.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        while true {
            do {
                try engine.flush()
                return .terminateNow
            } catch {
                NSApp.activate()   // only to show this alert
                let alert = NSAlert()
                alert.messageText = "Later couldn't save its latest changes."
                alert.informativeText = error.localizedDescription
                alert.addButton(withTitle: "Try Again")
                alert.addButton(withTitle: "Quit Anyway")
                if alert.runModal() == .alertSecondButtonReturn { return .terminateNow }
            }
        }
    }

    /// Later's own menus, shown in the menu bar only while its window is in front (like any app).
    /// Nothing stays in the menu bar. Without these, ⌘, ⌘Q ⌘W and copy/paste in search wouldn't work.
    private func installMainMenu() {
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Settings…", action: #selector(openSettings), keyEquivalent: ",").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit Later", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let fileMenu = NSMenu(title: "File")
        fileMenu.addItem(withTitle: "Close Window", action: #selector(closeWindow(_:)), keyEquivalent: "w").target = self
        let editMenu = NSMenu(title: "Edit")
        // Always this delegate, which picks the typing or the list's history (see undo(_:)).
        editMenu.addItem(withTitle: "Undo", action: #selector(AppDelegate.undo(_:)), keyEquivalent: "z").target = self
        editMenu.addItem(withTitle: "Redo", action: #selector(AppDelegate.redo(_:)), keyEquivalent: "Z").target = self
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Find…", action: #selector(find(_:)), keyEquivalent: "f").target = self
        let viewMenu = NSMenu(title: "View")
        for (index, section) in LibrarySection.ordered.enumerated() {
            let item = viewMenu.addItem(withTitle: section.title, action: #selector(showSection(_:)), keyEquivalent: "\(index + 1)")
            item.target = self
            item.tag = index
        }
        viewMenu.addItem(.separator())
        for (index, grouping) in LibraryGrouping.allCases.enumerated() {
            let item = viewMenu.addItem(withTitle: "Group by \(grouping.title)", action: #selector(setGrouping(_:)), keyEquivalent: "")
            item.target = self
            item.tag = index
        }
        let sortMenu = NSMenu(title: "Sort By")
        LibraryWindow.addSortItems(to: sortMenu)
        viewMenu.addItem(withTitle: "Sort By", action: nil, keyEquivalent: "").submenu = sortMenu
        viewMenu.addItem(.separator())
        // Sent along the responder chain to the window's split view, which keeps their titles in step.
        viewMenu.addItem(withTitle: "Show Sidebar", action: #selector(NSSplitViewController.toggleSidebar(_:)), keyEquivalent: "s")
            .keyEquivalentModifierMask = [.control, .command]
        viewMenu.addItem(withTitle: "Show Inspector", action: #selector(NSSplitViewController.toggleInspector(_:)), keyEquivalent: "i")
            .keyEquivalentModifierMask = [.option, .command]
        let main = NSMenu()
        for menu in [appMenu, fileMenu, editMenu, viewMenu] {
            let item = NSMenuItem()
            item.submenu = menu
            main.addItem(item)
        }
        NSApp.mainMenu = main
    }

    @objc private func openSettings() { settingsWindow.show() }

    @objc func find(_ sender: Any?) { libraryWindow.focusSearch() }
    @objc func showSection(_ sender: NSMenuItem) { libraryWindow.show(section: LibrarySection.ordered[sender.tag]) }
    @objc func setGrouping(_ sender: NSMenuItem) { libraryWindow.model.grouping = LibraryGrouping.allCases[sender.tag] }
    @objc func setSort(_ sender: NSMenuItem) { libraryWindow.model.sort = LibrarySort.allCases[sender.tag] }
    @objc func setSortDirection(_ sender: NSMenuItem) { libraryWindow.model.sortReversed = sender.tag == 1 }

    #if DEBUG
    /// Development only: show or hide the library window from a shell without bringing Later forward
    /// (`notifyutil -p com.ryan.later.dev.show`, or `.hide`), to time opening with no window-reading tool attached.
    /// Also `.down` (the down arrow), `.type` (the next letter of "walnut" in the search, then clear) and `.section` (the
    /// next section), `.group` (the next grouping), each timed to its frame (see Perf.frame), and `.scroll` (two seconds
    /// of scrolling down the list).
    private var devTokens: [Int32] = [0, 0, 0, 0, 0, 0, 0, 0]
    private var devTyped = 0
    private func listenForDevCommands() {
        let commands: [(String, @MainActor (AppDelegate) -> Void)] = [
            ("show", {
                Perf.frame("show")
                $0.libraryWindow.show(activating: false)
            }),
            ("hide", {
                Perf.frame("hide")
                $0.libraryWindow.hide()
            }),
            ("down", {
                Perf.frame("down")
                $0.libraryWindow.model.move(by: 1, extending: false, toEnd: false)
            }),
            ("type", {
                let word = Array("walnut"), model = $0.libraryWindow.model
                $0.devTyped = ($0.devTyped + 1) % (word.count + 1)
                Perf.frame("type")
                model.searchChanged($0.devTyped == 0 ? "" : String(word[..<$0.devTyped]))
            }),
            ("section", {
                let model = $0.libraryWindow.model
                let next = LibrarySection.ordered.firstIndex(of: model.section).map { ($0 + 1) % LibrarySection.ordered.count } ?? 0
                Perf.frame("section")
                model.section = LibrarySection.ordered[next]
            }),
            ("group", {
                let model = $0.libraryWindow.model, all = LibraryGrouping.allCases
                Perf.frame("group")
                model.grouping = all[((all.firstIndex(of: model.grouping) ?? 0) + 1) % all.count]
            }),
            ("scroll", { $0.libraryWindow.scrollTest() }),
            ("add", { $0.engine.addDevPage() }),
        ]
        for (index, (name, command)) in commands.enumerated() {
            notify_register_dispatch("com.ryan.later.dev." + name, &devTokens[index], .main) { [weak self] _ in
                MainActor.assumeIsolated { if let self { command(self) } }
            }
        }
    }
    #endif

    private func finishOnboarding() {
        onboarded = true
        if !Sample.enabled { UserDefaults.standard.set(true, forKey: "onboarded") }
        LoginItem.shared.applyDefault()   // outside /Applications, the installed copy applies it on first launch
        engine.start()
        libraryWindow.show()   // before closing, so the window count never hits zero and the Dock icon doesn't flicker
        onboardingWindow.close()
    }
}
