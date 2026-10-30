using System.Globalization;
using Later.App.ViewModels;
using Later.Core;
using Xunit;

namespace Later.Windows.Tests;

/// The list as headings and rows, and the words each says, at a fixed clock, zone and culture.
public sealed class RowsTests
{
    static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo EnUs = new("en-US");

    readonly Library library = SampleLibrary.Build(Now);

    Listing Listing(LibrarySection section, string search = "", LibraryGrouping grouping = LibraryGrouping.Date, LibrarySort sort = LibrarySort.LastSeen, bool open = false) =>
        LibraryListing.Build(library.Data, open ? SampleLibrary.OpenCopies(library) : new Dictionary<string, int>(), library.PageKey, section, search, Now, grouping, sort, false,
                             new HashSet<string>(), Utc, EnUs);

    static List<object> Flat(Listing listing, LibrarySection? section = null, bool searching = false, LibraryGrouping grouping = LibraryGrouping.Date, bool groupingActive = true,
                      LibrarySort sort = LibrarySort.LastSeen, Func<string, bool>? folded = null) =>
        ListRows.Build(listing, section ?? LibrarySection.All, searching, grouping, groupingActive, sort, Now, folded ?? (_ => false), Utc, EnUs);

    [Fact]
    public void EachHeadingIsFollowedByItsRowsInOrder()
    {
        var listing = Listing(LibrarySection.All);
        var flat = Flat(listing);
        Assert.Equal(listing.Groups.Count + listing.Items.Count, flat.Count);
        Assert.Equal(listing.Groups.Select(g => g.Title), flat.OfType<GroupHeaderRow>().Select(h => h.Title));
        Assert.Equal(listing.Items.Select(i => i.Id), flat.OfType<RowModel>().Select(r => r.Id));
        Assert.IsType<GroupHeaderRow>(flat[0]);
        Assert.Equal("Today, 4 pages", flat[0].ToString());
    }

    [Fact]
    public void AFoldedGroupGivesOnlyItsHeadingWhichSaysWhatPressingItDoes()
    {
        var listing = Listing(LibrarySection.All);
        var flat = Flat(listing, folded: t => t == "Yesterday");
        var yesterday = flat.OfType<GroupHeaderRow>().Single(h => h.Title == "Yesterday");
        Assert.True(yesterday.Folded);
        Assert.Equal("Expand Yesterday", yesterday.ActionName);
        Assert.Equal("", yesterday.Chevron);
        Assert.Equal(listing.Items.Count - yesterday.Count, flat.OfType<RowModel>().Count());   // the pages stay in the listing; only their rows go
        var today = flat.OfType<GroupHeaderRow>().First();
        Assert.Equal("Collapse Today", today.ActionName);
        Assert.Equal("", today.Chevron);
    }

    [Fact]
    public void ARowIsNamedByTitleSiteKindAndWhenSavedAndSaysWhenOpenOrDone()
    {
        var listing = Listing(LibrarySection.All, open: true);
        var rows = Flat(listing).OfType<RowModel>().ToList();
        var rei = rows.Single(r => r.Site == "rei.com");
        var item = listing.Items.Single(i => i.Id == rei.Id);
        Assert.Equal($"Patagonia Nano Puff Jacket - Men's | REI Co-op, rei.com, Shopping, saved {LibraryQuery.ShortDate(item.FirstSaved, Now, Utc, EnUs)}"
                     + string.Concat(new[] { rei.Hint, rei.OpenedText }.Where(t => t.Length > 0).Distinct().Select(t => ", " + t)) + (rei.IsOpen ? ", open now" : ""), rei.Name);
        Assert.Equal(rei.Name, rei.ToString());
        var twice = rows.Single(r => r.Site == "linkvault.example.net");
        Assert.Equal(2, twice.Copies);
        Assert.EndsWith(", open now", twice.Name, StringComparison.Ordinal);
        Assert.Equal("2 open", twice.OpenLabel);
        var done = Flat(Listing(LibrarySection.Done), LibrarySection.Done).OfType<RowModel>().ToList();
        Assert.NotEmpty(done);
        Assert.All(done, r => { Assert.True(r.Done); Assert.EndsWith(", done", r.Name, StringComparison.Ordinal); });
    }

    [Fact]
    public void ARowWithNoTitleIsNamedByItsLink()
    {
        var item = new Item(Guid.NewGuid(), "https://a.com/x", "", "a.com", Kind.Reading, "a.com/*", Browser.Helium, Now, Now, 1, ItemStatus.Active);
        var row = ListRows.Row(item, 0, null, LibrarySort.LastSeen, Now, true, true, Utc, EnUs);
        Assert.Equal("https://a.com/x", row.Title);
    }

    [Fact]
    public void TheKindAndTheSiteShowOnlyWhereNothingElseSaysThem()
    {
        bool KindShown(List<object> rows) => rows.OfType<RowModel>().All(r => r.ShowsKind);
        Assert.True(KindShown(Flat(Listing(LibrarySection.All))));
        Assert.DoesNotContain(Flat(Listing(LibrarySection.OfKind(Kind.School)), LibrarySection.OfKind(Kind.School)).OfType<RowModel>(), r => r.ShowsKind);   // a kind's own section
        Assert.True(KindShown(Flat(Listing(LibrarySection.OfKind(Kind.School), "a"), LibrarySection.OfKind(Kind.School), searching: true)));   // a search mixes kinds
        Assert.DoesNotContain(Flat(Listing(LibrarySection.All), grouping: LibraryGrouping.Kind).OfType<RowModel>(), r => r.ShowsKind);   // the kind heading says it
        var bySite = Flat(Listing(LibrarySection.All, grouping: LibraryGrouping.Site), grouping: LibraryGrouping.Site);
        Assert.DoesNotContain(bySite.OfType<RowModel>(), r => r.ShowsHost);
        Assert.All(bySite.OfType<GroupHeaderRow>(), h => { Assert.Equal(HeaderStyle.Site, h.Style); Assert.True(h.IsSite); });
        Assert.True(Flat(Listing(LibrarySection.All)).OfType<RowModel>().All(r => r.ShowsHost));
    }

