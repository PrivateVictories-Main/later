import Foundation

/// The user's changes to the list with their Undo and Redo, on the app's undo history for the list. Changes registered in
/// the same turn of the run loop (a multiple selection) are one Undo. Each Redo repeats the change exactly as it was
/// made, so Mark Done keeps its first completion date.
extension Library {
    /// Deleting is instant; Undo puts the page back where it was.
    public func delete(_ id: UUID, now: Date, undo: UndoManager) {
        guard let position = data.items.firstIndex(where: { $0.id == id }) else { return }
        let item = data.items[position]
        undoable("Delete", undo: undo) { $0.delete(id, now: now) } revert: { $0.restore(item, at: position) }
    }

    /// Undo puts the page back at its place, and its page type's correction as it was.
    public func notAnIdea(_ id: UUID, undo: UndoManager) {
        guard let position = data.items.firstIndex(where: { $0.id == id }) else { return }
        let item = data.items[position], correction = data.corrections[item.pageType]
        undoable("Not an Idea", undo: undo) { $0.notAnIdea(id) } revert: {
            $0.setCorrection(correction, for: item.pageType)
            $0.restore(item, at: position)
        }
    }

    /// Mark Done and Move Back undo too, so Undo right after one never reaches past it to an earlier Delete. Undoing
    /// Move Back keeps the original done date.
    public func setDone(_ id: UUID, _ done: Bool, now: Date, undo: UndoManager) {
        guard let item = data.items.first(where: { $0.id == id }), (item.status == .done) != done else { return }
        let doneAt = item.doneAt ?? now
        undoable(done ? "Mark Done" : "Move Back", undo: undo) { $0.setDone(id, done, now: now) } revert: {
            $0.setDone(id, !done, now: doneAt)
        }
    }

    /// A move changes the kind of every saved page of its type, so Undo puts back each one's kind and the type's
    /// correction.
    public func move(_ id: UUID, to kind: Kind, undo: UndoManager) {
        guard let item = data.items.first(where: { $0.id == id }), item.kind != kind else { return }
        let type = item.pageType, correction = data.corrections[type]
        let kinds = Dictionary(uniqueKeysWithValues: data.items.filter { $0.pageType == type }.map { ($0.id, $0.kind) })
        undoable("Move to \(kind.title)", undo: undo) { $0.move(id, to: kind) } revert: {
            $0.setCorrection(correction, for: type)
            $0.setKinds(kinds)
        }
    }

    /// "Keep in my list" and "This is a regular" change the page type's correction and every saved page of that type, so
    /// Undo puts back both.
    public func keepInList(_ id: UUID, undo: UndoManager) { choose(id, "Keep in My List", undo: undo) { $0.keepInList(id) } }
    public func markRegular(_ id: UUID, undo: UndoManager) { choose(id, "Mark as Regular", undo: undo) { $0.markRegular(id) } }

    private func choose(_ id: UUID, _ name: String, undo: UndoManager, change: @escaping @MainActor (Library) -> Void) {
        guard let item = data.items.first(where: { $0.id == id }) else { return }
        let type = item.pageType, correction = data.corrections[type]
        let flags = Dictionary(uniqueKeysWithValues: data.items.filter { $0.pageType == type }.map { ($0.id, $0.regular) })
        undoable(name, undo: undo, change: change) {
            $0.setCorrection(correction, for: type)
            $0.setRegulars(flags)
        }
    }

    /// Empty Done is one Undo, however many pages it deletes.
    public func emptyDone(now: Date, undo: UndoManager) {
        let done = data.items.enumerated().filter { $0.element.status == .done }.map { (item: $0.element, position: $0.offset) }
        guard !done.isEmpty else { return }
        undoable("Empty Done", undo: undo) { $0.emptyDone(now: now) } revert: { $0.restore(done) }
    }

    /// Makes `change`, with `revert` as its Undo, whose Redo is `change` again, and so on.
    private func undoable(_ name: String, undo: UndoManager, change: @escaping @MainActor (Library) -> Void,
                          revert: @escaping @MainActor (Library) -> Void) {
        change(self)
        undo.registerUndo(withTarget: self) { [weak undo] library in
            MainActor.assumeIsolated {
                guard let undo else { return }
                library.undoable(name, undo: undo, change: revert, revert: change)
            }
        }
        undo.setActionName(name)
    }
}
