using System.Globalization;
using Later.App.ViewModels;
using Later.Core;
using Xunit;

namespace Later.Windows.Tests;

/// What the right pane says and offers, from the selection (the Mac's InspectorView, with its 1.4 "why" line and corrections).
public sealed class InspectorModelTests
{
    static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo EnUs = new("en-US");

    readonly Library library = SampleLibrary.Build(Now);

    Listing Listing(LibrarySection section, string search = "") =>
        LibraryListing.Build(library.Data, SampleLibrary.OpenCopies(library), library.PageKey, section, search, Now, LibraryGrouping.Date, LibrarySort.LastSeen, false, new HashSet<string>(), Utc, EnUs);

    InspectorModel For(IReadOnlyList<Item> selected, LibrarySection? section = null, string search = "", bool canEdit = true, string? openIn = "Helium", Listing? listing = null)
    {
        var days = LibraryListing.VisitDays(library, Familiarity.Today(Now));
        var s = section ?? LibrarySection.All;
        return InspectorModel.For(listing ?? Listing(s, search), selected, search.Trim().Length > 0, search.Trim(), s, canEdit, Now, i => LibraryListing.Why(i, library.Data, days), openIn, Utc, EnUs);
    }

    Item Page(Func<Item, bool> which) => library.Data.Items.First(which);

    // MARK: Nothing selected

    [Fact]
    public void WithNothingSelectedTheOverviewListsTheKindsAndTheSitesWithManyPages()
    {
        var view = For([]);
        Assert.Equal(InspectorMode.Overview, view.Mode);
        Assert.Equal("Select a page to see where it came from and what you can do with it.", view.Caption);
        Assert.Equal(22, view.Kinds.Sum(k => k.Count));
        Assert.Equal(KindInfo.All.Where(k => view.Kinds.Any(c => c.Kind == k)), view.Kinds.Select(k => k.Kind));   // only kinds with pages, in order
        Assert.Equal("Tools, 4", view.Kinds.Single(k => k.Kind == Kind.Tools).Name);
        Assert.True(view.Navigates);
        Assert.Equal("learn.northgate.edu", view.Sites[0].Host);   // five pages on one site
        Assert.Equal(5, view.Sites[0].Count);
        Assert.Equal("learn.northgate.edu, 5", view.Sites[0].Name);
        Assert.True(view.Sites.Count <= 6);
    }

    [Fact]
    public void NoBreakdownInsideOneKindAndNoSitesUntilOneHasSeveralPages()
    {
        Assert.Empty(For([], LibrarySection.OfKind(Kind.Shopping)).Kinds);
        var searching = For([], LibrarySection.OfKind(Kind.Shopping), "a");
        Assert.NotEmpty(searching.Kinds);   // a search mixes kinds
        Assert.False(searching.Navigates);   // and only describes its results
        Assert.Empty(For([], LibrarySection.Done).Sites);   // three done pages, three sites
    }

    [Fact]
    public void AnEmptySearchSaysNothingMatches()
    {
        var view = For([], search: "zzzzzz");
        Assert.Equal("Nothing matches “zzzzzz”.", view.Caption);
        Assert.Empty(view.Kinds);
        Assert.Empty(view.Sites);
    }

    // MARK: One page

    [Fact]
    public void OnePageHasItsFactsItsWhyLineAndTheSixActions()
    {
        var rei = Page(i => i.Host == "rei.com");
        var view = For([rei]);
        Assert.Equal(InspectorMode.Page, view.Mode);
        Assert.Equal(rei.Title, view.Title);
        Assert.Equal("rei.com/product/229153/patagonia-nano-puff-jacket-mens", view.Link);
        Assert.Equal("New site", view.Why);
        Assert.Equal("Open in Helium", view.OpenLabel);
        Assert.Equal("Copy Link", view.CopyLabel);
        Assert.Equal("Mark Done", view.DoneLabel);
        Assert.True(view.ShowsDone);
        Assert.Equal("Not an Idea", view.NotAnIdeaLabel);
        Assert.Equal("Delete", view.DeleteLabel);
        Assert.Null(view.CorrectionLabel);   // a page that is a thing has no regular choice
        Assert.Equal("Kind", view.Facts[0].Label);
        Assert.Equal("Shopping", view.Facts[0].Value);
        Assert.Equal(["Kind", "Site"], view.Facts.Take(2).Select(f => f.Label).ToArray());
        Assert.Equal(1, view.Facts.Count(f => f.Label == "Last seen"));
        Assert.Equal("From", view.Facts[^1].Label);
        Assert.Equal("Helium", view.Facts[^1].Value);
    }

    [Fact]
    public void WithNoBrowserFoundOpenSaysOnlyOpen() => Assert.Equal("Open", For([Page(i => i.Host == "rei.com")], openIn: null).OpenLabel);

