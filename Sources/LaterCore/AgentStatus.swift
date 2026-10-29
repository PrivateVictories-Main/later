import Foundation

/// What the background helper tells the window app when it can't do its job: a state and a count, never a link or a title.
public struct AgentStatus: Codable, Equatable, Sendable {
    public enum State: String, Codable, Sendable {
        /// The list is damaged or missing with a backup; the helper leaves it to the window app and watches nothing.
        case needsRecovery
        /// The storage folder can't be opened, so nothing could be kept.
        case cantOpenStorage
        /// The last save failed (a full disk, say), so pages it noticed since may be missing.
        case cantSave
    }

    public var state: State
    /// Pages in the list at the time.
    public var count: Int

    public init(state: State, count: Int) {
        self.state = state
        self.count = count
    }

    /// For the window's notice bar.
    public var notice: String {
        switch state {
        case .needsRecovery: "Later wasn't watching your tabs while your list needed repair."
        case .cantOpenStorage: "Later couldn't open its storage folder while it was closed, so pages it noticed weren't kept."
        case .cantSave: "Later couldn't save while it was closed, so some pages it noticed may be missing."
        }
    }
}

/// The helper's status file, beside the list. One writer (the helper), read and cleared by the window app.
public struct AgentStatusFile: Sendable {
    public let url: URL

    public init(url: URL) { self.url = url }

    public func read() -> AgentStatus? {
        (try? Data(contentsOf: url)).flatMap { try? JSONDecoder().decode(AgentStatus.self, from: $0) }
    }

    /// Best effort: a status that can't be written is only a missing hint.
    public func write(_ status: AgentStatus) {
        guard let bytes = try? JSONEncoder().encode(status) else { return }
        try? bytes.write(to: url, options: .atomic)
    }

    public func clear() { try? FileManager.default.removeItem(at: url) }

    /// Reads the problem and clears it, as the window app does once it has taken over.
    public func take() -> AgentStatus? {
        defer { clear() }
        return read()
    }
}
