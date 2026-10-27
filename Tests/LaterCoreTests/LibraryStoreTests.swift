import Foundation
import Testing
@testable import LaterCore

@Suite struct LibraryStoreTests {
    func withStore(_ body: (LibraryStore, URL) throws -> Void) throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("later-tests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try body(LibraryStore(fileURL: dir.appendingPathComponent("library.json")), dir)
    }

    func sample(_ title: String) -> LibraryData {
        var data = LibraryData()
        data.items = [Item(id: UUID(uuidString: "00000000-0000-0000-0000-000000000001")!, url: "https://a.com/p/1234567",
                           title: title, host: "a.com", kind: .shopping, pageType: "a.com/p/*", browser: .helium,
                           firstSaved: Date(timeIntervalSince1970: 1), lastSeen: Date(timeIntervalSince1970: 2),
                           visits: 1, status: .active, doneAt: nil)]
        return data
    }

    @Test func theAgentLeavesADamagedOrMissingListToTheApp() throws {
        try withStore { store, dir in
            #expect(!store.needsRecovery)   // nothing yet: a new list
            try store.save(sample("A"))
            #expect(!store.needsRecovery)
            try store.save(sample("B"))   // now there is a backup too
            try Data("{ not json".utf8).write(to: store.fileURL)
            #expect(store.needsRecovery)
            let damaged = try Data(contentsOf: store.fileURL)
            let peeked = store.peek(notice: "n")
            #expect(peeked.data == sample("A"))   // the backup, as a read-only view
            #expect(!peeked.writable)
            #expect(try Data(contentsOf: store.fileURL) == damaged)   // peeking set nothing aside
            #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.contains("corrupt") }.isEmpty)
            try FileManager.default.removeItem(at: store.fileURL)
            #expect(store.needsRecovery)   // missing, but a backup exists
        }
    }

    @Test func roundTrips() throws {
        try withStore { store, _ in
            try store.save(sample("A"))
            let loaded = store.load()
            #expect(loaded.data == sample("A"))
            #expect(loaded.notice == nil)
        }
    }

