import Foundation
import Testing
@testable import LaterCore

@Suite struct WatchProbeTests {
    private let fm = FileManager.default

    /// Runs `body` with a fresh temp folder, always removed afterwards.
    private func withTempRoot(_ body: (String) throws -> Void) throws {
        let root = fm.temporaryDirectory.appendingPathComponent("LaterWatchProbe-\(UUID().uuidString)").path
        defer {
            try? fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: root + "/Default/Sessions")
            try? fm.removeItem(atPath: root)
        }
        try body(root)
    }

    @Test func sessionsFolderPresent() throws {
        try withTempRoot { root in
            try fm.createDirectory(atPath: root + "/Default/Sessions", withIntermediateDirectories: true)
            #expect(WatchProbe.canWatch(folder: root))
        }
    }

    @Test func sessionsFolderNotYetCreated() throws {
        try withTempRoot { root in
            try fm.createDirectory(atPath: root + "/Default", withIntermediateDirectories: true)
            #expect(WatchProbe.canWatch(folder: root))
        }
    }

    /// A browser that's installed but has never been opened has no data folder yet: that's waiting, not denied.
    @Test func missingRoot() throws {
        try withTempRoot { root in
            #expect(!WatchProbe.canWatch(folder: root))
            #expect(WatchProbe.access(folder: root) == .missing)
        }
    }

    @Test func deniedRoot() throws {
        try withTempRoot { root in
            try fm.createDirectory(atPath: root, withIntermediateDirectories: true)
            try fm.setAttributes([.posixPermissions: 0], ofItemAtPath: root)
            defer { try? fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: root) }
            #expect(WatchProbe.access(folder: root) == .denied)
        }
    }

    @Test func sessionsFolderDenied() throws {
        try withTempRoot { root in
            try fm.createDirectory(atPath: root + "/Default/Sessions", withIntermediateDirectories: true)
            try fm.setAttributes([.posixPermissions: 0], ofItemAtPath: root + "/Default/Sessions")
            #expect(!WatchProbe.canWatch(folder: root))
            #expect(WatchProbe.access(folder: root) == .denied)
        }
    }
}
