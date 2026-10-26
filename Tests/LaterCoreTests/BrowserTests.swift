import Testing
@testable import LaterCore

@Suite struct BrowserTests {
    @Test func matchesEveryKnownBundleID() {
        #expect(Browser.matching(bundleID: "net.imput.helium") == .helium)
        #expect(Browser.helium.bundleIDs == ["net.imput.helium"])   // no extra builds unless a build adds them
        #expect(Browser.matching(bundleID: "com.google.Chrome") == .chrome)
    }

    @Test func unknownOrMissingBundleIDMatchesNothing() {
        #expect(Browser.matching(bundleID: "com.apple.Safari") == nil)
        #expect(Browser.matching(bundleID: nil) == nil)
    }
}
