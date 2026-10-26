import Foundation
import Testing
@testable import LaterCore

/// Hands each ring to whoever is waiting for the next one; rings with nobody waiting are dropped.
@MainActor private final class RingWaiter {
    private var waiter: (id: UUID, continuation: CheckedContinuation<Bool, Never>)?

    func ring() { finish(true) }

    /// True if a ring arrives within `limit`.
    func next(within limit: Duration) async -> Bool {
        await withCheckedContinuation { continuation in
            let id = UUID()
            waiter = (id, continuation)
            Task { @MainActor in
                try? await Task.sleep(for: limit)
                if waiter?.id == id { finish(false) }
            }
        }
    }

    private func finish(_ rang: Bool) {
        let current = waiter
        waiter = nil
        current?.continuation.resume(returning: rang)
    }
}

@MainActor @Suite struct DoorbellTests {
    private let fm = FileManager.default

    private func append(_ path: String) throws {
        let handle = try #require(FileHandle(forWritingAtPath: path))
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: Data("x".utf8))
    }

    @Test func ringsOnSessionWritesAndRotation() async throws {
        let root = fm.temporaryDirectory.appendingPathComponent("LaterDoorbell-\(UUID().uuidString)").path
        defer { try? fm.removeItem(atPath: root) }
        let sessions = root + "/Default/Sessions"
        try fm.createDirectory(atPath: sessions, withIntermediateDirectories: true)
        #expect(fm.createFile(atPath: sessions + "/Session_1", contents: Data("a".utf8)))

        let rings = RingWaiter()
        let bell = Doorbell { rings.ring() }
        #expect(bell.start(folder: root))
        defer { bell.stop() }

        try append(sessions + "/Session_1")
        #expect(await rings.next(within: .seconds(2)))

        #expect(fm.createFile(atPath: sessions + "/Session_2", contents: Data("b".utf8)))
        #expect(await rings.next(within: .seconds(2)))
        while await rings.next(within: .seconds(1)) {}   // let the rotation's late rings pass, so the next one is the write's

        try append(sessions + "/Session_2")   // the browser keeps writing to the new file
        #expect(await rings.next(within: .seconds(2)))
        while await rings.next(within: .seconds(1)) {}

        bell.stop()
        try append(sessions + "/Session_2")
        #expect(await !rings.next(within: .seconds(1)))
    }

    @Test func missingFolderCannotStart() {
        let bell = Doorbell {}
        #expect(!bell.start(folder: fm.temporaryDirectory.appendingPathComponent("LaterDoorbell-missing-\(UUID().uuidString)").path))
    }
}
