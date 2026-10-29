import Foundation
import Testing
@testable import Later
@testable import LaterCore

@Suite @MainActor struct EngineStopTests {
    private func save(_ library: Library, _ n: Int) {
        library.save(url: "https://example.com/p/\(n)000000", title: "Page \(n)",
                     verdict: Verdict(isSpecific: true, kind: .shopping, pageType: "example.com/p/*"), browser: .chrome, now: .now)
    }

    @Test func aStoppedEngineWritesTheListOnceAndNeverAgain() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("later-stop-test-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = LibraryStore(fileURL: dir.appendingPathComponent("library.json"))
        let library = Library()
        let engine = Engine(testing: library, store: store)
        engine.start()
        save(library, 1)   // schedules a save a second from now
        try engine.stop()
        #expect(store.load().data.items.count == 1)   // stop wrote it at once
        let written = try Data(contentsOf: store.fileURL)

        save(library, 2)   // the other process owns the list now
        try await Task.sleep(for: .seconds(1.5))   // past the coalescing delay of both saves
        try engine.flush()
        #expect(try Data(contentsOf: store.fileURL) == written)
        try engine.stop()   // a second stop does nothing
    }
}
