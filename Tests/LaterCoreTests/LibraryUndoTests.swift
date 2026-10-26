import Foundation
import Testing
@testable import LaterCore

/// The list's Undo and Redo, on a real UndoManager. Each `step` is one turn of the run loop in the app.
@Suite @MainActor struct LibraryUndoTests {
    let undo: UndoManager = {
        let undo = UndoManager()
        undo.groupsByEvent = false
        return undo
    }()
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    let product = Verdict(isSpecific: true, kind: .shopping, pageType: "a.com/p/*")

    func step(_ body: () -> Void) {
        undo.beginUndoGrouping()
        body()
        undo.endUndoGrouping()
    }

    /// Desk and Lamp share a page type; Other doesn't. Lamp is done.
    func library() -> Library {
        let lib = Library()
        lib.save(url: "https://a.com/p/1", title: "Desk", verdict: product, browser: .helium, now: t0)
        lib.save(url: "https://b.com/x", title: "Other", verdict: Verdict(isSpecific: true, kind: .reading, pageType: "b.com/*"),
                 browser: .helium, now: t0)
        lib.save(url: "https://a.com/p/2", title: "Lamp", verdict: product, browser: .helium, now: t0)
        lib.setDone(lib.data.items[2].id, true, now: t0)
        return lib
    }

    func id(_ lib: Library, _ title: String) -> UUID { lib.data.items.first { $0.title == title }!.id }

    @Test func deleteUndoesAndRedoes() {
        let lib = library(), before = lib.data
        step { lib.delete(id(lib, "Desk"), now: t0, undo: undo) }
        let after = lib.data
        #expect(undo.undoActionName == "Delete")
        undo.undo()
        #expect(lib.data == before)
        undo.redo()
        #expect(lib.data == after)
    }

    /// Both pages of one type in one batch, after an earlier correction that must survive.
    @Test func notAnIdeaUndoesExactlyEvenForPagesOfOneType() {
        let lib = library()
        lib.move(id(lib, "Desk"), to: .school)
        let before = lib.data
        step { [id(lib, "Lamp"), id(lib, "Desk")].forEach { lib.notAnIdea($0, undo: undo) } }
        #expect(lib.data.items.map(\.title) == ["Other"])
        #expect(lib.data.corrections["a.com/p/*"] == Correction(isSpecific: false, kind: .school))
        let after = lib.data
        undo.undo()
        #expect(lib.data == before)
        undo.redo()
        #expect(lib.data == after)
    }

    /// Move changes every page of the type, so Undo puts back each page's own kind and the type's correction.
    @Test func moveUndoesEveryPageOfTheType() {
        let lib = library()
        lib.setKinds([id(lib, "Lamp"): .reading])   // the two pages of the type start on different kinds
        let before = lib.data
        step { lib.move(id(lib, "Desk"), to: .school, undo: undo) }
        #expect(lib.data.items.map(\.kind) == [.school, .reading, .school])
        #expect(undo.undoActionName == "Move to School")
        let after = lib.data
        undo.undo()
        #expect(lib.data == before)
        #expect(lib.data.corrections["a.com/p/*"] == nil)
        undo.redo()
        #expect(lib.data == after)
    }

    @Test func emptyDoneIsOneUndo() {
        let lib = library()
        lib.setDone(id(lib, "Desk"), true, now: t0)
        let before = lib.data
        step { lib.emptyDone(now: t0, undo: undo) }
        #expect(lib.data.items.map(\.title) == ["Other"])
        #expect(lib.isDismissed("https://a.com/p/1"))
        let after = lib.data
        undo.undo()
        #expect(lib.data == before)
        #expect(!lib.isDismissed("https://a.com/p/1"))
        #expect(!undo.canUndo)
        undo.redo()
        #expect(lib.data == after)
    }

    /// Redo of Mark Done keeps the completion date it first had; Undo of Move Back puts back the original one.
    @Test func doneDatesSurviveUndoAndRedo() {
        let lib = library()
        let desk = id(lib, "Desk"), lamp = id(lib, "Lamp"), t1 = t0 + 3600
        step { lib.setDone(desk, true, now: t1, undo: undo) }
        undo.undo()
        #expect(lib.data.items[0].status == .active && lib.data.items[0].doneAt == nil)
        undo.redo()
        #expect(lib.data.items[0].doneAt == t1)
        step { lib.setDone(lamp, false, now: t1 + 60, undo: undo) }
        #expect(undo.undoActionName == "Move Back")
        undo.undo()
        #expect(lib.data.items[2].doneAt == t0)
    }
}
