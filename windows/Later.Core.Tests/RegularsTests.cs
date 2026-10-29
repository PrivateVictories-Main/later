using System.Text;
using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

/// The Mac's Regulars tests that apply on Windows. There, both browsers' History is read, so the days come from
/// `SetHistoryDays`; what a Mac file noted itself (`SiteDays`) is only kept and merged, never written by Windows.
public class RegularsTests
{
    static int Today => Familiarity.Today(T0);
    static readonly Verdict Place = new(true, Kind.Tools, "dash.example.com/~", IsPlace: true);
    static readonly Verdict Thing = new(true, Kind.Reading, "dash.example.com/posts/*");
    static readonly Verdict OldPlace = new(true, Kind.Tools, "dash.example.com/~");   // as 1.3 saved it, no flag
    const string Home = "https://dash.example.com/";
    const string Post = "https://dash.example.com/posts/how-we-found-the-bug";

    /// A library where dash.example.com was visited on `count` different days before today.
    static Library VisitedOn(int count)
    {
        var lib = new Library();
        lib.SetHistoryDays(new() { ["dash.example.com"] = Enumerable.Range(1, count).Select(d => Today - d).ToHashSet() });
        return lib;
    }

    static List<bool?> Flags(Library lib) => lib.Data.Items.Select(i => i.Regular).ToList();

    static void Note(Library lib, string site, params int[] days) => lib.Data.SiteDays[Library.Hash(site)] = [.. days];

    // MARK: Days

    [Fact]
    public void HistoryDaysAndANotedFilesDaysAreMergedPerSite()
    {
        var lib = new Library();
        lib.SetHistoryDays(new() { ["dash.example.com"] = [Today - 1, Today - 2] });
        Note(lib, "dash.example.com", Today - 2, Today - 3, Today);
        Assert.Equal(3, lib.VisitDays("dash.example.com", Today));   // today never counts; day -2 once
        Assert.Equal(0, lib.VisitDays("other.com", Today));
    }

    [Fact]
    public void PruneKeepsOnlyTheLast28DaysOfNotes()
    {
        var lib = new Library();
        Note(lib, "a.com", Today - 29, Today - 28, Today - 1);
        Note(lib, "old.com", Today - 40);
        lib.Prune(T0);
        Assert.Equal(new Dictionary<string, List<int>> { [Library.Hash("a.com")] = [Today - 28, Today - 1] }, lib.Data.SiteDays);
    }

    [Fact]
    public void RegularSitesAreCountedAcrossBothSourcesWithoutCountingOneTwice()
    {
        var lib = new Library();
        lib.SetHistoryDays(new()
        {
            ["a.com"] = [Today - 1, Today - 2, Today - 3, Today - 4],
            ["b.com"] = [Today - 1],
        });
        Note(lib, "a.com", Today - 5, Today - 6, Today - 7, Today - 8);   // a.com is in both
        Note(lib, "c.com", Today - 5, Today - 6, Today - 7, Today - 8);
        Assert.Equal(2, lib.RegularSiteCount(Today));   // a.com and c.com
    }

    [Fact]
    public void TheDaysAreKnownOnlyOnceThereIsEnoughHistoryToGoBy()
    {
        var lib = new Library();
        Assert.False(lib.HasVisitDays(Today));
        Note(lib, "x.com", Today);
        Assert.False(lib.HasVisitDays(Today));   // today never counts
        Note(lib, "x.com", Today, Today - 1);
        Assert.False(lib.HasVisitDays(Today));   // one past day is not enough to tell a regular from a new site
        Note(lib, "y.com", Today - 2, Today - 3, Today - 4);
        Assert.True(lib.HasVisitDays(Today));    // four different past days, over any sites
        var history = new Library();
        history.SetHistoryDays(new() { ["x.com"] = [Today - 1, Today - 40] });
        Assert.False(history.HasVisitDays(Today));
        history.SetHistoryDays(new() { ["x.com"] = [Today - 1, Today - 2], ["z.com"] = [Today - 3, Today - 9] });
        Assert.True(history.HasVisitDays(Today));
    }

    [Fact]
    public void AFailedHistoryReadDoesNotForgetTheLastGoodOne()
    {
        var lib = new Library();
        lib.SetHistoryDays(new() { ["a.com"] = [Today - 1, Today - 2, Today - 3, Today - 4] });
        lib.SetHistoryDays([]);
        Assert.Equal(4, lib.VisitDays("a.com", Today));
    }

    // MARK: The file

    [Fact]
    public void AFileWithoutSiteDaysOrRegularFlagsStillOpens()
    {
        var old = """{"version":1,"items":[],"corrections":{"a.com/~":{"isSpecific":true}},"seen":{},"dwellSamples":[]}""";
        var data = LibraryJson.Decode(Encoding.UTF8.GetBytes(old));
        Assert.Empty(data.SiteDays);
        Assert.Equal(new Correction(IsSpecific: true), data.Corrections["a.com/~"]);
    }

