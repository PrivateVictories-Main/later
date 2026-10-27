import AppKit
import LaterCore
import ServiceManagement

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

// Development only: takes the development agent back out of Login Items after a dry run.
if arguments.contains("--dev-agent-off") {
    try? SMAppService.agent(plistName: LoginItem.plistName).unregister()
    exit(0)
}
#endif

if arguments.contains("--verify") {
    app.setActivationPolicy(.accessory)
    let verifier = Verifier()
    verifier.start()
    app.run()
} else if arguments.contains("--agent") {
    app.setActivationPolicy(.prohibited)   // no Dock tile, no windows, no menu
    let delegate = AgentDelegate()
    app.delegate = delegate
    app.run()
} else {
    #if DEBUG
    // Development only: skips onboarding (which needs a click) so a dry run can register the development agent.
    if arguments.contains("--dev-agent") { UserDefaults.standard.set(true, forKey: "onboarded") }
    #endif
    let delegate = AppDelegate()
    app.delegate = delegate
    app.run()
}