    [Fact]
    public void KindHeadingsCarryTheirKind()
    {
        var byKind = Flat(Listing(LibrarySection.All, grouping: LibraryGrouping.Kind), grouping: LibraryGrouping.Kind).OfType<GroupHeaderRow>().ToList();
        Assert.All(byKind, h => { Assert.True(h.IsKind); Assert.False(h.ShowsTitle); Assert.NotNull(h.Kind); });
        Assert.Equal(KindInfo.All.Where(k => byKind.Any(h => h.Kind == k)), byKind.Select(h => h.KindValue));   // in Kind's own order
    }

    [Fact]
    public void TheRowsSecondButtonDependsOnWhereThePageIs()
    {
        var all = Listing(LibrarySection.All);
        Assert.All(Flat(all).OfType<RowModel>(), r => { Assert.True(r.ShowsMarkDone); Assert.False(r.ShowsMoveBack || r.Keeps); });
        var regulars = Flat(Listing(LibrarySection.Regulars), LibrarySection.Regulars).OfType<RowModel>().ToList();
        Assert.NotEmpty(regulars);
        Assert.All(regulars, r => { Assert.True(r.Keeps); Assert.False(r.ShowsMarkDone || r.ShowsMoveBack); });   // Keep Pages Like This, not Done
        var done = Flat(Listing(LibrarySection.Done), LibrarySection.Done).OfType<RowModel>();
        Assert.All(done, r => { Assert.True(r.ShowsMoveBack); Assert.False(r.ShowsMarkDone || r.Keeps); });
    }

    [Fact]
    public void ARegularThatIsDoneOffersMoveBack()
    {
        var github = library.Data.Items.Single(i => i.Url == "https://github.com/");
        var done = github with { Status = ItemStatus.Done, DoneAt = Now };
        var row = ListRows.Row(done, 0, null, LibrarySort.LastSeen, Now, true, true, Utc, EnUs);
        Assert.True(row.ShowsMoveBack);
        Assert.False(row.Keeps);
    }

    [Fact]
    public void TheDateAtTheRightFollowsTheOrderAndTheVisitsAreCounted()
    {
        var item = library.Data.Items.First(i => i.TotalVisits > 1);
        var row = ListRows.Row(item, 0, null, LibrarySort.LastSeen, Now, true, true, Utc, EnUs);
        Assert.Equal(LibraryQuery.ShortDate(LibraryQuery.SortDate(item), Now, Utc, EnUs), row.When);
        Assert.Equal($"{item.TotalVisits} visits", row.Visits);
        var byOpened = ListRows.Row(item, 0, null, LibrarySort.DateOpened, Now, true, true, Utc, EnUs);
        Assert.Equal(LibraryQuery.ShortDate(item.Opened, Now, Utc, EnUs), byOpened.When);
    }

    [Fact]
    public void TheSecondLineJoinsSiteAndHintAndLeavesOutTheSiteUnderASiteHeading()
    {
        var item = library.Data.Items.First(i => i.Host == "rei.com");
        Assert.Equal("rei.com · Opened Thu 3:04 PM", ListRows.Row(item, 0, "Opened Thu 3:04 PM", LibrarySort.LastSeen, Now, true, true, Utc, EnUs).SiteLine);
        Assert.Equal("Opened Thu 3:04 PM", ListRows.Row(item, 0, "Opened Thu 3:04 PM", LibrarySort.LastSeen, Now, true, false, Utc, EnUs).SiteLine);
        Assert.Equal("rei.com", ListRows.Row(item, 0, null, LibrarySort.LastSeen, Now, true, true, Utc, EnUs).SiteLine);
        var row = ListRows.Row(item, 0, null, LibrarySort.LastSeen, Now, true, true, Utc, EnUs);
        Assert.Equal(string.Join(" · ", new[] { row.SiteLine, row.OpenedText, row.Visits }.Where(t => t.Length > 0)), row.Meta);
        Assert.StartsWith("rei.com", row.Meta, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowNameCarriesTheHintAndWhenItWasOpenedSoLookAlikesDiffer()
    {
        var item = library.Data.Items.First(i => i.Host == "rei.com");
        var plain = ListRows.Row(item, 0, null, LibrarySort.LastSeen, Now, true, true, Utc, EnUs);
        var hinted = ListRows.Row(item, 0, "Opened Thu 3:04 PM", LibrarySort.LastSeen, Now, true, true, Utc, EnUs);
        Assert.Contains(", Opened Thu 3:04 PM", hinted.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("Opened Thu", plain.Name, StringComparison.Ordinal);
        Assert.NotEqual(plain.Name, hinted.Name);
        var open = ListRows.Row(item, 2, "Opened Thu 3:04 PM", LibrarySort.LastSeen, Now, true, true, Utc, EnUs);
        Assert.EndsWith(", open now", open.Name, StringComparison.Ordinal);   // the flags stay last
    }

    [Fact]
    public void AHeadingWithOnePageSaysOnePage() =>
        Assert.Equal("rei.com, 1 page", new GroupHeaderRow("rei.com", 1, false, HeaderStyle.Site, null).Name);
}