    [Fact]
    public void FlagsRoundTripAndStayOutOfFilesThatNeverUsedThem()
    {
        var lib = VisitedOn(5);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        var json = Encoding.UTF8.GetString(LibraryJson.Encode(lib.Data));
        Assert.Contains("\"regular\":true", json, StringComparison.Ordinal);
        Assert.True(LibraryJson.Decode(Encoding.UTF8.GetBytes(json)).Items[0].Regular);
        var plain = new Library();
        plain.Save(Home, "Dash", Thing, Browser.Helium, T0);
        Assert.DoesNotContain("regular", Encoding.UTF8.GetString(LibraryJson.Encode(plain.Data)), StringComparison.Ordinal);
    }

    [Fact]
    public void AListThatNeverNotedASiteIsWrittenWithoutTheFieldAndNotedDaysSurvive()
    {
        Assert.DoesNotContain("siteDays", Encoding.UTF8.GetString(LibraryJson.Encode(new LibraryData())), StringComparison.Ordinal);
        var data = new LibraryData { SiteDays = { ["abc"] = [1, 2] } };
        var again = LibraryJson.Decode(LibraryJson.Encode(data));
        Assert.Equal(new Dictionary<string, List<int>> { ["abc"] = [1, 2] }, again.SiteDays);
    }

    // MARK: Saving

    [Fact]
    public void APlaceOnARegularSiteIsSavedAsARegular()
    {
        var lib = VisitedOn(4);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        Assert.True(lib.Data.Items[0].Regular);
    }

    [Fact]
    public void APlaceOnANewSiteIsKeptInTheList()
    {
        var lib = VisitedOn(3);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        Assert.False(lib.Data.Items[0].Regular);
    }

    [Fact]
    public void AThingOnARegularSiteIsKeptAndIsNotAPlace()
    {
        var lib = VisitedOn(20);
        lib.Save(Post, "Post", Thing, Browser.Helium, T0);
        Assert.Null(lib.Data.Items[0].Regular);
    }

    [Fact]
    public void ACorrectionBeatsFamiliarityWhenSaving()
    {
        var lib = VisitedOn(20);
        lib.SetCorrection(new Correction(IsSpecific: true), "dash.example.com/~");
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        Assert.False(lib.Data.Items[0].Regular);
    }

    // MARK: Migration

    [Fact]
    public void MigrationMarksOnlyPlacesOnRegularSitesAndDeletesNothing()
    {
        var lib = VisitedOn(6);
        lib.Save(Home, "Dash", OldPlace, Browser.Helium, T0);
        lib.Save(Post, "Post", Thing, Browser.Helium, T0);
        lib.Save("https://new.example.org/", "New", Thing, Browser.Helium, T0);
        Assert.All(lib.Data.Items, i => Assert.Null(i.Regular));
        var result = lib.MigrateRegulars(Today);
        Assert.Equal((Migrated: 1, Of: 2), result);   // the post is a thing: not a place
        Assert.Equal(new List<bool?> { true, null, false }, Flags(lib));
        Assert.Equal(3, lib.Data.Items.Count);
        Assert.Equal(0, lib.MigrateRegulars(Today).Of);   // each page is looked at once
    }

