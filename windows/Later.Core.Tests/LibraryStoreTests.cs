using System.Text;
using System.Text.Json.Nodes;
using Later.Core;
using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

public class LibraryStoreTests
{
    static LibraryStore StoreIn(TempDir dir) => new(dir.At("library.json"));

    static LibraryData Sample(string title)
    {
        var data = new LibraryData();
        data.Items.Add(new Item(Guid.Parse("00000000-0000-0000-0000-000000000001"), "https://a.com/p/1234567", title, "a.com", Kind.Shopping,
                                "a.com/p/*", Browser.Helium, Unix(1), Unix(2), 1, ItemStatus.Active));
        return data;
    }

    static LibraryData Read(string path) => LibraryJson.Decode(File.ReadAllBytes(path));

    static void Garbage(string path) => File.WriteAllText(path, "{garbage");

    static string[] CorruptCopies(TempDir dir) =>
        Directory.GetFiles(dir.Path, "library.corrupt-*").Select(p => Path.GetFileName(p)).ToArray();

    // Check: what Load would find, touching nothing (the background helper's question).

    [Fact]
    public void CheckSaysFineForAReadableOrAMissingList()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        Assert.Equal(LibraryStore.Health.Fine, store.Check());
        store.Save(Sample("A"));
        Assert.Equal(LibraryStore.Health.Fine, store.Check());
    }

    [Fact]
    public void CheckSaysNeedsRecoveryForADamagedListOrAMissingOneWithABackupAndTouchesNothing()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A")); store.Save(Sample("B"));   // the second save makes the backup
        Garbage(dir.At("library.json"));
        Assert.Equal(LibraryStore.Health.NeedsRecovery, store.Check());
        File.Delete(dir.At("library.json"));
        Assert.Equal(LibraryStore.Health.NeedsRecovery, store.Check());   // only the backup is left
        Assert.Equal(["library.json.bak"], Directory.GetFiles(dir.Path).Select(p => Path.GetFileName(p)).ToArray());
    }

    [Fact]
    public void CheckSaysUnreadableForAListThatIsOnlyLocked()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        using (new FileStream(dir.At("library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(LibraryStore.Health.Unreadable, store.Check());
        Assert.Equal(LibraryStore.Health.Fine, store.Check());
    }

    [Fact]
    public void RoundTrips()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        var loaded = store.Load();
        AssertSame(Sample("A"), loaded.Data);
        Assert.Null(loaded.Notice);
    }

    [Fact]
    public void AKindFromANewerBuildReadsAsOther()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        File.WriteAllText(store.FilePath, """{"version":1,"items":[{"id":"00000000-0000-0000-0000-000000000001","url":"https://a.com/p/1234567","title":"A","host":"a.com","kind":"gizmos","pageType":"a.com/p/*","browser":"helium","firstSaved":1,"lastSeen":2,"visits":1,"status":"active"}],"corrections":{"a.com/p/*":{"kind":"gizmos"}},"seen":{},"dwellSamples":[]}""");
        var loaded = store.Load();
        Assert.Null(loaded.Notice);
        Assert.Equal(Kind.Other, loaded.Data.Items[0].Kind);
        Assert.Equal(Kind.Other, loaded.Data.Corrections["a.com/p/*"].Kind);
    }

    [Fact]
    public void MissingFileGivesAnEmptyList()
    {
        using var dir = new TempDir();
        var loaded = StoreIn(dir).Load();
        AssertSame(new LibraryData(), loaded.Data);
        Assert.Null(loaded.Notice);
    }

    [Fact]
    public void KeepsThePreviousVersionAsBackup()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        store.Save(Sample("B"));
        AssertSame(Sample("A"), Read(store.FilePath + ".bak"));
    }

    [Fact]
    public void CorruptFileIsKeptAndTheBackupIsLoaded()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        store.Save(Sample("B"));
        Garbage(store.FilePath);
        var loaded = store.Load();
        AssertSame(Sample("A"), loaded.Data);
        Assert.NotNull(loaded.Notice);
        Assert.Single(CorruptCopies(dir));
    }

    [Fact]
    public void LeftoverTempFileIsIgnored()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        File.WriteAllText(store.FilePath + ".tmp", "{half-written");
        AssertSame(Sample("A"), store.Load().Data);
    }

    [Fact]
    public void ResetRemovesEverything()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        store.Save(Sample("B"));
        File.WriteAllText(store.FilePath + ".tmp", "x");
        store.Reset();
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void FailedSaveKeepsTheBackup()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        store.Save(Sample("B"));
        Directory.CreateDirectory(store.FilePath + ".tmp");   // the temp file can't be created, on any platform
        Assert.ThrowsAny<IOException>(() => store.Save(Sample("C")));
        AssertSame(Sample("A"), Read(store.FilePath + ".bak"));
        AssertSame(Sample("B"), Read(store.FilePath));
    }

    [Fact]
    public void OlderFilesWithoutDismissalsLoad()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        var json = JsonNode.Parse(LibraryJson.Encode(Sample("A")))!.AsObject();
        Assert.True(json.Remove("dismissed"));
        File.WriteAllText(store.FilePath, json.ToJsonString());
        var loaded = store.Load();
        AssertSame(Sample("A"), loaded.Data);
        Assert.Null(loaded.Notice);
    }

    [Fact]
    public void SavesTrustAFileThisStoreReadOrWrote()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        Garbage(store.FilePath);   // changed behind the store's back
        store.Save(Sample("B"));   // not decoded first: the store wrote the file itself
        AssertSame(Sample("B"), Read(store.FilePath));
        Garbage(store.FilePath);
        Assert.Throws<IOException>(() => new LibraryStore(store.FilePath).Save(Sample("C")));   // a new store checks
        var reloaded = new LibraryStore(store.FilePath);
        store.Save(Sample("D"));
        AssertSame(Sample("D"), reloaded.Load().Data);
        reloaded.Save(Sample("E"));   // known good after its own load
        AssertSame(Sample("E"), Read(store.FilePath));
    }

    [Fact]
    public void CorruptCopiesNeverCollide()
    {
        using var dir = new TempDir(); var store = StoreIn(dir);
        for (int n = 0; n < 2; n++)
        {
            Garbage(store.FilePath);
            Assert.True(store.Load().Writable);
        }
        Assert.Equal(2, CorruptCopies(dir).Length);
    }

    [Fact]
    public void CorruptFileThatCantBeSetAsideIsNeverSavedOver()
    {
        if (!OperatingSystem.IsWindows()) return;   // holding a file open blocks a move only on Windows
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        store.Save(Sample("B"));
        Garbage(store.FilePath);
        var garbage = File.ReadAllBytes(store.FilePath);
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // readable (so it's malformed), but without Delete it can't be moved
        {
            var loaded = store.Load();
            AssertSame(Sample("A"), loaded.Data);
            Assert.False(loaded.Writable);
            Assert.Contains("set aside", loaded.Notice, StringComparison.Ordinal);
            Assert.Empty(CorruptCopies(dir));
            Assert.Throws<IOException>(() => store.Save(Sample("C")));
        }
        Assert.Throws<IOException>(() => store.Save(Sample("C")));   // released, but still unreadable and never set aside
        AssertSame(Sample("A"), Read(store.FilePath + ".bak"));
        Assert.Equal(garbage, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void SaveSurvivesAReaderHoldingTheOldFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            store.Save(Sample("B"));
        AssertSame(Sample("B"), Read(store.FilePath));
    }

    [Fact]
    public void LockedButValidFileIsNotTreatedAsCorrupt()
    {
        if (!OperatingSystem.IsWindows()) return;   // a sharing violation exists only on Windows
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        store.Save(Sample("B"));
        var bytes = File.ReadAllBytes(store.FilePath);
        var fresh = new LibraryStore(store.FilePath);
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = fresh.Load();
            Assert.False(loaded.Writable);
            Assert.Contains("try again", loaded.Notice, StringComparison.Ordinal);
            Assert.Empty(CorruptCopies(dir));
            Assert.Throws<IOException>(() => fresh.Save(Sample("C")));
        }
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
        AssertSame(Sample("A"), Read(store.FilePath + ".bak"));
        var again = fresh.Load();   // the lock is gone: the newest list loads, nothing was set aside
        AssertSame(Sample("B"), again.Data);
        Assert.True(again.Writable);
        Assert.Null(again.Notice);
    }

    [Fact]
    public void LoadDoesNotBlockAReplace()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir(); var store = StoreIn(dir);
        store.Save(Sample("A"));
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))   // what Load itself opens with
        {
            store.Save(Sample("B"));
            Assert.Equal("B", new LibraryStore(store.FilePath).Load().Data.Items[0].Title);
        }
    }
}
