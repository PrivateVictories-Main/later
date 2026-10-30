using Later.App.ViewModels;
using Later.Core;
using Xunit;

namespace Later.Windows.Tests;

/// What each action does to the library: undoable, and several pages are one Undo.
public sealed class ListEditingTests
{
    static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    readonly Library library = SampleLibrary.Build(Now);
    readonly UndoStack undo = new();

    IEnumerable<Item> Recent => library.Data.Items.Where(i => i.Status == ItemStatus.Active && i.Regular != true);

    [Fact]
    public void SeveralPagesDoneAtOnceAreOneUndo()
    {
        var ids = Recent.Take(3).Select(i => i.Id).ToList();
        ItemChanges.SetDone(library, undo, ids, true, Now);
        Assert.All(ids, id => Assert.Equal(ItemStatus.Done, library.Data.Items.Single(i => i.Id == id).Status));
        Assert.Equal("Mark Done", undo.UndoName);
        undo.Undo();
        Assert.All(ids, id => Assert.Equal(ItemStatus.Active, library.Data.Items.Single(i => i.Id == id).Status));
        Assert.False(undo.CanUndo);   // one step, not three
        undo.Redo();
        Assert.All(ids, id => Assert.Equal(ItemStatus.Done, library.Data.Items.Single(i => i.Id == id).Status));
    }

    [Fact]
    public void MoveBackIsItsOwnChangeAndKeepsTheFirstDoneDate()
    {
        var done = library.Data.Items.First(i => i.Status == ItemStatus.Done);
        ItemChanges.SetDone(library, undo, [done.Id], false, Now);
        Assert.Equal(ItemStatus.Active, library.Data.Items.Single(i => i.Id == done.Id).Status);
        Assert.Equal("Move Back", undo.UndoName);
        undo.Undo();
        Assert.Equal(done.DoneAt, library.Data.Items.Single(i => i.Id == done.Id).DoneAt);
    }

    [Fact]
    public void DeletingSeveralPagesIsOneUndoThatPutsEachBackWhereItWas()
    {
        var before = library.Data.Items.Select(i => i.Id).ToList();
        var ids = Recent.Skip(2).Take(2).Select(i => i.Id).ToList();
        ItemChanges.Delete(library, undo, ids, Now);
        Assert.Equal(before.Count - 2, library.Data.Items.Count);
        undo.Undo();
        Assert.Equal(before, library.Data.Items.Select(i => i.Id).ToList());
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void EmptyDoneDeletesExactlyWhatDoneShowsAsOneUndo()
    {
        // A regular that is finished is in Done like any other finished page.
        var github = library.Data.Items.Single(i => i.Url == "https://github.com/");
        ItemChanges.SetDone(library, undo, [github.Id], true, Now);
        var shown = LibraryQuery.Items(library.Data, LibrarySection.Done, "", Now).Select(i => i.Id).ToHashSet();
        Assert.Contains(github.Id, shown);
        var others = library.Data.Items.Where(i => !shown.Contains(i.Id)).Select(i => i.Id).ToList();
        ItemChanges.EmptyDone(library, undo, Now);
        Assert.Equal(others, library.Data.Items.Select(i => i.Id).ToList());   // everything Done showed went, and nothing else
        Assert.Equal("Empty Done", undo.UndoName);
        undo.Undo();
        Assert.Equal(shown, LibraryQuery.Items(library.Data, LibrarySection.Done, "", Now).Select(i => i.Id).ToHashSet());
        Assert.Equal("Mark Done", undo.UndoName);   // the one step before it is still there: Undo did not reach past Empty Done
    }

    [Fact]
    public void KeepPagesLikeThisMovesARegularBackAndUndoBringsTheChoiceBack()
    {
        var github = library.Data.Items.Single(i => i.Url == "https://github.com/");
        Assert.True(github.IsActiveRegular);
        ItemChanges.KeepInList(library, undo, [github.Id]);
        var kept = library.Data.Items.Single(i => i.Id == github.Id);
        Assert.Equal(false, kept.Regular);
        Assert.True(library.Data.Corrections[github.PageType].IsSpecific);
        Assert.Contains(LibraryQuery.Items(library.Data, LibrarySection.All, "", Now), i => i.Id == github.Id);
        undo.Undo();
        Assert.True(library.Data.Items.Single(i => i.Id == github.Id).IsActiveRegular);
        Assert.False(library.Data.Corrections.ContainsKey(github.PageType));
    }

    [Fact]
    public void ThisIsARegularLeavesAKeptPlaceToTheRegularsAndUndoGivesItBack()
    {
        var tiny = library.Data.Items.Single(i => i.Host == "tinytools.example.net");
        Assert.Equal(false, tiny.Regular);
        ItemChanges.MarkRegular(library, undo, [tiny.Id]);
        Assert.True(library.Data.Items.Single(i => i.Id == tiny.Id).IsActiveRegular);
        Assert.Equal(true, library.Data.Corrections[tiny.PageType].Regular);
        Assert.Equal("Mark as Regular", undo.UndoName);
        undo.Undo();
        Assert.Equal(false, library.Data.Items.Single(i => i.Id == tiny.Id).Regular);
        Assert.False(library.Data.Corrections.ContainsKey(tiny.PageType));
    }

    [Fact]
    public void MovingTwoPagesOfOneTypeToAKindIsOneUndo()
    {
        var a = Recent.First(i => i.Kind == Kind.School);
        var same = Recent.Where(i => i.PageType == a.PageType).Select(i => i.Id).ToList();
        var kinds = library.Data.Items.ToDictionary(i => i.Id, i => i.Kind);
        ItemChanges.Move(library, undo, same, Kind.Tools);
        Assert.All(same, id => Assert.Equal(Kind.Tools, library.Data.Items.Single(i => i.Id == id).Kind));
        undo.Undo();
        Assert.Equal(kinds, library.Data.Items.ToDictionary(i => i.Id, i => i.Kind));
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void NotAnIdeaRemovesThePagesAndUndoPutsThemBack()
    {
        var before = library.Data.Items.Select(i => i.Id).ToList();
        var ids = Recent.Take(2).Select(i => i.Id).ToList();
        ItemChanges.NotAnIdea(library, undo, ids);
        Assert.All(ids, id => Assert.DoesNotContain(library.Data.Items, i => i.Id == id));
        undo.Undo();
        Assert.Equal(before, library.Data.Items.Select(i => i.Id).ToList());
    }

    [Fact]
    public void NothingChangesForAPageThatIsNoLongerThere()
    {
        ItemChanges.Delete(library, undo, [Guid.NewGuid()], Now);
        Assert.False(undo.CanUndo);
    }
}
