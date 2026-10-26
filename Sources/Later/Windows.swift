import AppKit
import SwiftUI

/// The Dock icon shows while any Later window is open, and goes away when the last one closes.
@MainActor
enum AppWindows {
    private static var openCount = 0

    static func didOpen() {
        openCount += 1
        if openCount == 1 { NSApp.setActivationPolicy(.regular) }
    }

    static func didClose() {
        openCount = max(0, openCount - 1)
        guard openCount == 0 else { return }
        NSApp.setActivationPolicy(.accessory)
        // The closed window's views are freed by the next turn; hand those pages back now instead of keeping them
        // until the system runs short (otherwise ~25 MB stays counted against Later while it sits in the background).
        DispatchQueue.main.async { _ = malloc_zone_pressure_relief(nil, 0) }
    }
}

/// An AppKit window that hosts a SwiftUI view, created on demand and fully released on close,
/// so memory drops back when the user closes it.
@MainActor
final class HostedWindow: NSObject, NSWindowDelegate {
    private let title: String
    private let size: NSSize
    private let resizable: Bool
    private let autosaveName: String?
    /// The content runs up under an invisible title bar, for a welcome screen that leads with its own artwork.
    private let transparentTitlebar: Bool
    private let content: @MainActor () -> AnyView
    private var window: NSWindow?

    init(title: String, size: NSSize, resizable: Bool, autosaveName: String? = nil, transparentTitlebar: Bool = false,
         content: @escaping @MainActor () -> AnyView) {
        self.title = title
        self.size = size
        self.resizable = resizable
        self.autosaveName = autosaveName
        self.transparentTitlebar = transparentTitlebar
        self.content = content
    }

    func show() {
        let start = ContinuousClock.now
        if window == nil {
            var style: NSWindow.StyleMask = [.titled, .closable, .miniaturizable, .fullSizeContentView]
            if resizable { style.insert(.resizable) }
            let host = NSHostingController(rootView: content())
            host.sceneBridgingOptions = [.title, .toolbars]
            let newWindow = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: style,
                                     backing: .buffered, defer: false)
            newWindow.title = title
            newWindow.contentViewController = host
            newWindow.setContentSize(size)
            newWindow.toolbarStyle = .unified
            if transparentTitlebar {
                newWindow.titlebarAppearsTransparent = true
                newWindow.titleVisibility = .hidden
                newWindow.isMovableByWindowBackground = true
            }
            newWindow.isReleasedWhenClosed = false
            newWindow.delegate = self
            newWindow.center()
            if let autosaveName { newWindow.setFrameAutosaveName(autosaveName) }
            window = newWindow
            AppWindows.didOpen()
        }
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
        let title = self.title
        DispatchQueue.main.async {   // next run-loop turn: the first frame is on screen
            Perf.log.info("window \(title, privacy: .public) open=\(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)ms")
        }
    }

    func close() {
        window?.close()
    }

    func windowWillClose(_ notification: Notification) {
        window?.contentViewController = nil
        window = nil
        AppWindows.didClose()
    }
}