    [Fact]
    public void MigrationHonorsCorrections()
    {
        var lib = VisitedOn(6);
        lib.SetCorrection(new Correction(IsSpecific: true), "dash.example.com/~");
        lib.Save(Home, "Dash", OldPlace, Browser.Helium, T0);
        Assert.Equal((Migrated: 0, Of: 1), lib.MigrateRegulars(Today));
        Assert.False(lib.Data.Items[0].Regular);
    }

    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { 1, 2, 3 })]
    public void MigrationLeavesEveryFlagAloneUntilThereIsEnoughHistory(int[] ago)
    {
        var lib = new Library();
        Note(lib, "dash.example.com", [.. ago.Select(a => Today - a)]);
        lib.Save(Home, "Dash", OldPlace, Browser.Chrome, T0);
        Assert.Equal((Migrated: 0, Of: 0), lib.MigrateRegulars(Today));
        Assert.Null(lib.Data.Items[0].Regular);   // null is shown as a normal page and looked at again later
    }

    [Fact]
    public void PagesFlaggedByAnOlderBroaderRuleComeBackToTheListWhenTheyAreNoLongerPlaces()
    {
        var lib = VisitedOn(6);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        lib.Save("https://dash.example.com/team", "Team", Place, Browser.Helium, T0);
        // Flagged as places by the 1.4 rule, though they are deeper pages and a page named by a query value.
        lib.Save("https://dash.example.com/blog/hello-world", "Hello World", Place, Browser.Helium, T0);
        lib.Save("https://dash.example.com/article.php?id=7", "An article", Place, Browser.Helium, T0);
        Assert.All(lib.Data.Items, i => Assert.True(i.Regular));
        Assert.Equal(2, lib.RefreshPlaces());
        Assert.Equal(new List<bool?> { true, true, null, null }, Flags(lib));
        Assert.Equal(4, lib.Data.Items.Count);
        Assert.Equal(0, lib.RefreshPlaces());
    }

    [Fact]
    public void RefreshingPlacesLeavesAPageTypeTheUserMadeARegular()
    {
        var lib = VisitedOn(6);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        lib.Save("https://dash.example.com/blog/hello-world", "Hello World", Place, Browser.Helium, T0);
        lib.MarkRegular(lib.Data.Items[0].Id);
        Assert.Equal(0, lib.RefreshPlaces());
        Assert.Equal(new List<bool?> { true, true }, Flags(lib));
    }

    // MARK: The user's choices

    [Fact]
    public void KeepInMyListMovesTheRegularsOfThatPageTypeAndKeepsFuturePages()
    {
        var lib = VisitedOn(6);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        lib.Save("https://dash.example.com/team", "Team", Place, Browser.Helium, T0);
        lib.KeepInList(lib.Data.Items[0].Id);
        Assert.Equal(new List<bool?> { false, false }, Flags(lib));
        Assert.True(lib.Data.Corrections["dash.example.com/~"].IsSpecific);
        lib.Save("https://dash.example.com/billing", "Billing", Place, Browser.Helium, T0);
        Assert.False(lib.Data.Items[^1].Regular);
    }

    [Fact]
    public void ThisIsARegularMovesThePageTypeOutOfTheList()
    {
        var lib = VisitedOn(1);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        Assert.False(lib.Data.Items[0].Regular);
        lib.MarkRegular(lib.Data.Items[0].Id);
        Assert.True(lib.Data.Items[0].Regular);
        Assert.True(lib.Data.Corrections["dash.example.com/~"].Regular);
        lib.Save("https://dash.example.com/billing", "Billing", Place, Browser.Helium, T0);
        Assert.True(lib.Data.Items[^1].Regular);
    }

    [Fact]
    public void EachChoiceUndoesAndRedoes()
    {
        var lib = VisitedOn(6);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        var id = lib.Data.Items[0].Id;
        var undo = new UndoStack();
        lib.KeepInList(id, undo);
        Assert.False(lib.Data.Items[0].Regular);
        undo.Undo();
        Assert.True(lib.Data.Items[0].Regular);
        Assert.False(lib.Data.Corrections.ContainsKey("dash.example.com/~"));
        undo.Redo();
        Assert.False(lib.Data.Items[0].Regular);
        lib.MarkRegular(id, undo);
        Assert.True(lib.Data.Items[0].Regular);
        undo.Undo();
        Assert.False(lib.Data.Items[0].Regular);
        Assert.Null(lib.Data.Corrections["dash.example.com/~"].Regular);
        Assert.True(lib.Data.Corrections["dash.example.com/~"].IsSpecific);
    }

    [Fact]
    public void SetRegularsPutsFlagsBackForUndo()
    {
        var lib = VisitedOn(6);
        lib.Save(Home, "Dash", Place, Browser.Helium, T0);
        var id = lib.Data.Items[0].Id;
        lib.KeepInList(id);
        lib.SetRegulars(new Dictionary<Guid, bool?> { [id] = true });
        Assert.True(lib.Data.Items[0].Regular);
    }
}

/// A place page on a regular site, read by the judge, is saved as a regular (the judge hands the library the verdict).
public class JudgeRegularsTests
{
    const string Dash = "https://dash.example.com/";
    static int Today => Familiarity.Today(T0);

    static Harness VisitedOn(int count)
    {
        var library = new Library();
        library.SetHistoryDays(new() { ["dash.example.com"] = Enumerable.Range(1, count).Select(d => Today - d).ToHashSet() });
        return new Harness(library);
    }

    static void ReadOnePage(Harness h, string url, string title = "Dash")
    {
        h.Show(T(url, title, active: true));
        h.Wait(120); h.Show(T(url, title, active: true));
    }

    [Fact]
    public void APlaceOnARegularSiteThatHeldAttentionIsSavedAsARegular()
    {
        var h = VisitedOn(5);
        ReadOnePage(h, Dash);
        Assert.Equal(new List<bool?> { true }, h.Library.Data.Items.Select(i => i.Regular).ToList());
    }

    [Fact]
    public void APlaceOnANewSiteThatHeldAttentionIsKept()
    {
        var h = VisitedOn(1);
        ReadOnePage(h, Dash);
        Assert.Equal(new List<bool?> { false }, h.Library.Data.Items.Select(i => i.Regular).ToList());
    }

