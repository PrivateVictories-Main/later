using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

public class LibraryTests
{
    static readonly Verdict Product = new(true, Kind.Shopping, "a.com/p/*");
    const string Link = "https://www.a.com/p/1234567";
    const string Lamp = "https://www.a.com/p/7654321";

    static Library TwoPages()
    {
        var lib = new Library();
        lib.Save(Link, "Desk", Product, Browser.Helium, T0);
        lib.Save(Lamp, "Lamp", Product, Browser.Helium, T0);
        return lib;
    }

    [Fact]
    public void LoadingDropsSavedPagesWhoseLinkCarriesASecretInThePath()
    {
        var lib = new Library();
        lib.Save("https://a.com/reset-password/Xk9fA2bQ7zLm", "Reset", Product, Browser.Helium, T0);
        lib.Save("https://a.com/invite/0b9f6c1e-52a7-4c1b-9d3e-7a4c2f1e8b60", "Invite", Product, Browser.Helium, T0);
        lib.Save("https://a.com/#/accept/Xk9fA2bQ7zLm", "Accept", Product, Browser.Helium, T0);
        lib.Save(Link, "Desk", Product, Browser.Helium, T0);
        lib.Save("https://a.com/account/settings/billing", "Billing", Product, Browser.Helium, T0);
        Assert.Equal(3, lib.DropSecretLinks());
        Assert.Equal(new[] { "Desk", "Billing" }, lib.Data.Items.Select(i => i.Title));
        Assert.False(lib.Contains("https://a.com/reset-password/Xk9fA2bQ7zLm"));
        Assert.Equal(0, lib.DropSecretLinks());
    }

    [Fact]
    public void SavesOnceAndIgnoresDuplicates()
    {
        var lib = new Library();
        Assert.True(lib.Save(Link, "Desk", Product, Browser.Helium, T0));
        Assert.False(lib.Save(Link, "Desk", Product, Browser.Helium, T0));
        Assert.Single(lib.Data.Items);
        Assert.Equal("a.com", lib.Data.Items[0].Host);
        Assert.Equal(1, lib.Data.Items[0].Visits);
    }

    [Fact]
    public void DoneItemsAreNotSavedAgainButUnmarkingBringsThemBack()
    {
        var lib = new Library();
        lib.Save(Link, "Desk", Product, Browser.Helium, T0);
        var id = lib.Data.Items[0].Id;
        lib.SetDone(id, true, T0);
        Assert.False(lib.Save(Link, "Desk", Product, Browser.Helium, T0));
        Assert.Equal(ItemStatus.Done, lib.Data.Items[0].Status);
        lib.SetDone(id, false, T0.AddSeconds(5));
        Assert.Equal(ItemStatus.Active, lib.Data.Items[0].Status);
        Assert.Null(lib.Data.Items[0].DoneAt);
    }

    [Fact]
    public void VisitsOnlyCountForActiveItems()
    {
        var lib = new Library();
        lib.Save(Link, "Desk", Product, Browser.Helium, T0);
        lib.RecordVisit(Link, T0.AddSeconds(60));
        Assert.Equal(2, lib.Data.Items[0].Visits);
        Assert.Equal(T0.AddSeconds(60), lib.Data.Items[0].LastSeen);
        lib.SetDone(lib.Data.Items[0].Id, true, T0);
        lib.RecordVisit(Link, T0.AddSeconds(120));
        Assert.Equal(2, lib.Data.Items[0].Visits);
    }

    [Fact]
    public void RestoreUndoesADeleteExactly()
    {
        var lib = TwoPages();
        var before = lib.CopyData();
        int changes = 0;
        lib.Changed += () => changes++;
        var desk = lib.Data.Items[0];
        lib.Delete(desk.Id, T0);
        Assert.False(lib.Contains(Link));
        Assert.True(lib.IsDismissed(Link));
        lib.Restore(desk, 0);
        Assert.False(lib.IsDismissed(Link));
        AssertSame(before, lib.Data);
        Assert.Equal(2, changes);
        Assert.True(lib.Contains(Link));
        lib.RecordVisit(Link, T0.AddSeconds(60));   // the url index points at the restored item again
        Assert.Equal(2, lib.Data.Items[0].Visits);
    }

    [Fact]
    public void RestoreReplacesAPageSavedAgainSinceTheDelete()
    {
        var lib = TwoPages();
        lib.RecordVisit(Link, T0.AddSeconds(60));
        var before = lib.CopyData();
        var desk = lib.Data.Items[0];
        lib.Delete(desk.Id, T0);
        lib.Save(Link, "Desk again", Product, Browser.Chrome, T0.AddSeconds(120));   // the tab is still open
        int changes = 0;
        lib.Changed += () => changes++;
        lib.Restore(desk, 0);
        AssertSame(before, lib.Data);   // the original wins, back at its old place, with one entry for the page
        Assert.Equal(1, changes);
        lib.RecordVisit(Link, T0.AddSeconds(180));
        Assert.Equal(3, lib.Data.Items[0].Visits);
    }

