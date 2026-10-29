using System.Globalization;
using Xunit;

namespace Later.Core.Tests;

/// Shared fixtures: the clock, the tab helper, the judge harness and data comparison.
internal static class Fixtures
{
    public const string ShopHome = "https://www.amazon.com/";   // a habit site's home: general by its own rule
    public const string ProductA = "https://shop.example.com/products/1000001";
    public const string ProductB = "https://shop.example.com/products/1000002";

    /// 2027-01-15 08:00:00 UTC; the Swift tests use the same instant.
    public static readonly DateTime T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).UtcDateTime;

    public static DateTime Unix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    public static DateTime At(string isoWithoutZone) =>
        DateTime.Parse(isoWithoutZone + "Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static Tab T(string url, string title = "Walnut desk organizer", bool active = false, int window = 0) =>
        new(url, title, active, window);

    public static Tab Place(string url, int window, int tab, long id, bool active = false) =>
        new(url, "Walnut desk organizer", active, window, tab, id);

    /// The Swift tests compare whole `LibraryData` values; here the pieces are compared one by one.
    public static void AssertSame(LibraryData expected, LibraryData actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.Corrections, actual.Corrections);
        Assert.Equal(expected.Seen, actual.Seen);
        Assert.Equal(expected.DwellSamples, actual.DwellSamples);
        Assert.Equal(expected.Dismissed, actual.Dismissed);
        Assert.Equal(expected.SiteDays, actual.SiteDays);
    }

    public static Item MakeItem(string url, string title, string host = "a.com", Kind kind = Kind.Reading, string pageType = "a.com/*",
                                Browser browser = Browser.Helium, DateTime? saved = null, DateTime? seen = null, int visits = 1,
                                ItemStatus status = ItemStatus.Active, DateTime? doneAt = null) =>
        new(Guid.NewGuid(), url, title, host, kind, pageType, browser, saved ?? T0, seen ?? saved ?? T0, visits, status, doneAt);
}

/// A library, a judge watching it, and a clock the test moves.
internal sealed class Harness
{
    public Library Library { get; }
    public Judge Judge { get; }
    public ISet<Browser> Watched { get; }
    public DateTime Now { get; set; } = Fixtures.T0;

    public Harness(Library? library = null, ISet<Browser>? watched = null)
    {
        Library = library ?? new Library();
        Watched = watched ?? new HashSet<Browser> { Browser.Helium };
        Judge = new Judge(Library, Watched);
        Judge.SetFrontBrowser(Browser.Helium, Now);
    }

    public void Wait(double seconds) => Now = Now.AddSeconds(seconds);

    public void Show(params Tab[] tabs) => Judge.Ingest(new Snapshot(Browser.Helium, tabs), Now);

    public void Read(Browser browser, params Tab[] tabs) => Judge.Ingest(new Snapshot(browser, tabs), Now);

    public List<string> Saved => Library.Data.Items.Select(i => i.Url).ToList();

    public void AssertSaved(params string[] urls) => Assert.Equal(urls, Saved.ToArray());

    public void AssertNothingSaved() => Assert.Empty(Library.Data.Items);
}
