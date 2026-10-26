import AppKit
import LaterCore

let app = NSApplication.shared
let arguments = CommandLine.arguments

#if DEBUG
// Development only: dark mode for visual checks without touching the system appearance.
if arguments.contains("--dark") { app.appearance = NSAppearance(named: .darkAqua) }

if arguments.contains("--reset") {
    // Development only: deletes the saved list, settings, kept site icons and window state (inside the running build's
    // own container) after test runs, so nothing of the sites it saw stays behind. Compiled out of release builds, where
    // a stray argument would wipe the user's list.
    try? LibraryStore.appDefault().reset()
    if let domain = Bundle.main.bundleIdentifier {
        UserDefaults.standard.removePersistentDomain(forName: domain)
        let library = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        try? FileManager.default.removeItem(at: library.appendingPathComponent("Saved Application State/\(domain).savedState"))
    }
    if let icons = SiteIcons.folder { try? FileManager.default.removeItem(at: icons) }
    exit(0)
}
#endif

if arguments.contains("--verify") {
    app.setActivationPolicy(.accessory)
    let verifier = Verifier()
    verifier.start()
    app.run()
} else {
    let delegate = AppDelegate()
    app.delegate = delegate
    app.run()
}
