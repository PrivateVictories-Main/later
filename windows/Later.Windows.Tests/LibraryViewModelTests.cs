using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Engine;
using Xunit;

namespace Later.Windows.Tests;

/// The window's state (the port of the Mac's `LibraryModel`), compiled into the tests from the app project.
public sealed class LibraryViewModelTests
{
    static readonly DateTime Noon = SampleLibrary.NoonUtc(new DateTime(2026, 10, 15));
    static readonly IReadOnlyDictionary<string, int> NoCopies = new Dictionary<string, int>();

    static LibraryData Sample() => SampleLibrary.Data(Noon);

    static List<string> Events(LibraryViewModel model)
    {
        var events = new List<string>();
        model.PropertyChanged += (_, e) => events.Add(e.PropertyName!);
        return events;
    }

    [Fact]
    public void ThereIsNoListUntilTheFirstSnapshotAndThenItsCountsAreThereEvenBeforeTheWindowShows()
    {
        var model = new LibraryViewModel(Noon);
        var events = Events(model);
        Assert.False(model.IsLoaded);
        Assert.Empty(model.Counts);
        model.SetSnapshot(Sample(), NoCopies);
        Assert.True(model.IsLoaded);
        Assert.Equal(LibraryQuery.Counts(Sample(), Noon)[LibrarySection.All], model.Counts[LibrarySection.All]);
        model.SetSnapshot(Sample(), NoCopies);
        Assert.Equal(1, events.Count(e => e == nameof(LibraryViewModel.IsLoaded)));   // said once
    }

    [Fact]
    public void ALibraryReallyWithoutPagesIsLoadedWithNoCounts()
    {
        var model = new LibraryViewModel(Noon);
        model.SetSnapshot(new LibraryData(), NoCopies);
        Assert.True(model.IsLoaded);   // loaded and empty is what the first-run welcome is for; not loaded is not
        Assert.Empty(model.Counts);
        Assert.Empty(model.Current.Items);
    }

    [Fact]
    public void WhileHiddenASnapshotChangesNothingDrawnAndShowingCatchesUp()
    {
        var model = new LibraryViewModel(Noon);
        model.Wake(Noon);
        model.SetSnapshot(Sample(), NoCopies);
        var counts = model.Counts; var current = model.Current;
        model.Hide();
        model.SetSnapshot(new LibraryData(), NoCopies);
        Assert.Same(counts, model.Counts);
        Assert.Same(current, model.Current);
        model.Wake(Noon);
        Assert.Empty(model.Counts);
        Assert.Empty(model.Current.Items);
    }

    [Fact]
    public void ShowingAtTheSameMomentAsTheSampleClockMovesNothing()
    {
        var model = new LibraryViewModel(Noon);
        model.SetSnapshot(Sample(), NoCopies);
        var counts = model.Counts; var current = model.Current;
        model.Wake(Noon);   // the first activation of a sample run
        Assert.Equal(Noon, model.Now);
        Assert.Same(counts, model.Counts);
        Assert.Same(current, model.Current);
    }

    [Fact]
    public void ShowingLaterTheSameDayKeepsTheClockAndAnotherDayMovesIt()
    {
        var model = new LibraryViewModel(Noon);
        model.SetSnapshot(Sample(), NoCopies);
        model.Wake(Noon.AddMinutes(1));
        Assert.Equal(Noon, model.Now);   // nothing would look different
        model.Wake(Noon.AddDays(3));
        Assert.Equal(Noon.AddDays(3), model.Now);   // dates, and what has gone to Older, differ
    }

    [Fact]
    public void ASearchNarrowsTheListingAndLeavesTheCounts()
    {
        var model = new LibraryViewModel(Noon);
        model.Wake(Noon);
        model.SetSnapshot(Sample(), NoCopies);
        int all = model.Counts[LibrarySection.All];
        int before = model.Current.Items.Count;
        model.SetSearch("  apple ");
        Assert.True(model.Searching);
        Assert.Equal("apple", model.Query);
        Assert.NotEmpty(model.Current.Items);
        Assert.True(model.Current.Items.Count < before);
        Assert.Equal(all, model.Counts[LibrarySection.All]);
        model.SetSearch("   ");
        Assert.False(model.Searching);
        Assert.Equal(before, model.Current.Items.Count);
    }