    [Fact]
    public void MoveTeachesThePageType()
    {
        var lib = TwoPages();
        lib.Move(lib.Data.Items[0].Id, Kind.School);
        Assert.All(lib.Data.Items, i => Assert.Equal(Kind.School, i.Kind));
        Assert.Equal(new Correction(Kind: Kind.School), lib.Data.Corrections["a.com/p/*"]);
    }

    [Fact]
    public void NotAnIdeaDeletesOnlyThatItemAndTeachesThePageType()
    {
        var lib = TwoPages();
        lib.NotAnIdea(lib.Data.Items[0].Id);
        Assert.Equal(new List<string> { "Lamp" }, lib.Data.Items.Select(i => i.Title).ToList());
        Assert.False(lib.Data.Corrections["a.com/p/*"].IsSpecific);
    }

    [Fact]
    public void DeleteAndEmptyDone()
    {
        var lib = TwoPages();
        lib.SetDone(lib.Data.Items[0].Id, true, T0);
        lib.EmptyDone(T0);
        Assert.Equal(new List<string> { "Lamp" }, lib.Data.Items.Select(i => i.Title).ToList());
        Assert.True(lib.IsDismissed(Link));
        Assert.False(lib.IsDismissed(Lamp));
        lib.Delete(lib.Data.Items[0].Id, T0);
        Assert.Empty(lib.Data.Items);
        Assert.False(lib.Contains(Link));
        Assert.True(lib.IsDismissed(Lamp));
        Assert.DoesNotContain(lib.Data.Dismissed.Keys, k => k.Contains("a.com", StringComparison.Ordinal));
    }

    [Fact]
    public void DwellThresholdLearnsAndClamps()
    {
        var lib = new Library();
        Assert.Equal(TimeSpan.FromSeconds(120), lib.DwellThreshold);
        for (int i = 0; i < 20; i++) lib.AddDwellSample(10);
        Assert.Equal(TimeSpan.FromSeconds(60), lib.DwellThreshold);
        for (int i = 0; i < 200; i++) lib.AddDwellSample(1000);
        Assert.Equal(TimeSpan.FromSeconds(300), lib.DwellThreshold);
        Assert.Equal(200, lib.Data.DwellSamples.Count);
        var mid = new Library();
        for (int s = 1; s <= 100; s++) mid.AddDwellSample(s * 2);
        Assert.Equal(TimeSpan.FromSeconds(150), mid.DwellThreshold);
    }

    [Fact]
    public void SeenStoresHashesNotLinks()
    {
        var lib = new Library();
        lib.MarkSeen(Link, T0);
        Assert.True(lib.WasSeen(Link, T0.AddDays(1)));
        Assert.False(lib.WasSeen(Link, T0.AddDays(31)));
        Assert.DoesNotContain(lib.Data.Seen.Keys, k => k.Contains("a.com", StringComparison.Ordinal));
        lib.Prune(T0.AddDays(31));
        Assert.Empty(lib.Data.Seen);
    }

    [Fact]
    public void PruneDropsOldSeenAndDismissalsAtMostDaily()
    {
        var lib = new Library();
        lib.Save(Link, "Desk", Product, Browser.Helium, T0);
        lib.Delete(lib.Data.Items[0].Id, T0);
        lib.MarkSeen(Link, T0);
        lib.Prune(T0.AddDays(29.5));   // nothing old yet
        Assert.True(lib.Data.Seen.Count == 1 && lib.IsDismissed(Link));
        lib.Prune(T0.AddDays(30.25));  // old now, but pruned less than a day ago: skipped
        Assert.True(lib.Data.Seen.Count == 1 && lib.IsDismissed(Link));
        lib.Prune(T0.AddDays(30.5).AddSeconds(1));
        Assert.True(lib.Data.Seen.Count == 0 && !lib.IsDismissed(Link));
    }

    [Fact]
    public void EveryChangeIsReported()
    {
        var lib = new Library();
        int count = 0;
        lib.Changed += () => count++;
        lib.Save(Link, "Desk", Product, Browser.Helium, T0);
        lib.MarkSeen(Link, T0);
        Assert.Equal(2, count);
        Assert.Equal(2, lib.Revision);
    }

    [Fact]
    public void UnchangedSeenDoesNotReportAChange()
    {
        var lib = new Library();
        int count = 0;
        lib.MarkSeen(Link, T0);
        lib.Changed += () => count++;
        lib.MarkSeen(Link, T0);
        lib.Prune(T0.AddDays(1));
        Assert.Equal(0, count);
    }