    @Test func firstSaveKeepsTheOldListOnceAndNeverReplacesIt() throws {
        try withStore { store, dir in
            let old = Data(#"{"version":1,"items":[],"corrections":{},"seen":{},"dwellSamples":[]}"#.utf8)
            try old.write(to: store.fileURL)
            let copy = dir.appendingPathComponent("library-before-1.1.json")
            try store.save(sample("A"))
            #expect(try Data(contentsOf: copy) == old)
            try store.save(sample("B"))
            #expect(try Data(contentsOf: copy) == old)
            // A later run (a new store) with the copy present leaves it alone too.
            try LibraryStore(fileURL: store.fileURL).save(sample("C"))
            #expect(try Data(contentsOf: copy) == old)
        }
    }

    @Test func v12KeepsItsOwnCopyOfTheV11ListBesideTheEarlierOne() throws {
        try withStore { store, dir in
            let v10 = Data(#"{"version":1,"items":[],"corrections":{},"seen":{},"dwellSamples":[]}"#.utf8)
            let v11 = Data(#"{"version":1,"items":[],"corrections":{},"seen":{},"dwellSamples":[10]}"#.utf8)
            try v11.write(to: store.fileURL)
            try v10.write(to: dir.appendingPathComponent("library-before-1.1.json"))   // left by v1.1
            try store.save(sample("A"))
            #expect(try Data(contentsOf: dir.appendingPathComponent("library-before-1.1.json")) == v10)
            #expect(try Data(contentsOf: dir.appendingPathComponent("library-before-1.2.json")) == v11)
            try store.save(sample("B"))
            #expect(try Data(contentsOf: dir.appendingPathComponent("library-before-1.2.json")) == v11)
        }
    }

    @Test func noOldListMeansNoCopy() throws {
        try withStore { store, dir in
            try store.save(sample("A"))
            #expect(!FileManager.default.fileExists(atPath: dir.appendingPathComponent("library-before-1.1.json").path))
        }
    }

    @Test func aKindFromANewerBuildReadsAsOther() throws {
        try withStore { store, _ in
            let json = #"{"version":1,"items":[{"id":"00000000-0000-0000-0000-000000000001","url":"https://a.com/p/1234567","title":"A","host":"a.com","kind":"gizmos","pageType":"a.com/p/*","browser":"helium","firstSaved":1,"lastSeen":2,"visits":1,"status":"active"}],"corrections":{"a.com/p/*":{"kind":"gizmos"}},"seen":{},"dwellSamples":[]}"#
            try Data(json.utf8).write(to: store.fileURL)
            let loaded = store.load()
            #expect(loaded.notice == nil)
            #expect(loaded.data.items.first?.kind == .other)
            #expect(loaded.data.corrections["a.com/p/*"]?.kind == .other)
        }
    }

    @Test func missingFileGivesAnEmptyList() throws {
        try withStore { store, _ in
            let loaded = store.load()
            #expect(loaded.data == LibraryData())
            #expect(loaded.notice == nil)
        }
    }

    @Test func keepsThePreviousVersionAsBackup() throws {
        try withStore { store, _ in
            try store.save(sample("A"))
            try store.save(sample("B"))
            let backup = try JSONDecoder().decode(LibraryData.self, from: Data(contentsOf: store.backupURL))
            #expect(backup == sample("A"))
        }
    }

    @Test func corruptFileIsKeptAndTheBackupIsLoaded() throws {
        try withStore { store, dir in
            try store.save(sample("A"))
            try store.save(sample("B"))
            try Data("{garbage".utf8).write(to: store.fileURL)
            let loaded = store.load()
            #expect(loaded.data == sample("A"))
            #expect(loaded.notice != nil)
            let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
            #expect(names.contains { $0.hasPrefix("library.corrupt-") })
        }
    }

    @Test func leftoverTempFileIsIgnored() throws {
        try withStore { store, _ in
            try store.save(sample("A"))
            try Data("{half-written".utf8).write(to: store.tempURL)
            #expect(store.load().data == sample("A"))
        }
    }

    @Test func fileIsPrivateToTheUser() throws {
        try withStore { store, _ in
            try store.save(sample("A"))
            let attributes = try FileManager.default.attributesOfItem(atPath: store.fileURL.path)
            #expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600)
        }
    }

    @Test func resetRemovesEverything() throws {
        try withStore { store, dir in
            try store.save(sample("A"))
            try store.save(sample("B"))
            try store.reset()
            #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path).isEmpty)
        }
    }

    func decoded(_ url: URL) throws -> LibraryData {
        try JSONDecoder().decode(LibraryData.self, from: Data(contentsOf: url))
    }

    @Test func corruptFileThatCantBeSetAsideIsNeverSavedOver() throws {
        try withStore { store, dir in
            try store.save(sample("A"))
            try store.save(sample("B"))
            let garbage = Data("{garbage".utf8)
            try garbage.write(to: store.fileURL)
            try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: dir.path)
            defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dir.path) }
            let loaded = store.load()
            #expect(loaded.data == sample("A"))
            #expect(!loaded.writable)
            #expect(loaded.notice != nil)
            #expect(throws: (any Error).self) { try store.save(sample("C")) }
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dir.path)
            #expect(throws: (any Error).self) { try store.save(sample("C")) }
            #expect(try decoded(store.backupURL) == sample("A"))
            #expect(try Data(contentsOf: store.fileURL) == garbage)
        }
    }

    @Test func failedSaveKeepsTheBackup() throws {
        try withStore { store, _ in
            try store.save(sample("A"))
            try store.save(sample("B"))
            try FileManager.default.setAttributes([.immutable: true], ofItemAtPath: store.fileURL.path)
            defer { try? FileManager.default.setAttributes([.immutable: false], ofItemAtPath: store.fileURL.path) }
            #expect(throws: (any Error).self) { try store.save(sample("C")) }
            #expect(try decoded(store.backupURL) == sample("A"))
            #expect(try decoded(store.fileURL) == sample("B"))
        }
    }

    @Test func olderFilesWithoutDismissalsLoad() throws {
        try withStore { store, _ in
            var json = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(sample("A"))) as? [String: Any])
            #expect(json.removeValue(forKey: "dismissed") != nil)
            try JSONSerialization.data(withJSONObject: json).write(to: store.fileURL)
            let loaded = store.load()
            #expect(loaded.data == sample("A"))
            #expect(loaded.notice == nil)
        }
    }

    @Test func savesTrustAFileThisStoreReadOrWrote() throws {
        try withStore { store, _ in
            try store.save(sample("A"))
            try Data("{garbage".utf8).write(to: store.fileURL)   // changed behind the store's back
            try store.save(sample("B"))   // not decoded first: the store wrote the file itself
            #expect(try decoded(store.fileURL) == sample("B"))
            try Data("{garbage".utf8).write(to: store.fileURL)
            #expect(throws: (any Error).self) { try LibraryStore(fileURL: store.fileURL).save(sample("C")) }   // a new store checks
            let reloaded = LibraryStore(fileURL: store.fileURL)
            try store.save(sample("D"))
            #expect(reloaded.load().data == sample("D"))
            try reloaded.save(sample("E"))   // known good after its own load
            #expect(try decoded(store.fileURL) == sample("E"))
        }
    }

    @Test func corruptCopiesNeverCollide() throws {
        try withStore { store, dir in
            for _ in 0..<2 {
                try Data("{garbage".utf8).write(to: store.fileURL)
                #expect(store.load().writable)
            }
            let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
            #expect(names.filter { $0.hasPrefix("library.corrupt-") }.count == 2)
        }
    }
}
