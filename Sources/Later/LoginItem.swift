import Foundation
import ServiceManagement

/// "Keep watching when Later is closed": registers the background agent (`Later --agent`, see Agent.swift) with macOS, which
/// starts it at login and keeps it running. Only the copy installed in /Applications registers, so development builds never
/// add a login item or trigger background-item alerts. One shared state, so Settings shows what the automatic attempts did too.
@MainActor @Observable
final class LoginItem {
    static let shared = LoginItem()
    /// Whether the user wants the agent: set by their own toggle, or carried over from 1.2's "Open at login".
    static let wantedKey = "keepWatching"
    /// A development build has its own agent (own label and plist) and registers it only when asked with `--dev-agent`.
    #if DEBUG
    static let plistName = "com.ryan.later.dev.agent.plist"
    static let devAgent = CommandLine.arguments.contains("--dev-agent")
    #else
    static let plistName = "com.ryan.later.agent.plist"
    static let devAgent = false
    #endif
    static var isInstalled: Bool { Bundle.main.bundlePath.hasPrefix("/Applications/") || devAgent }
    private static var service: SMAppService { .agent(plistName: plistName) }
    private(set) var status = LoginItem.service.status
    /// Why macOS refused the last change; nil once a change goes through.
    private(set) var failure: String?

    func setEnabled(_ on: Bool) {
        guard Self.isInstalled, !Sample.enabled else { return }   // a sample run changes nothing, this included
        UserDefaults.standard.set(on, forKey: Self.wantedKey)
        change(on)
    }

    private func change(_ on: Bool) {
        do {
            if on { try Self.service.register() } else { try Self.service.unregister() }
            failure = nil
        } catch {
            Perf.log.error("login item change failed")   // the reason shows in Settings; logs carry no payload
            failure = error.localizedDescription
        }
        refresh()
    }

    /// At every launch of the installed copy. Moves 1.2's login item (the window app itself) to the agent, keeping its
    /// on or off, and registers it if macOS dropped it.
    func applyDefault() {
        guard Self.isInstalled, !Sample.enabled else { return }
        refresh()
        let old = SMAppService.mainApp
        let hadOld = old.status == .enabled || old.status == .requiresApproval
        if hadOld { try? old.unregister() }
        let defaults = UserDefaults.standard
        if defaults.object(forKey: Self.wantedKey) == nil {
            // 1.2 recorded that it had decided (by default or by the toggle); not enabled since means the user turned it off.
            defaults.set(hadOld || !defaults.bool(forKey: "loginDefaultApplied"), forKey: Self.wantedKey)
        }
        if defaults.bool(forKey: Self.wantedKey), status == .notRegistered || status == .notFound { change(true) }
        refresh()
    }

    func refresh() { status = Self.service.status }
}
