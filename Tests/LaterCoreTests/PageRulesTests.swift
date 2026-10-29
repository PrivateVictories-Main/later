import Foundation
import Testing
@testable import LaterCore

/// The table-driven page cases live in shared/vectors (see VectorTests); what stays here compares page types.
@Suite struct PageRulesTests {
    @Test func hashRoutesAreJudgedByTheirRoute() throws {
        let item = try #require(LinkCleaner.clean("https://app.example.com/#/items/9876543"))
        let itemVerdict = PageRules.judge(item, title: "Walnut desk")
        #expect(itemVerdict.isSpecific)
        #expect(itemVerdict.pageType == "app.example.com/#/items/*")
        let dashboard = try #require(LinkCleaner.clean("https://app.example.com/#/dashboard"))
        let dashboardVerdict = PageRules.judge(dashboard, title: "Dashboard")
        #expect(dashboardVerdict.isSpecific)   // a plain route is kept too, under a page type of its own
        #expect(dashboardVerdict.pageType != itemVerdict.pageType)
    }

    /// The Judge applies corrections to the rules-only verdict on every read: a "keep" correction must not bring a hard skip back.
    @Test func aKeepCorrectionAppliedLaterStillLeavesAHardSkipSkipped() throws {
        let logout = try #require(LinkCleaner.clean("https://example.com/logout"))
        let verdict = PageRules.judge(logout, title: "Signing out")
        #expect(!verdict.isSpecific)
        let keep = [verdict.pageType: Correction(isSpecific: true)]
        #expect(!PageRules.apply(keep, to: verdict).isSpecific)
    }

    static let routeMarkers = ["#/", "#!/", "#%2F", "#%21/"].flatMap { [($0, ""), ($0, "?returnTo=%2Fsearch%3Fq%3Ddesk")] }

    /// A correction on the item type must not touch the dashboard's verdict, whichever way the route is written.
    @Test(arguments: routeMarkers)
    func routeMarkersKeepItemsAndDashboardsApart(_ marker: String, _ query: String) throws {
        let item = try #require(LinkCleaner.clean("https://app.example.com/\(marker)items/ABCDEFG1\(query)"))
        let dashboard = try #require(LinkCleaner.clean("https://app.example.com/\(marker)dashboard\(query)"))
        let itemVerdict = PageRules.judge(item, title: "Walnut desk organizer")
        let dashboardVerdict = PageRules.judge(dashboard, title: "Dashboard")
        #expect(dashboardVerdict.pageType != itemVerdict.pageType)
        let corrections = ["app.example.com/#/items/*": Correction(isSpecific: false)]
        #expect(PageRules.judge(dashboard, title: "Dashboard", corrections: corrections) == dashboardVerdict)
    }
}

@Suite struct PathPatternTests {
    static func segments(_ path: String) -> [String] { path.split(separator: "/").map(String.init) }

    @Test func doubleStarMatchesZeroSegments() {
        #expect(("/explore/**" as PathPattern).matches(Self.segments("/explore"), queryKeys: []))
    }

    @Test func missingQueryKeyFails() {
        let watch: PathPattern = "/watch?v"
        #expect(!watch.matches(Self.segments("/watch"), queryKeys: []))
        #expect(watch.matches(Self.segments("/watch"), queryKeys: ["v"]))
    }

    @Test func segmentsIgnoreCase() {
        #expect(("/wiki/Main_Page" as PathPattern).matches(Self.segments("/WIKI/main_page"), queryKeys: []))
    }

    @Test func trailingSlashesDontMatter() {
        #expect(("/r/*" as PathPattern).matches(Self.segments("/r/swift/"), queryKeys: []))
    }
}
