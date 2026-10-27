import AppKit
import LaterCore
import ServiceManagement

let arguments = CommandLine.arguments

// The agent must not become an application as far as LaunchServices is concerned (no NSApplication): otherwise opening
// Later while it runs would be sent to the agent, which has the same bundle ID, instead of starting the window app.
if arguments.contains("--agent") {
    let agent = AgentDelegate()
    agent.start()
    dispatchMain()
}

let app = NSApplication.shared

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

// `Later --agent-off` and `--agent-on` change the background agent's registration from a shell, then exit.
if arguments.contains("--agent-off") {
    try? SMAppService.agent(plistName: LoginItem.plistName).unregister()
    exit(0)
}
if arguments.contains("--agent-on") {
    try? SMAppService.agent(plistName: LoginItem.plistName).register()
    exit(0)
}

if arguments.contains("--verify") {
    app.setActivationPolicy(.accessory)
    let verifier = Verifier()
    verifier.start()
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