    [Fact]
    public void CopyDataIsIndependentOfTheLiveData()
    {
        var lib = TwoPages();
        var copy = lib.CopyData();
        lib.SetDone(lib.Data.Items[0].Id, true, T0);
        lib.MarkSeen(Link, T0);
        Assert.Equal(ItemStatus.Active, copy.Items[0].Status);
        Assert.Empty(copy.Seen);
    }

    [Fact]
    public void HostOfDropsWwwAndKeepsUnparsableTextAsIs()
    {
        Assert.Equal("a.com", Library.HostOf("https://www.a.com/x"));
        Assert.Equal("a.com", Library.HostOf("https://a.com/x"));
        Assert.Equal("about:blank", Library.HostOf("about:blank"));
    }

    [Fact]
    public void HashesAreLowercaseHexSha256()
    {
        // The Mac writes the same strings into the file: SHA-256 of the UTF-8 text.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Library.Hash("abc"));
    }
}

public class EmptyTitleTests
{
    [Fact]
    public void APageSavedWithoutATitleIsTitledWithItsHostUntilTheRealOneArrives()
    {
        var lib = new Library();
        lib.Save("https://www.acmehq.example.com/", "", new Verdict(true, Kind.Tools, "acmehq.example.com/~"), Browser.Helium, DateTime.UtcNow);
        Assert.Equal("acmehq.example.com", lib.Data.Items[0].Title);
        lib.UpdateTitle("https://www.acmehq.example.com/", "");
        Assert.Equal("acmehq.example.com", lib.Data.Items[0].Title);
        lib.UpdateTitle("https://www.acmehq.example.com/", "Acme HQ");
        Assert.Equal("Acme HQ", lib.Data.Items[0].Title);
    }
}

public class RefreshKindsTests
{
    [Fact]
    public void UncorrectedItemsTakeTheCurrentRulesAndCorrectedOnesKeepTheUsersKind()
    {
        var lib = new Library();
        lib.Save("https://github.com/apple/swift", "GitHub - apple/swift", new Verdict(true, Kind.Other, "github.com/*/*"), Browser.Helium, T0);
        lib.Save("https://github.com/octocat", "octocat", new Verdict(true, Kind.Other, "github.com/*"), Browser.Helium, T0);
        lib.Save("https://www.amazon.com/dp/B0F1234567", "Sony", new Verdict(true, Kind.Shopping, "amazon.com/dp/*"), Browser.Helium, T0);
        lib.Move(lib.Data.Items[1].Id, Kind.Reading);   // The user's correction for github.com/*
        var before = lib.Data.Items.ToList();
        lib.RefreshKinds();
        Assert.Equal(new List<Kind> { Kind.Tools, Kind.Reading, Kind.Shopping }, lib.Data.Items.Select(i => i.Kind).ToList());
        Assert.Equal(before.Select(i => i.Title), lib.Data.Items.Select(i => i.Title));
        Assert.Equal(before.Select(i => i.Url), lib.Data.Items.Select(i => i.Url));
        Assert.Equal(before.Select(i => i.FirstSaved), lib.Data.Items.Select(i => i.FirstSaved));
        Assert.Equal(3, lib.Data.Items.Count);
    }

    [Fact]
    public void NothingIsUnsavedWhenARuleNowCallsAPageGeneral()
    {
        var lib = new Library();
        lib.Save("https://www.youtube.com/", "YouTube", new Verdict(true, Kind.Videos, "youtube.com/"), Browser.Helium, T0);
        lib.RefreshKinds();
        Assert.Single(lib.Data.Items);
    }

    [Fact]
    public void APageWithoutAHostKeepsItsKindAndCountsAreReported()
    {
        var lib = new Library();
        lib.Save("about:blank", "Blank", new Verdict(true, Kind.Shopping, "/"), Browser.Helium, T0);
        lib.Save("https://github.com/apple/swift", "swift", new Verdict(true, Kind.Other, "github.com/*/*"), Browser.Helium, T0);
        var counts = lib.RefreshKinds();
        Assert.Equal(new List<Kind> { Kind.Shopping, Kind.Tools }, lib.Data.Items.Select(i => i.Kind).ToList());
        Assert.True(counts.Items == 2 && counts.Changed == 1 && counts.ToTools == 1);
    }

    [Fact]
    public void AWhitespaceOnlyTitleIsSavedAsTheHost()
    {
        var lib = new Library();
        lib.Save("https://www.a.com/p/1234567", " \n\t ", new Verdict(true, Kind.Shopping, "a.com/p/*"), Browser.Helium, T0);
        Assert.Equal("a.com", lib.Data.Items[0].Title);
    }
}
