import Foundation
import LaterCore
import notify

/// What the window app and the background agent share to take turns: one lock file in the container, a Darwin
/// notification (named by bundle ID, so a development build never wakes the installed agent), and the agent's status file.
enum Handoff {
    static let notification = (Bundle.main.bundleIdentifier ?? "com.ryan.later") + ".handoff"

    private static var folder: URL? { (try? LibraryStore.appDefault())?.fileURL.deletingLastPathComponent() }

    static func lock() -> EngineLock? {
        folder.flatMap { EngineLock(path: $0.appendingPathComponent("engine.lock").path) }
    }

    /// What the agent reports when it can't do its job; the window app reads it at launch.
    static func statusFile() -> AgentStatusFile? {
        folder.map { AgentStatusFile(url: $0.appendingPathComponent("agent-status.json")) }
    }
}

/// The window app's side of the handoff: taking the engine lock from the agent, and trying again while it is read-only.
@MainActor
final class WindowHandoff {
    enum Outcome: Equatable {
        /// Nobody held the lock: no agent was running.
        case immediate
        /// The agent was asked and handed the list over.
        case afterHandoff
        /// The agent did not let go in time: the window only shows the list.
        case readOnly
    }

    private let lock: EngineLock
    private let nudge: () -> Void
    private(set) var outcome: Outcome?
    var isReadOnly: Bool { outcome == .readOnly }

    init(lock: EngineLock, nudge: @escaping () -> Void = { notify_post(Handoff.notification) }) {
        self.lock = lock
        self.nudge = nudge
    }

    /// Takes the lock now, or asks the agent to hand over and waits for it (it answers in a few milliseconds; nothing polls
    /// on its side). Asked again every 0.25 s: a request that reaches the agent just before it knows it holds the lock is ignored.
    @discardableResult
    func take(timeout: TimeInterval = 5) -> Outcome {
        let result: Outcome = lock.acquire(timeout: 0) ? .immediate
            : lock.acquire(timeout: timeout, nudgeEvery: 0.25, nudge: nudge) ? .afterHandoff : .readOnly
        outcome = result
        return result
    }

    /// The window was shown or activated again while read-only: ask once more, briefly. True when the list is ours now.
    /// Event-driven: only ever called from those events, never on a timer.
    func retry(timeout: TimeInterval = 0.25) -> Bool {
        guard isReadOnly else { return false }
        guard lock.acquire(timeout: timeout, nudgeEvery: 0.1, nudge: nudge) else { return false }
        outcome = .afterHandoff
        return true
    }
}
