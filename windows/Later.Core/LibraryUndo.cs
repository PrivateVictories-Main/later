namespace Later.Core;

/// The user's changes to the list with their Undo and Redo. Changes made between `BeginGroup` and `EndGroup` (a multiple
/// selection) are one Undo. Each Redo repeats the change exactly as it was made, so Mark Done keeps its first completion date.
public sealed class UndoStack
{
    sealed record Entry(string Name, Action Change, Action Revert);

    readonly Stack<Entry> undo = new();
    readonly Stack<Entry> redo = new();
    List<Entry>? group;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoName => undo.Count > 0 ? undo.Peek().Name : null;
    public string? RedoName => redo.Count > 0 ? redo.Peek().Name : null;

    /// Makes `change`, with `revert` as its Undo, whose Redo is `change` again.
    public void Do(string name, Action change, Action revert)
    {
        change();
        redo.Clear();
        var entry = new Entry(name, change, revert);
        if (group is not null) group.Add(entry); else undo.Push(entry);
    }

    public void BeginGroup() => group ??= new List<Entry>();

    /// The group becomes one entry named by its first change; Undo reverts its changes in reverse order.
    public void EndGroup()
    {
        var entries = group;
        group = null;
        if (entries is null || entries.Count == 0) return;
        var all = entries.ToList();
        undo.Push(new Entry(all[0].Name, () => { foreach (var e in all) e.Change(); }, () => { foreach (var e in Enumerable.Reverse(all)) e.Revert(); }));
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var entry = undo.Pop();
        entry.Revert();
        redo.Push(entry);
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        var entry = redo.Pop();
        entry.Change();
        undo.Push(entry);
    }
}

/// The five undoable changes to the list, as in the Mac app.
public static class LibraryUndo
{
    /// Deleting is instant; Undo puts the page back where it was.
    public static void Delete(this Library library, Guid id, DateTime now, UndoStack undo)
    {
        int position = library.Data.Items.FindIndex(i => i.Id == id);
        if (position < 0) return;
        var item = library.Data.Items[position];
        undo.Do("Delete", () => library.Delete(id, now), () => library.Restore(item, position));
    }

    /// Undo puts the page back at its place, and its page type's correction as it was.
    public static void NotAnIdea(this Library library, Guid id, UndoStack undo)
    {
        int position = library.Data.Items.FindIndex(i => i.Id == id);
        if (position < 0) return;
        var item = library.Data.Items[position];
        var correction = library.Data.Corrections.GetValueOrDefault(item.PageType);
        undo.Do("Not an Idea", () => library.NotAnIdea(id), () =>
        {
            library.SetCorrection(correction, item.PageType);
            library.Restore(item, position);
        });
    }

    /// Mark Done and Move Back undo too, so Undo right after one never reaches past it to an earlier Delete. Undoing
    /// Move Back keeps the original done date.
    public static void SetDone(this Library library, Guid id, bool done, DateTime now, UndoStack undo)
    {
        var item = library.Data.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || (item.Status == ItemStatus.Done) == done) return;
        var doneAt = item.DoneAt ?? now;
        undo.Do(done ? "Mark Done" : "Move Back", () => library.SetDone(id, done, now), () => library.SetDone(id, !done, doneAt));
    }

    /// A move changes the kind of every saved page of its type, so Undo puts back each one's kind and the type's
    /// correction.
    public static void Move(this Library library, Guid id, Kind to, UndoStack undo)
    {
        var item = library.Data.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || item.Kind == to) return;
        var type = item.PageType;
        var correction = library.Data.Corrections.GetValueOrDefault(type);
        var kinds = library.Data.Items.Where(i => i.PageType == type).ToDictionary(i => i.Id, i => i.Kind);
        undo.Do("Move to " + to.Title(), () => library.Move(id, to), () =>
        {
            library.SetCorrection(correction, type);
            library.SetKinds(kinds);
        });
    }

    /// Empty Done is one Undo, however many pages it deletes.
    public static void EmptyDone(this Library library, DateTime now, UndoStack undo)
    {
        var done = library.Data.Items.Select((item, position) => (Item: item, Position: position))
            .Where(x => x.Item.Status == ItemStatus.Done).ToList();
        if (done.Count == 0) return;
        undo.Do("Empty Done", () => library.EmptyDone(now), () => library.Restore(done));
    }
}