    [Fact]
    public void ARegularStillNeedsAttentionToBeSaved()
    {
        var h = VisitedOn(20);
        h.Show(T(Dash, "Dash", active: true));
        h.Wait(5); h.Show(T(Dash, "Dash", active: true));
        h.AssertNothingSaved();
    }

    [Fact]
    public void ASpecificPageOnARegularSiteIsKeptInTheList()
    {
        var h = VisitedOn(20);
        ReadOnePage(h, "https://dash.example.com/posts/how-we-found-the-bug", "How we found the bug");
        Assert.Equal(new List<bool?> { null }, h.Library.Data.Items.Select(i => i.Regular).ToList());
    }

    [Fact]
    public void ThePageTypesCorrectionsDecideForNewPages()
    {
        var h = VisitedOn(20);
        h.Library.SetCorrection(new Correction(IsSpecific: true), "dash.example.com/~");
        ReadOnePage(h, Dash);
        Assert.Equal(new List<bool?> { false }, h.Library.Data.Items.Select(i => i.Regular).ToList());
    }
}

public class RegularsSectionTests
{
    static LibraryData Data()
    {
        var lib = new Library();
        lib.SetHistoryDays(new() { ["dash.example.com"] = Enumerable.Range(1, 5).Select(d => Familiarity.Today(T0) - d).ToHashSet() });
        lib.Save("https://a.com/p/1", "Post", new Verdict(true, Kind.Reading, "a.com/p/*"), Browser.Helium, T0);
        lib.Save("https://dash.example.com/", "Dash", new Verdict(true, Kind.Tools, "dash.example.com/~", IsPlace: true), Browser.Helium, T0);
        lib.Save("https://new.example.org/", "New tool", new Verdict(true, Kind.Tools, "new.example.org/~", IsPlace: true), Browser.Helium, T0);
        return lib.Data;
    }

    static string[] Titles(LibrarySection section) =>
        LibraryQuery.Items(Data(), section, "", T0).Select(i => i.Title).Order().ToArray();

    [Fact]
    public void RegularsShowOnlyInTheirOwnSection()
    {
        Assert.Equal(["Dash"], Titles(LibrarySection.Regulars));
        Assert.Equal(["New tool", "Post"], Titles(LibrarySection.All));
        Assert.Equal(["New tool"], Titles(LibrarySection.OfKind(Kind.Tools)));
        Assert.Empty(Titles(LibrarySection.Older));
        Assert.Empty(Titles(LibrarySection.Done));
    }

    [Fact]
    public void ADonePageShowsInDoneWhetherOrNotItIsARegular()
    {
        var d = Data();
        int i = d.Items.FindIndex(x => x.Regular == true);
        d.Items[i] = d.Items[i] with { Status = ItemStatus.Done, LastSeen = T0.AddDays(-90) };
        Assert.Equal(["Dash"], LibraryQuery.Items(d, LibrarySection.Done, "", T0).Select(x => x.Title).ToArray());
        Assert.Empty(LibraryQuery.Items(d, LibrarySection.Older, "", T0));
        Assert.Empty(LibraryQuery.Items(d, LibrarySection.Regulars, "", T0));
        var counts = LibraryQuery.Counts(d, T0);
        Assert.Equal(1, counts[LibrarySection.Done]);
        Assert.False(counts.ContainsKey(LibrarySection.Regulars));
    }

    [Fact]
    public void EmptyDoneDeletesExactlyWhatTheDoneListShows()
    {
        var lib = new Library(Data());
        var regular = lib.Data.Items.First(x => x.Regular == true);
        lib.SetDone(regular.Id, true, T0);   // a Done page that is also a regular
        var shown = LibraryQuery.Items(lib.Data, LibrarySection.Done, "", T0).Select(x => x.Id).ToHashSet();
        Assert.Single(shown);
        var before = lib.Data.Items.Select(x => x.Id).ToHashSet();
        lib.EmptyDone(T0);
        before.ExceptWith(lib.Data.Items.Select(x => x.Id));
        Assert.Equal(shown, before);
    }

    [Fact]
    public void CountsFollowTheSections()
    {
        var counts = LibraryQuery.Counts(Data(), T0);
        Assert.Equal(1, counts[LibrarySection.Regulars]);
        Assert.Equal(2, counts[LibrarySection.All]);
        Assert.Equal(1, counts[LibrarySection.OfKind(Kind.Tools)]);
        Assert.False(counts.ContainsKey(LibrarySection.Done));
        Assert.Equal(1, LibraryQuery.Count(Data(), LibrarySection.Regulars, T0));
    }

    [Fact]
    public void SearchStillFindsARegular() =>
        Assert.Equal(["Dash"], LibraryQuery.Items(Data(), LibrarySection.All, "dash", T0).Select(x => x.Title).ToArray());
}
