import ServiceManagement
import Testing
@testable import Later

struct LoginItemTests {
    @Test func upgradingFrom12KeepsWhatTheUserChose() {
        #expect(LoginItem.initiallyWanted(oldItemWasOn: true, decidedBefore: true))     // 1.2 had it on
        #expect(!LoginItem.initiallyWanted(oldItemWasOn: false, decidedBefore: true))   // decided, now off: the user turned it off
        #expect(LoginItem.initiallyWanted(oldItemWasOn: false, decidedBefore: false))   // a first install: on by default
        #expect(LoginItem.initiallyWanted(oldItemWasOn: true, decidedBefore: false))
    }

    @Test func theAgentIsRegisteredAgainOnlyWhenAnotherBuildRegisteredIt() {
        func repair(_ status: SMAppService.Status, wanted: Bool = true, pinned: String?, current: String? = "b") -> Bool {
            LoginItem.needsRepair(wanted: wanted, status: status, pinned: pinned, current: current)
        }
        #expect(repair(.enabled, pinned: "a"))
        #expect(repair(.enabled, pinned: nil))           // registered by a build that kept no hash
        #expect(!repair(.enabled, pinned: "b"))          // same build
        #expect(!repair(.enabled, wanted: false, pinned: "a"))
        #expect(!repair(.requiresApproval, pinned: "a")) // the user switched it off in Login Items: leave it
        #expect(!repair(.notRegistered, pinned: "a"))    // the plain register path handles it
        #expect(!repair(.enabled, pinned: "a", current: nil))   // hash unreadable: change nothing
    }
}
