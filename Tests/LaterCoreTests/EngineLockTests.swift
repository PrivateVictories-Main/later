import Foundation
import Testing
@testable import LaterCore

/// Counts what the agent's tenure reports, from whichever thread reports it.
private final class AcquireCount: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.withLock { value } }
    func bump() { lock.withLock { value += 1 } }
}

/// Two EngineLocks on one file stand in for two processes: flock conflicts between separate opens, even in one process.
@Suite struct EngineLockTests {
    private func tempFile() -> String {
        FileManager.default.temporaryDirectory.appendingPathComponent("later-lock-test-\(UUID().uuidString)").path
    }

    private func waitFor(_ condition: () -> Bool, seconds: Double = 3) -> Bool {
        let end = Date.now.addingTimeInterval(seconds)
        while !condition(), Date.now < end { usleep(5_000) }
        return condition()
    }

    @Test func oneHolderAtATime() throws {
        let path = tempFile()
        defer { unlink(path) }
        let first = try #require(EngineLock(path: path)), second = try #require(EngineLock(path: path))
        #expect(first.acquire(timeout: 0))
        #expect(!second.acquire(timeout: 0.05))
        first.release()
        #expect(second.acquire(timeout: 0.05))
    }

    @Test func aHolderThatDiesFreesTheLock() throws {
        let path = tempFile()
        defer { unlink(path) }
        var crashed: EngineLock? = try #require(EngineLock(path: path))
        let waiting = try #require(EngineLock(path: path))
        #expect(crashed?.acquire(timeout: 0) == true)
        crashed = nil   // the descriptor closes, as when a process is killed
        #expect(waiting.acquire(timeout: 0.05))
    }

    @Test func tenureHoldsYieldsAndTakesBackWhenTheAppIsGone() throws {
        let path = tempFile()
        defer { unlink(path) }
        let acquired = AcquireCount()
        let tenure = AgentTenure(lock: try #require(EngineLock(path: path)), settle: 0.05) { acquired.bump() }
        tenure.start()
        #expect(waitFor { acquired.count == 1 })
        #expect(tenure.isHolding)

        // The app wants it: it's refused until the agent yields, then gets it, and the agent stays out.
        var app: EngineLock? = try #require(EngineLock(path: path))
        #expect(app?.acquire(timeout: 0.05) == false)
        tenure.release()
        #expect(!tenure.isHolding)
        #expect(app?.acquire(timeout: 2) == true)
        usleep(300_000)
        #expect(acquired.count == 1)

        // The app quits or is killed: the agent resumes.
        app = nil
        #expect(waitFor { acquired.count == 2 })
        #expect(tenure.isHolding)
    }

    @Test func aHandoffRequestLostToTheRaceIsAskedAgain() throws {
        let path = tempFile()
        defer { unlink(path) }
        let acquired = AcquireCount()
        let tenure = AgentTenure(lock: try #require(EngineLock(path: path)), settle: 0.05) { acquired.bump() }
        tenure.start()
        #expect(waitFor { acquired.count == 1 })
        let app = try #require(EngineLock(path: path))
        let requests = AcquireCount()
        // The first request is lost, as when it reaches the agent before it knows it holds the lock (release does nothing then).
        let got = app.acquire(timeout: 3, nudgeEvery: 0.05) {
            requests.bump()
            if requests.count > 1 { tenure.release() }
        }
        #expect(got)
        #expect(requests.count > 1)
        // With nobody answering, it gives up after the timeout instead of waiting for ever.
        let other = try #require(EngineLock(path: path))
        #expect(!other.acquire(timeout: 0.2, nudgeEvery: 0.05) {})
    }

    @Test func releasingWhileNotHoldingChangesNothing() throws {
        let path = tempFile()
        defer { unlink(path) }
        let acquired = AcquireCount()
        let app = try #require(EngineLock(path: path))
        #expect(app.acquire(timeout: 0))
        let tenure = AgentTenure(lock: try #require(EngineLock(path: path)), settle: 0.05) { acquired.bump() }
        tenure.start()
        tenure.release()   // a handoff request that arrives while the app already holds it
        app.release()
        #expect(waitFor { acquired.count == 1 })
        usleep(200_000)
        #expect(acquired.count == 1)   // the stray release must not have cut this tenure short
        #expect(tenure.isHolding)
    }
}
