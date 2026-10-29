import Foundation
import Testing
@testable import Later
@testable import LaterCore

/// The window app when the background helper never handed the list over: it shows the list, edits nothing, and takes over
/// once the lock is free.
@Suite @MainActor struct ReadOnlyEngineTests {
    private let verdict = Verdict(isSpecific: true, kind: .shopping, pageType: "a.com/p/*")

    private func withStore(_ body: (LibraryStore) throws -> Void) throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("later-readonly-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try body(LibraryStore(fileURL: dir.appendingPathComponent("library.json")))
    }

    private func listOnDisk(_ store: LibraryStore, _ titles: String...) throws {
        let library = Library()
        for (n, title) in titles.enumerated() {
            library.save(url: "https://a.com/p/100000\(n)", title: title, verdict: verdict, browser: .helium, now: .now)
        }
        try store.save(library.data)
    }

    @Test func aReadOnlyEngineCannotEditAndSaysSo() throws {
        try withStore { store in
            try listOnDisk(store, "A", "B")
            let engine = Engine(testing: Library(data: store.peek(notice: "").data), store: store, readOnly: true)
            #expect(!engine.canEdit)
            #expect(engine.notice == Engine.readOnlyNotice)
            let model = LibraryModel(engine: engine, undo: UndoManager())
            model.wake(at: .now)
            ItemActions(model: model).delete(engine.library.data.items)
            ItemActions(model: model).setDone(engine.library.data.items, true)
            #expect(engine.library.data.items.count == 2)
            #expect(engine.library.data.items.allSatisfy { $0.status == .active })
        }
    }

    @Test func aWritableEngineStillEdits() throws {
        let engine = Engine(testing: Library())
        engine.library.save(url: "https://a.com/p/1000000", title: "A", verdict: verdict, browser: .helium, now: .now)
        #expect(engine.canEdit)
        let model = LibraryModel(engine: engine, undo: UndoManager())
        model.wake(at: .now)
        ItemActions(model: model).delete(engine.library.data.items)
        #expect(engine.library.data.items.isEmpty)
    }

    @Test func takingOverLoadsTheListAsItIsNowAndSavesFromThere() throws {
        try withStore { store in
            try listOnDisk(store, "A")
            let engine = Engine(testing: Library(data: store.peek(notice: "").data), store: store, readOnly: true)
            try listOnDisk(store, "A", "B")   // the helper wrote once more before letting go
            engine.becomeWritable()
            #expect(engine.canEdit)
            #expect(engine.library.data.items.map(\.title).sorted() == ["A", "B"])
            #expect(engine.notice == nil)   // the read-only notice goes away
            let id = try #require(engine.library.data.items.first?.id)
            engine.library.delete(id, now: .now)
            try engine.flush()
            #expect(store.load().data.items.count == 1)
        }
    }

    @Test func takingOverTwiceDoesNothingTheSecondTime() throws {
        try withStore { store in
            try listOnDisk(store, "A")
            let engine = Engine(testing: Library(data: store.peek(notice: "").data), store: store, readOnly: true)
            engine.becomeWritable()
            engine.library.save(url: "https://a.com/p/2000000", title: "C", verdict: verdict, browser: .helium, now: .now)
            engine.becomeWritable()
            #expect(engine.library.data.items.count == 2)   // not reloaded from disk
        }
    }

    @Test func aProblemFromTheBackgroundShowsOverTheLoadNotice() {
        let engine = Engine(testing: Library())
        #expect(engine.notice == nil)
        let before = engine.noticeGeneration
        engine.showBackgroundProblem(AgentStatus(state: .cantSave, count: 4).notice)
        #expect(engine.notice == AgentStatus(state: .cantSave, count: 4).notice)
        #expect(engine.noticeGeneration == before + 1)   // shows again even if dismissed before
    }

    @Test func theEngineReportsSaveFailuresAndRecoveries() async throws {
        try withStore { store in
            let engine = Engine(testing: Library(), store: store)
            var reports: [Bool] = []
            engine.onSaveFailedChange = { reports.append($0) }
            // A list that can't be written: the folder is gone.
            let gone = store.fileURL.deletingLastPathComponent()
            try FileManager.default.removeItem(at: gone)
            #expect(throws: (any Error).self) { try engine.flush() }
            try FileManager.default.createDirectory(at: gone, withIntermediateDirectories: true)
            try engine.flush()
            #expect(reports == [true, false])
        }
    }
}
