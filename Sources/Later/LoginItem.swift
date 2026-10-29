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
        on ? start(fresh: Self.isUnregistered) : change(false)
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
    /// `helperMissing`: no helper held the list when Later opened, though watching in the background is wanted (see
    /// `helperWasMissing`). The registration is then repeated, even when it already pins this build.
    func applyDefault(helperMissing: Bool = false) {
        guard Self.isInstalled, !Sample.enabled else { return }
        refresh()
        let old = SMAppService.mainApp
        let hadOld = old.status == .enabled || old.status == .requiresApproval
        if hadOld { try? old.unregister() }
        let defaults = UserDefaults.standard
        if defaults.object(forKey: Self.wantedKey) == nil {
            defaults.set(Self.initiallyWanted(oldItemWasOn: hadOld, decidedBefore: defaults.bool(forKey: "loginDefaultApplied")),
                         forKey: Self.wantedKey)
        }
        let wanted = defaults.bool(forKey: Self.wantedKey)
        if helperMissing, wanted { wasMissing = true }
        if wanted, Self.isUnregistered {
            start(fresh: true)
        } else if Self.needsRepair(wanted: wanted, status: status, pinned: defaults.string(forKey: Self.pinKey), current: Self.codeHash)
                    || (helperMissing && wanted && status == .enabled) {
            try? Self.service.unregister()
            start(fresh: false)
        }
        refresh()
    }

    /// The window app took the engine lock at once, so no helper was holding it, yet the user wants one. After an update that
    /// was never followed by opening Later, launchd's pin on the old build keeps the helper from starting at login.
    nonisolated static func helperWasMissing(_ outcome: WindowHandoff.Outcome, wanted: Bool, installed: Bool) -> Bool {
        outcome == .immediate && wanted && installed
    }

    /// The helper wasn't running at the last launch. Settings says so when registering it again didn't bring it back.
    private(set) var wasMissing = false
    var notRestored: Bool { wasMissing && (status != .enabled || failure != nil) }

    /// 1.2 recorded that it had decided (by default or by the toggle) in `loginDefaultApplied`; having decided and no
    /// longer being enabled means the user turned it off. A first install has decided nothing, so it is on.
    nonisolated static func initiallyWanted(oldItemWasOn: Bool, decidedBefore: Bool) -> Bool { oldItemWasOn || !decidedBefore }

    private static var isUnregistered: Bool { service.status == .notRegistered || service.status == .notFound }

    /// launchd starts the agent only if the program still has the code hash it had when the agent was registered, and a
    /// replaced app (any new build) no longer does (measured: OS_REASON_CODESIGNING). launchd notices at the failed start
    /// and refreshes the hash within about ten seconds, but registering before that keeps the old hash, so a build that
    /// isn't the one recorded in `pinKey` registers, then registers again 15 s later and records itself. Registering
    /// again is not a new item, so macOS shows no banner. If Later quits before the second pass, the next launch repeats it.
    /// A fresh registration (nothing registered before) pins this build already, so it records itself at once and repairs nothing.
    private static let pinKey = "agentRegisteredHash"
    private var repair: Task<Void, Never>?

    private func start(fresh: Bool) {
        let defaults = UserDefaults.standard
        change(true)
        guard failure == nil else { return }
        if fresh { defaults.set(Self.codeHash, forKey: Self.pinKey) }
        guard !fresh, defaults.string(forKey: Self.pinKey) != Self.codeHash else { return }
        repair = Task {
            try? await Task.sleep(for: .seconds(15))
            // Not when it was switched off meanwhile, here or in Login Items (then it isn't enabled any more).
            guard !Task.isCancelled, Self.service.status == .enabled else { return }
            try? await Self.service.unregister()
            guard !Task.isCancelled, defaults.bool(forKey: Self.wantedKey) else { refresh(); return }
            change(true)
            if failure == nil { defaults.set(Self.codeHash, forKey: Self.pinKey) }
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
