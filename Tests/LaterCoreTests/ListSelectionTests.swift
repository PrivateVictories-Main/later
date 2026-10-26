import Foundation
import Testing
@testable import LaterCore

@Suite struct ListSelectionTests {
    let rows = (0..<5).map { _ in UUID() }

    @Test func clicksSelectToggleAndRun() {
        var selection = ListSelection()
        selection.click(rows[1], in: rows, command: false, shift: false)
        #expect(selection.selected == [rows[1]])
        selection.click(rows[3], in: rows, command: true, shift: false)
        #expect(selection.selected == [rows[1], rows[3]])
        selection.click(rows[3], in: rows, command: true, shift: false)
        #expect(selection.selected == [rows[1]])
        // ⇧ runs from the last click, either way.
        selection.click(rows[1], in: rows, command: false, shift: false)
        selection.click(rows[4], in: rows, command: false, shift: true)
        #expect(selection.selected == Set(rows[1...4]))
        selection.click(rows[0], in: rows, command: false, shift: true)
        #expect(selection.selected == Set(rows[0...1]))
    }

    @Test func arrowsMoveGrowAndJump() {
        var selection = ListSelection()
        selection.move(by: 1, extending: false, toEnd: false, in: rows)
        #expect(selection.selected == [rows[0]])
        selection.move(by: -1, extending: false, toEnd: false, in: rows)   // stays at the top
        #expect(selection.selected == [rows[0]])
        selection.move(by: 1, extending: true, toEnd: false, in: rows)
        selection.move(by: 1, extending: true, toEnd: false, in: rows)
        #expect(selection.selected == Set(rows[0...2]))
        selection.move(by: 1, extending: false, toEnd: true, in: rows)
        #expect(selection.selected == [rows[4]] && selection.cursor == rows[4])
        var fresh = ListSelection()
        fresh.move(by: -1, extending: false, toEnd: false, in: rows)   // up with nothing selected starts at the bottom
        #expect(fresh.selected == [rows[4]])
    }

    @Test func aRemovedSelectionMovesToTheNextPage() {
        var selection = ListSelection()
        selection.select(rows[2])
        selection.keep(rows.filter { $0 != rows[2] }, after: [rows[2]], before: rows)
        #expect(selection.selected == [rows[3]])
        // The last page gone: the one before it.
        selection.select(rows[4])
        selection.keep(Array(rows[0...3]), after: [rows[4]], before: rows)
        #expect(selection.selected == [rows[3]])
        // A page that wasn't selected changes nothing.
        selection.select(rows[0])
        selection.keep(Array(rows[0...3]), after: [rows[4]], before: rows)
        #expect(selection.selected == [rows[0]])
    }

    /// B and D selected, the keyboard on D; D alone is marked Done from its row. The arrows carry on from B, not the top.
    @Test func aPartlyRemovedSelectionKeepsTheKeyboardOnWhatStays() {
        var selection = ListSelection()
        selection.click(rows[1], in: rows, command: false, shift: false)
        selection.click(rows[3], in: rows, command: true, shift: false)
        let after = rows.filter { $0 != rows[3] }
        selection.keep(after, after: [rows[3]], before: rows)
        #expect(selection.selected == [rows[1]])
        #expect(selection.cursor == rows[1])
        var extending = selection
        extending.move(by: 1, extending: true, toEnd: false, in: after)
        #expect(extending.selected == [rows[1], rows[2]])   // ⇧ runs from B too
        selection.move(by: 1, extending: false, toEnd: false, in: after)
        #expect(selection.selected == [rows[2]])
    }
}
