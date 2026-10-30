using Xunit;

namespace Later.Core.Tests;

/// The numbers are read off the Mac's `Sample.swift` table (33 pages, 3 of them done, 5 places of which 4 are regulars) at a fixed clock.
public class SampleLibraryTests
{
    static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    readonly LibraryData data = SampleLibrary.Data(Now);

    Item At(string urlPart) => data.Items.Single(i => i.Url.Contains(urlPart, StringComparison.Ordinal));
    Item Exact(string url) => data.Items.Single(i => i.Url == url);

    [Fact]
    public void ThirtyThreePagesAndFivePlacesAreSaved()
    {
        Assert.Equal(38, data.Items.Count);
        Assert.Equal(33, data.Items.Count(i => i.Regular is null));   // the pages that are things
        Assert.Equal(5, data.Items.Count(i => i.Regular is not null));   // the places
    }

    [Fact]
    public void TheSidebarCountsAreTheMacsOwn()
    {
        var counts = LibraryQuery.Counts(data, Now);
        Assert.Equal(22, counts[LibrarySection.All]);
        Assert.Equal(new Dictionary<Kind, int>
        {
            [Kind.Shopping] = 2, [Kind.Posts] = 3, [Kind.School] = 8, [Kind.Reading] = 2, [Kind.Videos] = 2, [Kind.Tools] = 4, [Kind.Other] = 1,
        }, KindInfo.All.ToDictionary(k => k, k => counts[LibrarySection.OfKind(k)]));
        Assert.Equal(9, counts[LibrarySection.Older]);
        Assert.Equal(3, counts[LibrarySection.Done]);
        Assert.Equal(4, counts[LibrarySection.Regulars]);
        Assert.Equal(38, counts[LibrarySection.All] + counts[LibrarySection.Older] + counts[LibrarySection.Done] + counts[LibrarySection.Regulars]);
    }

    [Fact]
    public void TheSampleClockIsNoonLocalSoTheCountsDoNotDependOnTheHourItIsRunAt()
    {
        var noon = SampleLibrary.NoonUtc(new DateTime(2026, 10, 29, 23, 59, 0));   // the time of day given is ignored
        var local = TimeZoneInfo.ConvertTimeFromUtc(noon, TimeZoneInfo.Local);
        Assert.Equal(new DateTime(2026, 10, 29, 12, 0, 0), local);
        var counts = LibraryQuery.Counts(SampleLibrary.Data(noon), noon);
        Assert.Equal((22, 4, 4, 9, 3), (counts[LibrarySection.All], counts[LibrarySection.Regulars], counts[LibrarySection.OfKind(Kind.Tools)], counts[LibrarySection.Older], counts[LibrarySection.Done]));
    }

    [Fact]
    public void ThreePagesAreDoneAndTheirDoneTimeFollowsTheVisit()
    {
        var done = data.Items.Where(i => i.Status == ItemStatus.Done).ToList();
        Assert.Equal(3, done.Count);
        Assert.All(done, i => Assert.NotNull(i.DoneAt));
        Assert.Contains(done, i => i.Url.Contains("Byzantine", StringComparison.Ordinal));
    }

    [Fact]
    public void VisitsAreTheTablesAndHistoryAddsToMostPages()
    {
        Assert.Equal(3, At("patagonia").Visits);
        Assert.Equal(9, At("patagonia").TotalVisits);   // row 0: history says 3 + 6
        Assert.NotNull(At("patagonia").OpenedAt);
        Assert.NotNull(At("aircAruvnKk").OpenedAt);   // row 2 has history
        Assert.Null(At("item?id=39223456").OpenedAt);   // every fourth row (3, 7, 11 ...) has none
    }

    [Fact]
    public void OnlyThePagesOpenInTabsHaveAPlaceAndTheyFillTwoWindowsFromZero()
    {
        var placed = data.Items.Where(i => i.TabIndex is not null).ToList();
        Assert.Equal(21, placed.Count);
        Assert.All(data.Items.Where(i => i.Regular is not null), i => Assert.Null(i.TabIndex));
        Assert.All(placed.Where(i => i.Host == "learn.northgate.edu"), i => Assert.Equal(1, i.WindowRank));
        Assert.Equal(Enumerable.Range(0, 11), placed.Where(i => i.WindowRank == 0).Select(i => i.TabIndex!.Value).Order());
        Assert.Equal(Enumerable.Range(0, 10), placed.Where(i => i.WindowRank == 1).Select(i => i.TabIndex!.Value).Order());
        Assert.Equal(1, At("bsky.app").WindowRank);   // row 22: placed (even), and 22 % 6 is 4
    }

    [Fact]
    public void FourOfTheFivePlacesAreRegularsAndTheNewSiteIsKept()
    {
        Assert.True(Exact("https://github.com/").Regular);
        Assert.True(Exact("https://news.ycombinator.com/").Regular);
        Assert.True(Exact("https://www.reddit.com/").Regular);
        Assert.True(Exact("https://www.theatlantic.com/").Regular);
        Assert.False(At("tinytools.example.net").Regular);
    }

    [Fact]
    public void OpenCopiesHasOneEntryPerPlacedPageAndTwoForTheLinkVault()
    {
        var library = new Library(data);
        var copies = SampleLibrary.OpenCopies(library);
        Assert.Equal(21, copies.Count);
        Assert.Equal(2, copies[library.PageKey("https://linkvault.example.net/video")]);
        Assert.Equal(20, copies.Values.Count(n => n == 1));
    }
}