    [Fact]
    public void ASectionListsItsOwnPages()
    {
        var model = new LibraryViewModel(Noon);
        model.Wake(Noon);
        model.SetSnapshot(Sample(), NoCopies);
        model.Section = LibrarySection.Done;
        Assert.Equal(model.Counts[LibrarySection.Done], model.Current.Items.Count);
        Assert.All(model.Current.Items, i => Assert.Equal(ItemStatus.Done, i.Status));
    }

    static EngineState Watching(bool paused = false) =>
        new(new Dictionary<Browser, BrowserStatus> { [Browser.Helium] = BrowserStatus.Watching }, paused, null, true, true);

    [Fact]
    public void TheSameEngineStateInANewDictionaryIsNotAChange()
    {
        var model = new LibraryViewModel(Noon);
        model.SetEngineState(Watching());
        var events = Events(model);
        model.SetEngineState(Watching());   // the engine hands out a new dictionary each time
        Assert.Empty(events);
        model.SetEngineState(Watching(paused: true));
        Assert.Equal([nameof(LibraryViewModel.Engine)], events.ToArray());
    }

    // MARK: Selection, folding and the user's changes

    /// `data`: the list to show (every `Sample()` is a new list, with ids of its own).
    static LibraryViewModel Shown(LibraryData? data = null)
    {
        var model = new LibraryViewModel(Noon);
        model.Wake(Noon);
        model.SetSnapshot(data ?? Sample(), NoCopies);
        return model;
    }

    [Fact]
    public void ASectionOrASearchStartsWithNothingSelected()
    {
        var model = Shown();
        model.SetSelection(model.Current.RowIds.Take(2));
        Assert.Equal(2, model.Selected.Count);
        model.Section = LibrarySection.Done;
        Assert.Empty(model.Selected);
        model.SetSelection(model.Current.RowIds.Take(1));
        model.SetSearch("apple");
        Assert.Empty(model.Selected);
    }

    [Fact]
    public void TheSelectedPagesAreInTheListingsOrderWhateverOrderTheyWerePickedIn()
    {
        var model = Shown();
        var rows = model.Current.RowIds;
        model.SetSelection([rows[3], rows[1], rows[2]], rows[2]);
        Assert.Equal([rows[1], rows[2], rows[3]], model.SelectedItems.Select(i => i.Id).ToArray());
        Assert.Equal(rows[2], model.Cursor);
        model.SelectAll();
        Assert.Equal(rows.Count, model.Selected.Count);
        model.ClearSelection();
        Assert.Empty(model.SelectedItems);
    }

    [Fact]
    public void FoldingAHeadingTakesItsRowsOutOfTheKeyboardsWayAndUnfoldingBringsThemBack()
    {
        var model = Shown();
        int all = model.Current.RowIds.Count;
        var first = model.Current.Groups[0];
        Assert.False(model.IsFolded(first.Title));
        model.ToggleFold(first.Title);
        Assert.True(model.IsFolded(first.Title));
        Assert.Equal(all - first.Items.Count, model.Current.RowIds.Count);
        Assert.Equal(model.Counts[LibrarySection.All], model.Current.Items.Count);   // the pages are still listed
        model.ToggleFold(first.Title);
        Assert.Equal(all, model.Current.RowIds.Count);
    }

    [Fact]
    public void FoldingAHeadingDeselectsItsPagesSoNoActionCanTouchWhatTheListDoesNotShow()
    {
        var model = Shown();
        var first = model.Current.Groups[0];
        var elsewhere = model.Current.Groups[1].Items[0].Id;
        model.SetSelection([first.Items[0].Id, elsewhere], first.Items[0].Id);
        model.ToggleFold(first.Title);
        Assert.Equal([elsewhere], model.Selected.ToArray());   // only what the list still shows
        Assert.Equal([elsewhere], model.SelectedItems.Select(i => i.Id).ToArray());
        Assert.Equal(elsewhere, model.Cursor);
        model.ToggleFold(first.Title);
        Assert.Equal([elsewhere], model.Selected.ToArray());   // unfolding does not bring the selection back

        model.SetSelection([first.Items[0].Id]);
        model.ToggleFold(first.Title);
        Assert.Empty(model.Selected);
        Assert.Null(model.Cursor);
    }

