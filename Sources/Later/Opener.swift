import AppKit
import LaterCore

/// Opens a saved link as a new tab. Only ever called from the user's click.
enum Opener {
    /// Where a link opens: the preferred browser if it's running, else any running one, else the first installed
    /// (Helium first). Nil when none is installed.
    static func browser(preferred: Browser?) -> Browser? {
        let order = (preferred.map { [$0] } ?? []) + Browser.allCases
        return order.first { BrowserReader.mainInstance(of: $0) != nil } ?? order.first { $0.isInstalled }
    }

    static func open(_ link: String, preferred: Browser?) {
        guard let browser = browser(preferred: preferred) else {
            if let url = URL(string: link) { NSWorkspace.shared.open(url) }
            return
        }
        open(link, in: browser)
    }

    static func open(_ link: String, in browser: Browser) {
        guard let url = URL(string: link),
              let app = browser.appURL else { return }
        NSWorkspace.shared.open([url], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
    }
}
