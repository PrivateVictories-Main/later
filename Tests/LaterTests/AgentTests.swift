import Foundation
import Testing
@testable import Later
@testable import LaterCore

/// The background helper's decisions, over a temporary list, lock and status file, with a second lock as the window app.
@Suite @MainActor struct AgentTests {
    private let verdict = Verdict(isSpecific: true, kind: .shopping, pageType: "a.com/p/*")

    private struct Rig {
        let dir: URL
        let store: LibraryStore
        let lockPath: String
        let status: AgentStatusFile
        var app: EngineLock { EngineLock(path: lockPath)! }
    }

    /// A list with one page on disk; the status file lives in another folder, so it can be written when the list's is gone.
    private func rig(seed: Bool = true) throws -> Rig {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("later-agent-\(UUID().uuidString)")
        let dir = base.appendingPathComponent("data")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: base.appendingPathComponent("status"), withIntermediateDirectories: true)
        let store = LibraryStore(fileURL: dir.appendingPathComponent("library.json"))
        if seed {
            let library = Library()
            library.save(url: "https://a.com/p/1000000", title: "First", verdict: verdict, browser: .helium, now: .now)
            try store.save(library.data)
        }
        return Rig(dir: base, store: store, lockPath: dir.appendingPathComponent("engine.lock").path,
                   status: AgentStatusFile(url: base.appendingPathComponent("status/agent-status.json")))
    }

    private func agent(_ rig: Rig, store: LibraryStore?) -> AgentDelegate {
        AgentDelegate(lock: EngineLock(path: rig.lockPath), store: store, status: rig.status,
                             makeEngine: { Engine(testing: Library(data: store?.load().data ?? LibraryData()), store: store) })
    }

    private func eventually(_ condition: () -> Bool, seconds: Double = 3) async -> Bool {
        let end = Date.now.addingTimeInterval(seconds)
        while !condition(), Date.now < end { try? await Task.sleep(for: .milliseconds(10)) }
        return condition()
    }

    @Test func itWatchesOnceItHoldsTheLock() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        let agent = agent(rig, store: rig.store)
        agent.startTenure()
        #expect(await eventually { agent.engine != nil })
        #expect(agent.engine?.library.data.items.count == 1)
        #expect(rig.status.read() == nil)
    }

    @Test func aDamagedListIsLeftAloneAndReported() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        try Data("{ not json".utf8).write(to: rig.store.fileURL)
        let damaged = try Data(contentsOf: rig.store.fileURL)
        let agent = agent(rig, store: rig.store)
        agent.startTenure()
        #expect(await eventually { rig.status.read() != nil })
        #expect(rig.status.read()?.state == .needsRecovery)
        #expect(agent.engine == nil)   // watches nothing
        #expect(try Data(contentsOf: rig.store.fileURL) == damaged)   // touches nothing
    }

    @Test func aStorageFolderThatCantBeOpenedIsReported() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        let agent = agent(rig, store: nil)
        agent.startTenure()
        #expect(await eventually { rig.status.read() != nil })
        #expect(rig.status.read()?.state == .cantOpenStorage)
        #expect(agent.engine == nil)
    }

    @Test func yieldingWritesTheListThenLetsGo() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        let agent = agent(rig, store: rig.store)
        agent.startTenure()
        #expect(await eventually { agent.engine != nil })
        let engine = try #require(agent.engine)
        engine.library.save(url: "https://a.com/p/2000000", title: "Second", verdict: verdict, browser: .helium, now: .now)

        // The window app waits on the lock; whatever it finds on disk the moment it gets it is what it will show.
        let seen = ListBox()
        let app = rig.app
        let waiter = Thread {
            _ = app.acquire()
            seen.set(rig.store.load().data.items.count)
        }
        waiter.start()
        try await Task.sleep(for: .milliseconds(100))   // it is waiting
        agent.yield()
        #expect(await eventually { seen.value != nil })
        #expect(seen.value == 2)   // written before the lock was released
        #expect(agent.engine == nil)
        #expect(rig.status.read() == nil)
    }

    @Test func yieldingTwiceIsHarmless() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        let agent = agent(rig, store: rig.store)
        agent.startTenure()
        #expect(await eventually { agent.engine != nil })
        agent.yield()
        let written = try Data(contentsOf: rig.store.fileURL)
        let app = rig.app
        #expect(app.acquire(timeout: 1))
        agent.yield()   // a stale request, with the app now holding the lock
        #expect(try Data(contentsOf: rig.store.fileURL) == written)
        #expect(!rig.app.acquire(timeout: 0.05))   // the app still has it
    }

    @Test func aFailedFinalSaveIsReportedAndTheListStillGoesToTheApp() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        let agent = agent(rig, store: rig.store)
        agent.startTenure()
        #expect(await eventually { agent.engine != nil })
        try FileManager.default.removeItem(at: rig.store.fileURL.deletingLastPathComponent())   // the disk goes away
        agent.yield()
        #expect(rig.status.read()?.state == .cantSave)
        #expect(rig.status.read()?.count == 1)
        #expect(agent.engine == nil)
    }

    @Test func saveFailuresDuringWatchingAreReportedAndClearedWhenTheyStop() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        let agent = agent(rig, store: rig.store)
        agent.startTenure()
        #expect(await eventually { agent.engine != nil })
        let engine = try #require(agent.engine)
        let folder = rig.store.fileURL.deletingLastPathComponent()
        try FileManager.default.removeItem(at: folder)
        #expect(throws: (any Error).self) { try engine.flush() }
        #expect(rig.status.read()?.state == .cantSave)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try engine.flush()
        #expect(rig.status.read() == nil)
    }

    @Test func afterTheAppQuitsItWatchesAgainWithTheListTheAppLeft() async throws {
        let rig = try rig()
        defer { try? FileManager.default.removeItem(at: rig.dir) }
        let agent = agent(rig, store: rig.store)
        agent.startTenure()
        #expect(await eventually { agent.engine != nil })
        let first = try #require(agent.engine)
        agent.yield()
        let app = rig.app
        #expect(app.acquire(timeout: 2))
        let library = Library(data: rig.store.load().data)
        library.save(url: "https://a.com/p/3000000", title: "From the app", verdict: verdict, browser: .helium, now: .now)
        try rig.store.save(library.data)
        app.release()   // the app quits
        #expect(await eventually { agent.engine != nil && agent.engine !== first })
        #expect(agent.engine?.library.data.items.count == 2)
    }
}

private final class ListBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Int?
    var value: Int? { lock.withLock { stored } }
    func set(_ n: Int) { lock.withLock { stored = n } }
}
