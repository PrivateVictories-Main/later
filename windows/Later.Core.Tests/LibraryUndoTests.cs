using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

/// The list's Undo and Redo. Each `Step` is one group: a single Undo, like one turn of the run loop in the Mac app.
public class LibraryUndoTests
{
    readonly UndoStack undo = new();
    static readonly Verdict Product = new(true, Kind.Shopping, "a.com/p/*");

    void Step(Action body)
    {
        undo.BeginGroup();
        body();
        undo.EndGroup();
    }

    /// Desk and Lamp share a page type; Other doesn't. Lamp is done.
    static Library MakeLibrary()
    {
        var lib = new Library();
        lib.Save("https://a.com/p/1", "Desk", Product, Browser.Helium, T0);
        lib.Save("https://b.com/x", "Other", new Verdict(true, Kind.Reading, "b.com/*"), Browser.Helium, T0);
        lib.Save("https://a.com/p/2", "Lamp", Product, Browser.Helium, T0);
        lib.SetDone(lib.Data.Items[2].Id, true, T0);
        return lib;
    }

    static Guid Id(Library lib, string title) => lib.Data.Items.First(i => i.Title == title).Id;

    [Fact]
    public void DeleteUndoesAndRedoes()
    {
        var lib = MakeLibrary(); var before = lib.CopyData();
        Step(() => lib.Delete(Id(lib, "Desk"), T0, undo));
        var after = lib.CopyData();
        Assert.Equal("Delete", undo.UndoName);
        undo.Undo();
        AssertSame(before, lib.Data);
        undo.Redo();
        AssertSame(after, lib.Data);
    }

    /// Both pages of one type in one batch, after an earlier correction that must survive.
    [Fact]
    public void NotAnIdeaUndoesExactlyEvenForPagesOfOneType()
    {
        var lib = MakeLibrary();
        lib.Move(Id(lib, "Desk"), Kind.School);
        var before = lib.CopyData();
        Step(() => { foreach (var id in new[] { Id(lib, "Lamp"), Id(lib, "Desk") }) lib.NotAnIdea(id, undo); });
        Assert.Equal(new List<string> { "Other" }, lib.Data.Items.Select(i => i.Title).ToList());
        Assert.Equal(new Correction(IsSpecific: false, Kind: Kind.School), lib.Data.Corrections["a.com/p/*"]);
        var after = lib.CopyData();
        undo.Undo();
        AssertSame(before, lib.Data);
        undo.Redo();
        AssertSame(after, lib.Data);
    }

    /// Move changes every page of the type, so Undo puts back each page's own kind and the type's correction.
    [Fact]
    public void MoveUndoesEveryPageOfTheType()
    {
        var lib = MakeLibrary();
        lib.SetKinds(new Dictionary<Guid, Kind> { [Id(lib, "Lamp")] = Kind.Reading });   // the two pages of the type start on different kinds
        var before = lib.CopyData();
        Step(() => lib.Move(Id(lib, "Desk"), Kind.School, undo));
        Assert.Equal(new List<Kind> { Kind.School, Kind.Reading, Kind.School }, lib.Data.Items.Select(i => i.Kind).ToList());
        Assert.Equal("Move to School", undo.UndoName);
        var after = lib.CopyData();
        undo.Undo();
        AssertSame(before, lib.Data);
        Assert.False(lib.Data.Corrections.ContainsKey("a.com/p/*"));
        undo.Redo();
        AssertSame(after, lib.Data);
    }

    [Fact]
    public void EmptyDoneIsOneUndo()
    {
        var lib = MakeLibrary();
        lib.SetDone(Id(lib, "Desk"), true, T0);
        var before = lib.CopyData();
        Step(() => lib.EmptyDone(T0, undo));
        Assert.Equal(new List<string> { "Other" }, lib.Data.Items.Select(i => i.Title).ToList());
        Assert.True(lib.IsDismissed("https://a.com/p/1"));
        var after = lib.CopyData();
        undo.Undo();
        AssertSame(before, lib.Data);
        Assert.False(lib.IsDismissed("https://a.com/p/1"));
        Assert.False(undo.CanUndo);
        undo.Redo();
        AssertSame(after, lib.Data);
    }

    /// Redo of Mark Done keeps the completion date it first had; Undo of Move Back puts back the original one.
    [Fact]
    public void DoneDatesSurviveUndoAndRedo()
    {
        var lib = MakeLibrary();
        var desk = Id(lib, "Desk"); var lamp = Id(lib, "Lamp"); var t1 = T0.AddSeconds(3600);
        Step(() => lib.SetDone(desk, true, t1, undo));
        undo.Undo();
        Assert.True(lib.Data.Items[0].Status == ItemStatus.Active && lib.Data.Items[0].DoneAt is null);
        undo.Redo();
        Assert.Equal(t1, lib.Data.Items[0].DoneAt);
        Step(() => lib.SetDone(lamp, false, t1.AddSeconds(60), undo));
        Assert.Equal("Move Back", undo.UndoName);
        undo.Undo();
        Assert.Equal(T0, lib.Data.Items[2].DoneAt);
    }

    [Fact]
    public void ANewChangeClearsRedoAndNamesFollowTheStacks()
    {
        var lib = MakeLibrary();
        Step(() => lib.Delete(Id(lib, "Desk"), T0, undo));
        undo.Undo();
        Assert.True(undo.CanRedo);
        Assert.Equal("Delete", undo.RedoName);
        Step(() => lib.SetDone(Id(lib, "Desk"), true, T0, undo));
        Assert.False(undo.CanRedo);
        Assert.Equal("Mark Done", undo.UndoName);
    }
}
