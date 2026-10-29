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

    /// A leftover of 1.1 or 1.2, last touched `days` ago.
    func leaveCopy(_ name: String, in dir: URL, daysOld days: Double) throws -> URL {
        let url = dir.appendingPathComponent(name)
        try Data("{}".utf8).write(to: url)
        let then = Date.now.addingTimeInterval(-days * 86400)
        try FileManager.default.setAttributes([.creationDate: then, .modificationDate: then], ofItemAtPath: url.path)
        return url
    }

    @Test func savingNeverMakesASafetyCopy() throws {
        try withStore { store, dir in
            try store.save(sample("A"))
            try store.save(sample("B"))
            try LibraryStore(fileURL: store.fileURL).save(sample("C"))   // a later run
            let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
            #expect(!names.contains { $0.hasPrefix("library-before") })
        }
    }

    @Test func oldSafetyCopiesGoOnceTheCurrentListHasLoadedCleanly() throws {
        try withStore { store, dir in
            try store.save(sample("A"))
            let old = try leaveCopy("library-before-1.1.json", in: dir, daysOld: 31)
            let recent = try leaveCopy("library-before-1.2.json", in: dir, daysOld: 29)
            _ = store.peek(notice: "")   // a read-only look changes nothing
            #expect(FileManager.default.fileExists(atPath: old.path))
            #expect(store.load().notice == nil)
            #expect(!FileManager.default.fileExists(atPath: old.path))
            #expect(FileManager.default.fileExists(atPath: recent.path))
        }
    }

    @Test func safetyCopiesStayWhileTheListIsDamaged() throws {
        try withStore { store, dir in
            let old = try leaveCopy("library-before-1.1.json", in: dir, daysOld: 90)
            try Data("{ not json".utf8).write(to: store.fileURL)
            _ = store.load()   // set aside, nothing to restore: a new list, not a clean load
            #expect(FileManager.default.fileExists(atPath: old.path))
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

    @Test func unreadableFileIsNotTreatedAsCorrupt() throws {
        try withStore { store, dir in
            try store.save(sample("A"))
            try store.save(sample("B"))
            let bytes = try Data(contentsOf: store.fileURL)
            try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: store.fileURL.path)
            defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: store.fileURL.path) }
            let fresh = LibraryStore(fileURL: store.fileURL)
            let loaded = fresh.load()
            #expect(!loaded.writable)
            #expect(loaded.notice?.contains("try again") == true)
            #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.hasPrefix("library.corrupt-") }.isEmpty)
            #expect(throws: (any Error).self) { try fresh.save(sample("C")) }
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: store.fileURL.path)
            #expect(try Data(contentsOf: store.fileURL) == bytes)
            #expect(fresh.load().data == sample("B"))
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

    /// The file both platforms must read: this test encodes a fixed list with the real encoder and compares it, as parsed
    /// JSON, with shared/vectors/library-golden.json. Regenerate it with LATER_WRITE_GOLDEN=1 swift test --filter libraryGolden.
    @Test func libraryGolden() throws {
        var data = LibraryData()
        let base = Date(timeIntervalSinceReferenceDate: 800_000_000)
        data.items = [
            Item(id: UUID(uuidString: "11111111-2222-3333-4444-555555555555")!, url: "https://example.com/a", title: "A page",
                 host: "example.com", kind: .reading, pageType: "example.com/*", browser: .helium, firstSaved: base,
                 lastSeen: base.addingTimeInterval(60.5), visits: 3, status: .done, doneAt: base.addingTimeInterval(90),
                 openedAt: base.addingTimeInterval(-3_600), historyVisits: 7, tabIndex: 2, windowRank: 1, historyChecked: "https://example.com/a", regular: true),
            Item(id: UUID(uuidString: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")!, url: "https://shop.example.com/p/1", title: "Plain",
                 host: "shop.example.com", kind: .shopping, pageType: "shop.example.com/p/*", browser: .chrome, firstSaved: base,
                 lastSeen: base, visits: 1, status: .active, doneAt: nil, regular: false),
        ]
        data.corrections = ["example.com/*": Correction(isSpecific: false, kind: .school), "x.com/*": Correction(kind: .posts),
                            "y.com/~": Correction(isSpecific: true, regular: true)]
        data.seen = ["0123abcd": base.addingTimeInterval(10)]
        data.dismissed = ["feedbeef": base.addingTimeInterval(20)]
        data.siteDays = ["cafe0123": [20_000, 20_001]]
        data.dwellSamples = [12.5, 130]
        let actual = try JSONSerialization.jsonObject(with: JSONEncoder().encode(data)) as! NSDictionary
        let url = Vectors.folder.appendingPathComponent("library-golden.json")
        if ProcessInfo.processInfo.environment["LATER_WRITE_GOLDEN"] == "1" {
            try JSONSerialization.data(withJSONObject: actual, options: [.prettyPrinted, .sortedKeys]).write(to: url)
        }
        let golden = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! NSDictionary
        #expect(actual == golden)
        // and the golden decodes back to the same list
        #expect(try JSONDecoder().decode(LibraryData.self, from: Data(contentsOf: url)) == data)
    }
}