    [Fact]
    public void FirstSeenReplacesOpenedWhereTheHistoryCannotSayAndSavedAppearsOnlyWhenItDiffers()
    {
        var plain = Page(i => i.OpenedAt is null);
        var facts = For([plain]).Facts;
        Assert.Contains(facts, f => f.Label == "First seen");
        Assert.DoesNotContain(facts, f => f.Label is "Opened" or "Saved");
        var opened = plain with { OpenedAt = plain.FirstSaved.AddDays(-9) };
        facts = For([opened]).Facts;
        Assert.Contains(facts, f => f.Label == "Opened");
        Assert.Contains(facts, f => f.Label == "Saved");
        facts = For([plain with { OpenedAt = plain.FirstSaved.AddSeconds(-30) }]).Facts;
        Assert.DoesNotContain(facts, f => f.Label == "Saved");   // within a minute of the save
    }

    [Fact]
    public void VisitsAreOnceOrNTimesAndOpenNowCountsTabs()
    {
        var once = Page(i => i.TotalVisits == 1);
        Assert.Equal("Once", For([once]).Facts.Single(f => f.Label == "Visits").Value);
        var many = Page(i => i.TotalVisits > 1);
        Assert.Equal($"{many.TotalVisits} times", For([many]).Facts.Single(f => f.Label == "Visits").Value);
        var twice = Page(i => i.Host == "linkvault.example.net");
        Assert.Equal("In 2 tabs", For([twice]).Facts.Single(f => f.Label == "Open now").Value);
    }

    [Fact]
    public void AFinishedPageHasItsDoneDateAndMovesBack()
    {
        var done = Page(i => i.Status == ItemStatus.Done);
        var view = For([done], LibrarySection.Done);
        Assert.Equal("Move Back", view.DoneLabel);
        Assert.True(view.AllDone);
        Assert.Contains(view.Facts, f => f.Label == "Done");
    }

    [Fact]
    public void ARegularOffersKeepPagesLikeThisInsteadOfDone()
    {
        var github = Page(i => i.Url == "https://github.com/");
        var view = For([github], LibrarySection.Regulars);
        Assert.Equal("Regular · visited 24 days this month", view.Why);
        Assert.Equal("Keep Pages Like This", view.CorrectionLabel);
        Assert.True(view.CorrectionKeeps);
        Assert.False(view.ShowsDone);   // the regular's own choice stands in for it
    }

    [Fact]
    public void APlaceThatIsKeptOffersThisIsARegular()
    {
        var tiny = Page(i => i.Host == "tinytools.example.net");
        Assert.Equal(false, tiny.Regular);
        var view = For([tiny]);
        Assert.Equal("This Is a Regular", view.CorrectionLabel);
        Assert.False(view.CorrectionKeeps);
        Assert.True(view.ShowsDone);
        Assert.Equal("New site", view.Why);
    }

    [Fact]
    public void ARegularThatIsDoneGetsMoveBackAndStillItsCorrection()
    {
        var github = Page(i => i.Url == "https://github.com/");
        var done = github with { Status = ItemStatus.Done, DoneAt = Now };
        var view = For([done], LibrarySection.Done);
        Assert.True(view.ShowsDone);
        Assert.Equal("Move Back", view.DoneLabel);   // Done shows every finished page, so Move Back is its way out
        Assert.Equal("Keep Pages Like This", view.CorrectionLabel);
    }

    [Fact]
    public void ReadOnlyTurnsEveryChangeOff() => Assert.False(For([Page(i => i.Host == "rei.com")], canEdit: false).CanEdit);

    // MARK: Several pages

    [Fact]
    public void SeveralPagesAreOneSummaryWithTheSameActionsForAll()
    {
        var picked = library.Data.Items.Where(i => i.Host is "rei.com" or "github.com" or "learn.northgate.edu").Take(3).ToList();
        var view = For(picked);
        Assert.Equal(InspectorMode.Pages, view.Mode);
        Assert.Equal("3 Pages", view.Heading);
        Assert.Equal(picked.Select(i => i.Host).Distinct().Count() == 1 ? "From 1 site" : $"From {picked.Select(i => i.Host).Distinct().Count()} sites", view.SiteSummary);
        Assert.Equal("Open 3 Pages", view.OpenLabel);
        Assert.Equal("Copy Links", view.CopyLabel);
        Assert.Equal("Not Ideas", view.NotAnIdeaLabel);
        Assert.Equal("Delete 3", view.DeleteLabel);
        Assert.Equal("Mark Done", view.DoneLabel);
        Assert.True(view.ShowsDone);
        Assert.Null(view.CorrectionLabel);
    }

    [Fact]
    public void SeveralFinishedPagesMoveBackTogether()
    {
        var done = library.Data.Items.Where(i => i.Status == ItemStatus.Done).ToList();
        var view = For(done, LibrarySection.Done);
        Assert.Equal("Move Back", view.DoneLabel);
        Assert.Equal($"{done.Count} Pages", view.Heading);
    }
}