    [Fact]
    public void ASearchFoldsUnderItsOwnKeysSoADateHeadingFoldedBeforeDoesNotHideAResult()
    {
        var model = Shown();
        model.ToggleFold("Saved");   // under the date grouping this is no heading at all
        model.SetSearch("apple");
        Assert.False(model.IsFolded("Saved"));
        Assert.NotEmpty(model.Current.RowIds);
    }

    [Fact]
    public void DeletingTheSelectedPageSelectsTheNextOnceTheChangeArrives()
    {
        var original = Sample();
        var model = Shown(original);
        var rows = model.Current.RowIds;
        var gone = rows[2];
        model.SetSelection([gone]);
        int token = model.BeginChange([gone]);
        var data = original.Copy();
        data.Items.RemoveAll(i => i.Id == gone);
        model.SetSnapshot(data, NoCopies);
        Assert.Empty(model.SelectedItems);   // the page is no longer listed; where to go on from is told by the change
        model.ChangeApplied(token, "Delete");
        Assert.Equal([rows[3]], model.Selected.ToArray());
        Assert.Equal(rows[3], model.Cursor);
        Assert.Equal("Delete", model.UndoName);
        Assert.Equal(1, model.UndoVersion);
    }

    [Fact]
    public void UndoAndRedoThemselvesOfferNothingAndTheOfferCanBeDismissed()
    {
        var model = Shown();
        model.ChangeApplied(0, "Delete");
        Assert.Null(model.UndoName);
        int token = model.BeginChange([model.Current.RowIds[0]]);
        model.ChangeApplied(token, "Mark Done");
        Assert.Equal("Mark Done", model.UndoName);
        model.ChangeApplied(0, "Delete");   // an Undo sent the list again: the offer stays as it was
        Assert.Equal("Mark Done", model.UndoName);
        model.ClearUndoOffer();
        Assert.Null(model.UndoName);
    }

    [Fact]
    public void TwoChangesOfAKindAreTwoOffers()
    {
        var model = Shown();
        model.ChangeApplied(model.BeginChange([model.Current.RowIds[0]]), "Mark Done");
        model.ChangeApplied(model.BeginChange([model.Current.RowIds[1]]), "Mark Done");
        Assert.Equal(2, model.UndoVersion);
    }

    [Fact]
    public void ThePagesShownAreTheOnlyOnesSelectedAfterTheListChanges()
    {
        var original = Sample();
        var model = Shown(original);
        var rows = model.Current.RowIds;
        model.SetSelection([rows[0], rows[1]]);
        var data = original.Copy();
        data.Items.RemoveAll(i => i.Id == rows[1]);
        model.SetSnapshot(data, NoCopies);
        Assert.Equal([rows[0]], model.Selected.ToArray());
    }

    [Fact]
    public void TheWhyLineUsesTheDaysTheSnapshotCameWith()
    {
        var library = SampleLibrary.Build(Noon);
        var model = new LibraryViewModel(Noon);
        model.Wake(Noon);
        model.SetSnapshot(library.Data, NoCopies, LibraryListing.VisitDays(library, Familiarity.Today(Noon)));
        var github = library.Data.Items.Single(i => i.Url == "https://github.com/");
        Assert.Equal("Regular · visited 24 days this month", model.Why(github));
        model.SetSnapshot(library.Data, NoCopies);   // a snapshot with no days keeps the last ones
        Assert.Equal("Regular · visited 24 days this month", model.Why(github));
    }

    [Fact]
    public void KindGroupingInsideOneKindFallsBackToDatesAndTabOrderHasNoGrouping()
    {
        var model = Shown();
        model.Grouping = LibraryGrouping.Kind;
        model.Section = LibrarySection.OfKind(Kind.School);
        Assert.Equal(LibraryGrouping.Date, model.EffectiveGrouping);
        Assert.True(model.GroupingActive);
        model.Sort = LibrarySort.TabOrder;
        Assert.False(model.GroupingActive);
    }
}
