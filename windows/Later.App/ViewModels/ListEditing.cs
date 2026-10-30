using Later.Core;

namespace Later.App.ViewModels;

/// Where the user's changes to the list are made: on the engine thread (`AppEngine`), which owns the library and its undo history,
/// or, in a sample run, on the window's own thread.
internal interface IListEditor
{
    /// Runs `work` where the library lives, then sends the list as it is, with `token` (see `LibraryViewModel.BeginChange`).
    void Apply(int token, Action<Library, UndoStack> work);

    /// Takes back the last change (or does it again), then sends the list.
    void Undo();
    void Redo();
}

/// What each action does to a library. Several pages changed at once are one Undo, as on the Mac.
internal static class ItemChanges
{
    public static void SetDone(Library library, UndoStack undo, IEnumerable<Guid> ids, bool done, DateTime now) =>
        Each(undo, ids, id => library.SetDone(id, done, now, undo));

    public static void Move(Library library, UndoStack undo, IEnumerable<Guid> ids, Kind kind) =>
        Each(undo, ids, id => library.Move(id, kind, undo));

    public static void KeepInList(Library library, UndoStack undo, IEnumerable<Guid> ids) => Each(undo, ids, id => library.KeepInList(id, undo));

    public static void MarkRegular(Library library, UndoStack undo, IEnumerable<Guid> ids) => Each(undo, ids, id => library.MarkRegular(id, undo));

    public static void NotAnIdea(Library library, UndoStack undo, IEnumerable<Guid> ids) => Each(undo, ids, id => library.NotAnIdea(id, undo));

    public static void Delete(Library library, UndoStack undo, IEnumerable<Guid> ids, DateTime now) => Each(undo, ids, id => library.Delete(id, now, undo));

    /// Everything in Done, however many pages that is, as one Undo.
    public static void EmptyDone(Library library, UndoStack undo, DateTime now) => library.EmptyDone(now, undo);

    static void Each(UndoStack undo, IEnumerable<Guid> ids, Action<Guid> change)
    {
        undo.BeginGroup();
        try { foreach (var id in ids) change(id); }
        finally { undo.EndGroup(); }
    }
}
