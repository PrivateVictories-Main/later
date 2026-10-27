using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

/// One page is one item, however its link was written, and the user's deletes survive the change of key.
public class PageKeyLibraryTests
{
    static readonly Verdict Video = new(true, Kind.Videos, "youtube.com/watch?v");
    const string WwwLink = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=abc";
    const string BareLink = "http://youtube.com/watch?v=dQw4w9WgXcQ&t=30s";

    [Fact]
    public void TheSamePageWrittenTwoWaysIsSavedOnce()
    {
        var library = new Library();
        Assert.True(library.Save(WwwLink, "Video", Video, Browser.Helium, T0));
        Assert.False(library.Save(BareLink, "Video", Video, Browser.Chrome, T0));
        Assert.True(library.Contains(BareLink));
        Assert.Single(library.Data.Items);
        Assert.Equal(WwwLink, library.Data.Items[0].Url);   // opening it goes back to the link it was saved with
    }

    [Fact]
    public void AVisitAndATitleReachThePageWhicheverLinkAsks()
    {
        var library = new Library();
        library.Save(WwwLink, "Video", Video, Browser.Helium, T0);
        library.RecordVisit(BareLink, T0.AddSeconds(60));
        library.UpdateTitle(BareLink, "Video (new title)");
        Assert.Equal(2, library.Data.Items[0].Visits);
        Assert.Equal("Video (new title)", library.Data.Items[0].Title);
    }

    static Item Entry(string url, string title, double saved, double seen, int visits, double? done = null,
                      Kind kind = Kind.Videos, string type = "youtube.com/watch?v") =>
        new(Guid.NewGuid(), url, title, "youtube.com", kind, type, Browser.Helium, T0.AddSeconds(saved), T0.AddSeconds(seen), visits,
            done is null ? ItemStatus.Active : ItemStatus.Done, done is { } d ? T0.AddSeconds(d) : null);

    [Fact]
    public void LoadingMergesPagesThatAreReallyOne()
    {
        var data = new LibraryData
        {
            Items =
            [
                Entry(WwwLink, "Old title", 100, 200, 2),
                Entry("https://example.com/other", "Other", 50, 50, 1),
                Entry(BareLink, "New title", 10, 900, 3),
            ],
        };
        var library = new Library(data);
        Assert.Equal(1, library.MergeDuplicates());
        Assert.Equal(new List<string> { "New title", "Other" }, library.Data.Items.Select(i => i.Title).ToList());   // the earliest save keeps its place, the latest title wins
        var merged = library.Data.Items[0];
        Assert.Equal(T0.AddSeconds(10), merged.FirstSaved);
        Assert.Equal(T0.AddSeconds(900), merged.LastSeen);
        Assert.Equal(5, merged.Visits);
        Assert.Equal(ItemStatus.Active, merged.Status);
        Assert.Equal(0, library.MergeDuplicates());
        Assert.True(library.Contains(WwwLink) && library.Contains(BareLink));
    }

    [Fact]
    public void AMergedPageIsDoneOnlyIfItWasMarkedDoneAfterTheOtherCopyWasSeen()
    {
        // Done at 50, but the other copy was seen at 100: the user has been back to it, so it is active.
        var seenLater = new Library(new LibraryData { Items = [Entry(WwwLink, "A", 0, 0, 1, 50), Entry(BareLink, "B", 1, 100, 1)] });
        seenLater.MergeDuplicates();
        Assert.True(seenLater.Data.Items.Count == 1 && seenLater.Data.Items[0].Status == ItemStatus.Active && seenLater.Data.Items[0].DoneAt is null);

        // Done at 80, after the other copy was last seen at 1: it stays done.
        var doneLater = new Library(new LibraryData { Items = [Entry(WwwLink, "A", 0, 0, 1, 80), Entry(BareLink, "B", 1, 1, 1)] });
        doneLater.MergeDuplicates();
        Assert.True(doneLater.Data.Items[0].Status == ItemStatus.Done && doneLater.Data.Items[0].DoneAt == T0.AddSeconds(80));

        var allDone = new Library(new LibraryData { Items = [Entry(WwwLink, "A", 0, 0, 1, 50), Entry(BareLink, "B", 1, 1, 1, 80)] });
        allDone.MergeDuplicates();
        Assert.True(allDone.Data.Items[0].Status == ItemStatus.Done && allDone.Data.Items[0].DoneAt == T0.AddSeconds(80));
    }

    [Fact]
    public void AMergedPageKeepsTheUsersKind()
    {
        var data = new LibraryData
        {
            Items = [Entry(WwwLink, "A", 0, 500, 1, kind: Kind.Videos), Entry(BareLink, "B", 1, 1, 1, kind: Kind.School, type: "youtube.com/moved")],
        };
        data.Corrections["youtube.com/moved"] = new Correction(Kind: Kind.School);
        var library = new Library(data);
        library.MergeDuplicates();
        Assert.True(library.Data.Items[0].Kind == Kind.School && library.Data.Items[0].PageType == "youtube.com/moved");
        Assert.Equal(Kind.School, library.Data.Corrections["youtube.com/moved"].Kind);
    }

    [Fact]
    public void DifferentPagesOnOneSiteAreNotMerged()
    {
        var library = new Library(new LibraryData
        {
            Items = [Entry("https://tv.example.com/watch/AAAAAAAAAAA", "Live", 0, 0, 1), Entry("https://tv.example.com/watch/BBBBBBBBBBB", "Live", 1, 1, 1)],
        });
        Assert.Equal(0, library.MergeDuplicates());
        Assert.Equal(2, library.Data.Items.Count);
    }

    [Fact]
    public void APageDeletedBeforeV12StaysDeleted()
    {
        // v1.1 hashed the cleaned link itself; the same link must still be dismissed under the page key.
        var data = new LibraryData();
        data.Dismissed[Library.Hash(WwwLink)] = T0;
        data.Seen[Library.Hash(WwwLink)] = T0;
        var library = new Library(data);
        Assert.True(library.IsDismissed(WwwLink));
        Assert.True(library.WasSeen(WwwLink, T0.AddSeconds(60)));
        library.Undismiss(WwwLink);
        Assert.False(library.IsDismissed(WwwLink));
        Assert.Empty(library.Data.Dismissed);
    }

    [Fact]
    public void NewDeletesAndSightingsAreHashedByPageKeyAndMatchEveryWriting()
    {
        var library = new Library();
        library.Save(WwwLink, "Video", Video, Browser.Helium, T0);
        library.Delete(library.Data.Items[0].Id, T0);
        Assert.Contains(Library.Hash("youtube.com/watch?v=dQw4w9WgXcQ"), library.Data.Dismissed.Keys);
        Assert.True(library.IsDismissed(BareLink));
        library.MarkSeen(BareLink, T0);
        Assert.True(library.WasSeen(WwwLink, T0.AddSeconds(1)));
    }
}
