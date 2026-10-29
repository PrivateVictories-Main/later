import Testing
@testable import Later

@Suite @MainActor struct SecretsNoticeTests {
    @Test func nothingRemovedSaysNothing() {
        #expect(Engine.secretsNotice(removed: 0) == nil)
    }

    @Test func removedPagesAreCountedWithoutNamingAny() {
        #expect(Engine.secretsNotice(removed: 1) == "Later removed 1 saved page whose address carried a one-time secret.")
        #expect(Engine.secretsNotice(removed: 4) == "Later removed 4 saved pages whose address carried a one-time secret.")
    }
}
