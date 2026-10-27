import Foundation
import Security
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
        repair?.cancel()
        on ? start() : change(false)
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
        let wanted = defaults.bool(forKey: Self.wantedKey)
        if wanted, status == .notRegistered || status == .notFound {
            start()
        } else if Self.needsRepair(wanted: wanted, status: status, pinned: defaults.string(forKey: Self.pinKey), current: Self.codeHash) {
            try? Self.service.unregister()
            start()
        }
        refresh()
    }

    /// launchd starts the agent only if the program still has the code hash it had when the agent was registered, and a
    /// replaced app (any new build) no longer does (measured: OS_REASON_CODESIGNING). launchd notices at the failed start
    /// and refreshes the hash within about ten seconds, but registering before that keeps the old hash, so a build that
    /// isn't the one recorded in `pinKey` registers, then registers again 15 s later and records itself. Registering
    /// again is not a new item, so macOS shows no banner. If Later quits before the second pass, the next launch repeats it.
    private static let pinKey = "agentRegisteredHash"
    private var repair: Task<Void, Never>?

    private func start() {
        let new = UserDefaults.standard.string(forKey: Self.pinKey) != Self.codeHash
        change(true)
        guard new, failure == nil else { return }
        repair = Task {
            try? await Task.sleep(for: .seconds(15))
            guard !Task.isCancelled else { return }
            try? await Self.service.unregister()
            change(true)
            if failure == nil { UserDefaults.standard.set(Self.codeHash, forKey: Self.pinKey) }
        }
    }

    nonisolated static func needsRepair(wanted: Bool, status: SMAppService.Status, pinned: String?, current: String?) -> Bool {
        wanted && status == .enabled && current != nil && pinned != current
    }

    /// The code hash (cdhash) of the running program.
    private static var codeHash: String? {
        var code: SecCode?, staticCode: SecStaticCode?, info: CFDictionary?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code,
              SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode,
              SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let hash = (info as? [String: Any])?[kSecCodeInfoUnique as String] as? Data else { return nil }
        return hash.map { String(format: "%02x", $0) }.joined()
    }

    func refresh() { status = Self.service.status }
}
