import Foundation

/// Which pages of a list are selected, and how clicks and the arrow keys change that, as in any Mac list: a click
/// selects one page, ⌘-click adds or removes one, ⇧-click selects the run from the last click; the arrows move one page,
/// ⇧ grows the run, ⌘ goes to the first or last page.
public struct ListSelection: Equatable, Sendable {
    public var selected: Set<UUID> = []
    /// The page the keyboard is on: arrows move from it.
    public private(set) var cursor: UUID?
    /// Where a ⇧ selection runs from.
    private var anchor: UUID?

    public init() {}

    public mutating func click(_ id: UUID, in rows: [UUID], command: Bool, shift: Bool) {
        if command {
            if selected.remove(id) == nil { selected.insert(id) }
            anchor = id
            cursor = id
        } else if shift, let anchor, rows.contains(anchor) {
            selected = run(from: anchor, to: id, in: rows)
            cursor = id
        } else {
            select(id)
        }
    }

    public mutating func move(by step: Int, extending: Bool, toEnd: Bool, in rows: [UUID]) {
        guard !rows.isEmpty else { return }
        let from = cursor.flatMap(rows.firstIndex(of:)) ?? (step > 0 ? -1 : rows.count)
        let to = rows[toEnd ? (step > 0 ? rows.count - 1 : 0) : min(max(from + step, 0), rows.count - 1)]
        if extending, let anchor, rows.contains(anchor) {
            selected = run(from: anchor, to: to, in: rows)
            cursor = to
        } else {
            select(to)
        }
    }

    public mutating func select(_ id: UUID?) {
        selected = id.map { [$0] } ?? []
        anchor = id
        cursor = id
    }

    public mutating func selectAll(_ rows: [UUID]) {
        selected = Set(rows)
        anchor = rows.first
        cursor = rows.last
    }

    /// After a change to the pages `changed`, with the rows before and after it: what's gone is deselected, and when the
    /// selection was among them and nothing is left, the page after the last of them (or else before it) is selected, so
    /// working down a list takes one click per page. When some of the selection stays, the keyboard moves on from the
    /// nearest of it, as it would from where it was.
    public mutating func keep(_ rows: [UUID], after changed: Set<UUID>, before: [UUID]) {
        let present = Set(rows)
        let wasSelected = !changed.isDisjoint(with: selected)
        selected = selected.filter(present.contains)
        if wasSelected, selected.isEmpty, let last = before.lastIndex(where: changed.contains) {
            select(before[(last + 1)...].first(where: present.contains) ?? before[..<last].last(where: present.contains))
            return
        }
        if let gone = cursor, !present.contains(gone) { cursor = nearestSelected(to: gone, in: before) }
        if let gone = anchor, !present.contains(gone) { anchor = nearestSelected(to: gone, in: before) }
    }

    /// The selected row closest to `id` in `rows` (the earlier one on a tie); nil when nothing is selected.
    private func nearestSelected(to id: UUID, in rows: [UUID]) -> UUID? {
        guard let i = rows.firstIndex(of: id) else { return nil }
        return rows.indices.filter { selected.contains(rows[$0]) }.min { abs($0 - i) < abs($1 - i) }.map { rows[$0] }
    }

    private func run(from a: UUID, to b: UUID, in rows: [UUID]) -> Set<UUID> {
        guard let i = rows.firstIndex(of: a), let j = rows.firstIndex(of: b) else { return [b] }
        return Set(rows[min(i, j)...max(i, j)])
    }
}
