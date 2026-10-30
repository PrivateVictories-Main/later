using Later.App.Icons;
using Later.Core;
using Xunit;

namespace Later.Windows.Tests;

/// The sites' icons: asked for only about hosts on the list and at most once an hour for one that has none; trimmed to the list.
public sealed class SiteIconStoreTests : IDisposable
{
    static readonly byte[] Png = [1, 2, 3, 4];

    readonly TempDir dir = new();
    DateTime now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    int lookups;

    public void Dispose() => dir.Dispose();

    static Item Page(string host) => new(Guid.NewGuid(), $"https://{host}/p", host, host, Kind.Reading, host + "/*", Browser.Helium, DateTime.UtcNow, DateTime.UtcNow, 1, ItemStatus.Active);

    SiteIconStore Store(string? folder = null, Func<byte[], bool>? accept = null) =>
        new(folder ?? Path.Combine(dir.Path, "icons"), () => { lookups++; return []; }, accept ?? (_ => true), () => now, work => work());

    [Fact]
    public void AHostWithNoIconIsAskedAboutOnceAnHourAndNotOnEveryShow()
    {
        var store = Store();
        var items = new[] { Page("a.example.com") };
        store.Update(items);
        Assert.Equal(1, lookups);
        store.Update(items);   // the window shows again, or a page is saved
        Assert.Equal(1, lookups);
        now = now.AddMinutes(59);
        store.Update(items);
        Assert.Equal(1, lookups);
        now = now.AddMinutes(2);
        store.Update(items);
        Assert.Equal(2, lookups);   // an hour on, it is asked again
        Assert.Null(store.For("a.example.com"));
    }

    [Fact]
    public void ANewHostIsAskedAboutAtOnceAndAnOldOneIsNot()
    {
        var store = Store();
        store.Update([Page("a.example.com")]);
        Assert.Equal(1, lookups);
        store.Update([Page("a.example.com"), Page("b.example.com")]);
        Assert.Equal(2, lookups);
    }

    [Fact]
    public void AKeptIconIsUsedWithoutAskingTheBrowserAndTellsTheWindow()
    {
        var folder = Path.Combine(dir.Path, "icons");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.example.com.png"), Png);
        var store = Store(folder);
        int changed = 0;
        store.Changed += () => changed++;
        store.Update([Page("a.example.com")]);
        Assert.Equal(Png, store.For("a.example.com"));
        Assert.Equal(0, lookups);   // nothing was missing
        Assert.Equal(1, changed);
    }

    [Fact]
    public void AKeptFileThatDoesNotDecodeIsAskedForAgain()
    {
        var folder = Path.Combine(dir.Path, "icons");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.example.com.png"), Png);
        var store = Store(folder, accept: _ => false);
        store.Update([Page("a.example.com")]);
        Assert.Equal(1, lookups);
        Assert.Null(store.For("a.example.com"));
    }

    [Fact]
    public void AHostThatLeavesTheListLosesItsIconAndItsFile()
    {
        var folder = Path.Combine(dir.Path, "icons");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.example.com.png");
        File.WriteAllBytes(file, Png);
        var store = Store(folder);
        store.Update([Page("a.example.com")]);
        Assert.NotNull(store.For("a.example.com"));
        store.Update([Page("b.example.com")]);
        Assert.Null(store.For("a.example.com"));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void TheFolderIsTrimmedToTheSavedHostsOncePerLaunchEvenToNone()
    {
        var folder = Path.Combine(dir.Path, "icons");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "gone.example.com.png"), Png);
        File.WriteAllBytes(Path.Combine(folder, "a.example.com.png"), Png);
        var store = Store(folder);
        store.Update([Page("a.example.com")]);
        Assert.Equal(["a.example.com.png"], Directory.GetFiles(folder).Select(f => new FileInfo(f).Name).ToArray());
        File.WriteAllBytes(Path.Combine(folder, "later.example.com.png"), Png);
        store.Update([Page("a.example.com")]);
        Assert.True(File.Exists(Path.Combine(folder, "later.example.com.png")));   // once per launch: not again

        var emptied = Store(Path.Combine(dir.Path, "other"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "other"));
        File.WriteAllBytes(Path.Combine(dir.Path, "other", "x.png"), Png);
        emptied.Update([]);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "other")));
    }

    [Fact]
    public void AnUpdateWhileALookupRunsRunsOnceItEnds()
    {
        var queued = new List<Action>();
        var store = new SiteIconStore(Path.Combine(dir.Path, "icons"), () => { lookups++; return []; }, _ => true, () => now, queued.Add);
        store.Update([Page("a.example.com")]);
        Assert.Single(queued);
        store.Update([Page("a.example.com"), Page("b.example.com")]);   // while the first is still running
        Assert.Single(queued);
        queued[0]();   // the first ends; the second ask goes out
        Assert.Equal(2, queued.Count);
        queued[1]();
        Assert.Equal(2, lookups);
    }

    [Fact]
    public void NothingIsWrittenWhenThereIsNoFolder()
    {
        var store = new SiteIconStore(null, () => [], _ => true, () => now, work => work());
        store.Update([Page("a.example.com")]);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }
}
