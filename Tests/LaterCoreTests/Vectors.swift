import Foundation
import Testing
@testable import LaterCore

/// Table-driven cases shared with the Windows suite: `shared/vectors/*.json`. See that folder's README for the schema.
enum Vectors {
    static let folder = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // LaterCoreTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // repository root
        .appendingPathComponent("shared/vectors", isDirectory: true)

    /// Loud on failure: an empty argument list would make a parameterized test pass without testing anything.
    static func load<T: Decodable>(_ name: String, as type: T.Type = T.self) -> T {
        do {
            return try JSONDecoder().decode(T.self, from: Data(contentsOf: folder.appendingPathComponent(name)))
        } catch {
            fatalError("Can't load shared/vectors/\(name): \(error)")
        }
    }
}

struct CleanVector: Decodable, Sendable, CustomTestStringConvertible {
    let raw: String
    let expected: String
    var testDescription: String { raw }
}

struct StorefrontVector: Decodable, Sendable, CustomTestStringConvertible {
    let host: String
    let name: String
    let `is`: Bool
    var testDescription: String { host }
}

struct LinkCleanVectors: Decodable, Sendable {
    let counts: [String: Int]
    let clean: [CleanVector]
    let refuse: [String]
    let distinct: [[String]]
    let storefront: [StorefrontVector]
    static let shared: LinkCleanVectors = Vectors.load("link-clean.json")
}

struct KeyVector: Decodable, Sendable, CustomTestStringConvertible {
    let raw: String
    let key: String
    var testDescription: String { raw }
}

struct PageKeyVectors: Decodable, Sendable {
    let counts: [String: Int]
    let same: [[String]]
    let different: [[String]]
    let keys: [KeyVector]
    static let shared: PageKeyVectors = Vectors.load("page-key.json")
}

struct CorrectionVector: Decodable, Sendable {
    let isSpecific: Bool?
    let kind: String?
}

struct JudgeCase: Decodable, Sendable, CustomTestStringConvertible {
    let url: String
    let title: String
    let specific: Bool?
    let kind: String?
    let pageType: String?
    let pageTypeSuffix: String?
    let notPageTypeSuffix: String?
    let feeds: Bool?
    let corrections: [String: CorrectionVector]?
    var testDescription: String { url + (title.isEmpty ? "" : "  [\(title)]") }

    func check() throws {
        let link = try #require(LinkCleaner.clean(url), "\(url) must clean")
        var fixes: [String: Correction] = [:]
        for (type, c) in corrections ?? [:] {
            fixes[type] = Correction(isSpecific: c.isSpecific, kind: try c.kind.map { try #require(Kind(rawValue: $0), "unknown kind \($0)") })
        }
        let verdict = PageRules.judge(link, title: title, corrections: fixes, savesFeedHomes: feeds ?? false)
        if let specific { #expect(verdict.isSpecific == specific, "specific: \(testDescription)") }
        if let kind { #expect(verdict.kind.rawValue == kind, "kind: \(testDescription)") }
        if let pageType { #expect(verdict.pageType == pageType, "pageType: \(testDescription)") }
        if let pageTypeSuffix { #expect(verdict.pageType.hasSuffix(pageTypeSuffix), "pageType suffix: \(testDescription)") }
        if let notPageTypeSuffix { #expect(!verdict.pageType.hasSuffix(notPageTypeSuffix), "pageType suffix: \(testDescription)") }
    }
}

struct JudgeVectors: Decodable, Sendable {
    let counts: [String: Int]
    let cases: [JudgeCase]
    static let pageRules: JudgeVectors = Vectors.load("page-rules.json")
    static let siteRules: JudgeVectors = Vectors.load("site-rules.json")
}

@Suite struct VectorTests {
    @Test func countsMatchTheTables() {
        let l = LinkCleanVectors.shared, k = PageKeyVectors.shared
        #expect(l.counts == ["clean": l.clean.count, "refuse": l.refuse.count, "distinct": l.distinct.count, "storefront": l.storefront.count])
        #expect(k.counts == ["same": k.same.count, "different": k.different.count, "keys": k.keys.count])
        #expect(JudgeVectors.pageRules.counts == ["cases": JudgeVectors.pageRules.cases.count])
        #expect(JudgeVectors.siteRules.counts == ["cases": JudgeVectors.siteRules.cases.count])
        #expect(l.distinct.allSatisfy { $0.count == 2 } && k.same.allSatisfy { $0.count == 2 } && k.different.allSatisfy { $0.count == 2 })
    }

    @Test(arguments: LinkCleanVectors.shared.clean)
    func cleans(_ v: CleanVector) {
        #expect(LinkCleaner.clean(v.raw)?.absoluteString == v.expected)
    }

    @Test(arguments: LinkCleanVectors.shared.refuse)
    func refuses(_ raw: String) {
        #expect(LinkCleaner.clean(raw) == nil)
    }

    @Test(arguments: LinkCleanVectors.shared.distinct)
    func distinctLinksStayDistinct(_ pair: [String]) throws {
        let a = try #require(LinkCleaner.clean(pair[0])), b = try #require(LinkCleaner.clean(pair[1]))
        #expect(a != b, "\(pair[0]) vs \(pair[1])")
    }

    @Test(arguments: LinkCleanVectors.shared.storefront)
    func storefronts(_ v: StorefrontVector) {
        #expect(LinkCleaner.isStorefront(v.host, name: v.name) == v.is)
    }

    private func key(_ raw: String) throws -> String { LinkCleaner.pageKey(try #require(LinkCleaner.clean(raw), "\(raw) must clean")) }

    @Test(arguments: PageKeyVectors.shared.same)
    func sameLinkWrittenTwoWays(_ pair: [String]) throws {
        #expect(try key(pair[0]) == key(pair[1]), "\(pair[0]) vs \(pair[1])")
    }

    @Test(arguments: PageKeyVectors.shared.different)
    func differentPagesStayApart(_ pair: [String]) throws {
        #expect(try key(pair[0]) != key(pair[1]), "\(pair[0]) vs \(pair[1])")
    }

    @Test(arguments: PageKeyVectors.shared.keys)
    func exactKeys(_ v: KeyVector) throws {
        #expect(try key(v.raw) == v.key)
    }

    @Test(arguments: JudgeVectors.pageRules.cases)
    func pageRules(_ c: JudgeCase) throws { try c.check() }

    @Test(arguments: JudgeVectors.siteRules.cases)
    func siteRules(_ c: JudgeCase) throws { try c.check() }
}
