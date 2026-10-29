import Foundation
import Testing
@testable import LaterCore

@Suite struct AgentStatusTests {
    func withFile(_ body: (AgentStatusFile, URL) throws -> Void) throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("later-status-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try body(AgentStatusFile(url: dir.appendingPathComponent("agent-status.json")), dir)
    }

    @Test func nothingWrittenMeansNoProblem() throws {
        try withFile { file, _ in #expect(file.read() == nil) }
    }

    @Test func aProblemIsWrittenAndReadBack() throws {
        try withFile { file, _ in
            file.write(AgentStatus(state: .cantSave, count: 12))
            #expect(file.read() == AgentStatus(state: .cantSave, count: 12))
            file.write(AgentStatus(state: .needsRecovery, count: 0))   // the newest problem replaces the old one
            #expect(file.read()?.state == .needsRecovery)
        }
    }

    @Test func theFileHoldsOnlyAStateAndACount() throws {
        try withFile { file, _ in
            file.write(AgentStatus(state: .cantOpenStorage, count: 3))
            let json = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: file.url)) as? [String: Any])
            #expect(Set(json.keys) == ["state", "count"])
            #expect(json["state"] as? String == "cantOpenStorage")
        }
    }

    @Test func takeReadsAndClears() throws {
        try withFile { file, dir in
            file.write(AgentStatus(state: .cantSave, count: 1))
            #expect(file.take()?.state == .cantSave)
            #expect(file.read() == nil)
            let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
            #expect(names.isEmpty)
            #expect(file.take() == nil)
        }
    }

    @Test func clearIsHarmlessWithNothingThere() throws {
        try withFile { file, _ in file.clear() }
    }

    @Test func anUnreadableFileIsNoProblem() throws {
        try withFile { file, _ in
            try Data("not json".utf8).write(to: file.url)
            #expect(file.read() == nil)
        }
    }

    @Test func everyStateHasAPlainSentence() {
        for state in [AgentStatus.State.needsRecovery, .cantOpenStorage, .cantSave] {
            #expect(AgentStatus(state: state, count: 0).notice.hasSuffix("."))
        }
    }
}
