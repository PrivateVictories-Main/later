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
}
