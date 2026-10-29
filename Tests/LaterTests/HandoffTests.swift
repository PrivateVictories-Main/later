import Foundation
import Testing
@testable import Later
@testable import LaterCore

/// The window app's side of the handoff, with a second lock standing in for the background helper.
@Suite @MainActor struct HandoffTests {
    private func lockPair() throws -> (path: String, helper: EngineLock, app: EngineLock) {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("later-handoff-\(UUID().uuidString)").path
        return (path, try #require(EngineLock(path: path)), try #require(EngineLock(path: path)))
    }

    @Test func nobodyHoldingTheLockIsTakenAtOnce() throws {
        let (path, _, app) = try lockPair()
        defer { unlink(path) }
        var nudges = 0
        let handoff = WindowHandoff(lock: app) { nudges += 1 }
        #expect(handoff.take() == .immediate)
        #expect(!handoff.isReadOnly)
        #expect(nudges == 0)   // nobody to ask
    }

    @Test func aHelperThatLetsGoAfterBeingAskedHandsOver() throws {
        let (path, helper, app) = try lockPair()
        defer { unlink(path) }
        #expect(helper.acquire(timeout: 0))
        let handoff = WindowHandoff(lock: app) { helper.release() }   // the helper answers the request
        #expect(handoff.take() == .afterHandoff)
        #expect(!handoff.isReadOnly)
    }

    @Test func aHelperThatNeverLetsGoLeavesTheWindowReadOnly() throws {
        let (path, helper, app) = try lockPair()
        defer { unlink(path) }
        #expect(helper.acquire(timeout: 0))
        let handoff = WindowHandoff(lock: app) {}
        #expect(handoff.take(timeout: 0.3) == .readOnly)
        #expect(handoff.isReadOnly)
    }

    @Test func comingBackToTheWindowTriesAgainAndTakesOverWhenTheLockIsFree() throws {
        let (path, helper, app) = try lockPair()
        defer { unlink(path) }
        #expect(helper.acquire(timeout: 0))
        var answering = false
        let handoff = WindowHandoff(lock: app) { if answering { helper.release() } }
        #expect(handoff.take(timeout: 0.2) == .readOnly)
        #expect(!handoff.retry(timeout: 0.05))   // still held
        #expect(handoff.isReadOnly)
        answering = true
        #expect(handoff.retry(timeout: 0.5))
        #expect(!handoff.isReadOnly)
    }

    @Test func retryingWhenNotReadOnlyDoesNothing() throws {
        let (path, _, app) = try lockPair()
        defer { unlink(path) }
        var nudges = 0
        let handoff = WindowHandoff(lock: app) { nudges += 1 }
        handoff.take()
        #expect(!handoff.retry())
        #expect(nudges == 0)
    }

    @Test func aMissingHelperIsRecognisedOnlyWhenOneWasWanted() {
        func missing(_ outcome: WindowHandoff.Outcome, wanted: Bool = true, installed: Bool = true) -> Bool {
            LoginItem.helperWasMissing(outcome, wanted: wanted, installed: installed)
        }
        #expect(missing(.immediate))                         // nobody held the lock, yet watching is on
        #expect(!missing(.afterHandoff))                     // the helper handed over: it was running
        #expect(!missing(.readOnly))                         // something holds the lock
        #expect(!missing(.immediate, wanted: false))         // the user turned it off
        #expect(!missing(.immediate, installed: false))      // a development build has no helper
    }
}
