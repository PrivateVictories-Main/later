import Foundation
import LaterCore

/// What the window app and the background agent share to take turns: one lock file in the container, and a Darwin
/// notification (named by bundle ID, so a development build never wakes the installed agent).
enum Handoff {
    static let notification = (Bundle.main.bundleIdentifier ?? "com.ryan.later") + ".handoff"

    static func lock() -> EngineLock? {
        guard let store = try? LibraryStore.appDefault() else { return nil }
        return EngineLock(path: store.fileURL.deletingLastPathComponent().appendingPathComponent("engine.lock").path)
    }
}
