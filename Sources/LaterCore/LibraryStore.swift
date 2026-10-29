import Foundation
import os

/// One JSON file. Every save is complete-or-nothing (temp file, fsync, atomic rename),
/// the previous version is kept as `library.json.bak`, and an unreadable file is kept, never overwritten.
public struct LibraryStore: Sendable {
    public let fileURL: URL
    var backupURL: URL { fileURL.appendingPathExtension("bak") }
    var tempURL: URL { fileURL.appendingPathExtension("tmp") }
    var backupTempURL: URL { backupURL.appendingPathExtension("tmp") }
    /// This process read or wrote the primary file itself, so a save needn't decode it first. Shared by copies.
    private let primaryKnownGood = OSAllocatedUnfairLock(initialState: false)
    /// The list as v1.0 and v1.1 left it. Nothing makes them any more (1.3 reads and writes the same format); the ones an
    /// upgrade finds are removed 30 days after they were made, once the current list has loaded cleanly.
    var safetyCopyURLs: [URL] {
        ["library-before-1.1.json", "library-before-1.2.json"].map { fileURL.deletingLastPathComponent().appendingPathComponent($0) }
    }

    public init(fileURL: URL) {
        self.fileURL = fileURL
    }

    /// `Application Support/Later/library.json` inside the app's sandbox container.
    public static func appDefault() throws -> LibraryStore {
        let base = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                               appropriateFor: nil, create: true)
        let dir = base.appendingPathComponent("Later", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        return LibraryStore(fileURL: dir.appendingPathComponent("library.json"))
    }

    public struct Loaded: Sendable {
        public var data: LibraryData
        /// A plain sentence for the window when something had to be recovered.
        public var notice: String?
        /// False when an unreadable list couldn't be set aside; the app must not save for the rest of its session.
        public var writable: Bool

        public init(data: LibraryData, notice: String?, writable: Bool = true) {
            self.data = data
            self.notice = notice
            self.writable = writable
        }
    }

    /// Whether `load()` would have to recover something (a damaged list, or a missing one with a backup). The agent leaves
    /// that to the window app, which tells the user what happened.
    public var needsRecovery: Bool {
        decode(fileURL) == nil && [fileURL, backupURL].contains { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// The list as it is on disk, touching no file: for a process that doesn't hold the engine lock, so it neither recovers
    /// nor saves. Falls back to the backup copy, then to an empty list.
    public func peek(notice: String) -> Loaded {
        Loaded(data: decode(fileURL) ?? decode(backupURL) ?? LibraryData(), notice: notice, writable: false)
    }

    public func load() -> Loaded {
        if let data = decode(fileURL) {
            primaryKnownGood.withLock { $0 = true }
            removeOldSafetyCopies()
            return Loaded(data: data, notice: nil)
        }
        primaryKnownGood.withLock { $0 = false }
        let fm = FileManager.default
        var keptAs: String?
        if fm.fileExists(atPath: fileURL.path) {
            // A file that can't be opened (permissions, I/O error) isn't known to be damaged: leave it alone.
            guard (try? Data(contentsOf: fileURL)) != nil else {
                return Loaded(data: decode(backupURL) ?? LibraryData(),
                              notice: "Later couldn't read its list just now. It will try again.",
                              writable: false)
            }
            let name = "library.corrupt-\(Int(Date().timeIntervalSince1970))-\(UUID().uuidString.prefix(8)).json"
            guard (try? fm.moveItem(at: fileURL, to: fileURL.deletingLastPathComponent().appendingPathComponent(name))) != nil else {
                return Loaded(data: decode(backupURL) ?? LibraryData(),
                              notice: "Later couldn't set aside a damaged copy of your list, so it won't save changes this time. Nothing has been deleted. Quit Later and open it again to try once more.",
                              writable: false)
            }
            keptAs = name
        }
        if let data = decode(backupURL) {
            return Loaded(data: data, notice: "Your list was restored from its backup copy.")
        }
        if let keptAs {
            return Loaded(data: LibraryData(), notice: "Your list couldn't be read, so a new one was started. The old file was kept as \(keptAs).")
        }
        return Loaded(data: LibraryData(), notice: nil)
    }

    /// So a page deleted since then doesn't live on in a file the app never rewrites.
    private func removeOldSafetyCopies(now: Date = .now) {
        let cutoff = now.addingTimeInterval(-30 * 86400)
        for url in safetyCopyURLs {
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path) else { continue }
            // The later of the two dates, since a copy may carry the date of the list it was made from.
            let made = max(attributes[.creationDate] as? Date ?? .distantPast, attributes[.modificationDate] as? Date ?? .distantPast)
            if made < cutoff { try? FileManager.default.removeItem(at: url) }
        }
    }

    public func save(_ data: LibraryData) throws {
        let bytes = try JSONEncoder().encode(data)
        let fm = FileManager.default
        // Never rotate an unreadable list over the good backup.
        if !primaryKnownGood.withLock({ $0 }), fm.fileExists(atPath: fileURL.path), decode(fileURL) == nil {
            throw CocoaError(.fileReadCorruptFile)
        }
        guard fm.createFile(atPath: tempURL.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
            throw CocoaError(.fileWriteUnknown)
        }
        let handle = try FileHandle(forWritingTo: tempURL)
        do {
            try handle.write(contentsOf: bytes)
            try handle.synchronize()
            try handle.close()
        } catch {
            try? handle.close()
            throw error
        }
        if fm.fileExists(atPath: fileURL.path) {
            unlink(backupTempURL.path)
            guard link(fileURL.path, backupTempURL.path) == 0,
                  rename(backupTempURL.path, backupURL.path) == 0 else { throw Self.posixError() }
        }
        guard rename(tempURL.path, fileURL.path) == 0 else { throw Self.posixError() }
        primaryKnownGood.withLock { $0 = true }
        let folder = open(fileURL.deletingLastPathComponent().path, O_RDONLY)
        guard folder >= 0 else { throw Self.posixError() }
        defer { close(folder) }
        guard fsync(folder) == 0 else { throw Self.posixError() }
    }

    private static func posixError() -> POSIXError { POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }

    /// Deletes the list, its backup and any temp file. Used by `Later --reset` after test runs.
    public func reset() throws {
        for url in [fileURL, backupURL, tempURL, backupTempURL] + safetyCopyURLs where FileManager.default.fileExists(atPath: url.path) {
            try FileManager.default.removeItem(at: url)
        }
    }

    private func decode(_ url: URL) -> LibraryData? {
        guard let bytes = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(LibraryData.self, from: bytes)
    }
}
