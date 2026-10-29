using System.Globalization;
using Later.Core;
using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class HistoryLookupTests
{
    static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
    static long ChromiumTime(double unix) => (long)((unix + 11_644_473_600) * 1_000_000);
    static DateTime At(double unix) => DateTime.UnixEpoch.AddSeconds(unix);
    const double Jan1 = 1_767_225_600.0;   // 2026-01-01 00:00 UTC

    /// A profile's History file with Chromium's two tables. `pages` maps a link to its title, visit count and visit times (Unix).
    static void MakeHistory(string root, Dictionary<string, (int Count, double[] Visits, string Title)> pages, string profile = "Default")
    {
        var sql = new System.Text.StringBuilder(@"
            CREATE TABLE urls(id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER DEFAULT 0 NOT NULL,
                              typed_count INTEGER DEFAULT 0 NOT NULL, last_visit_time INTEGER NOT NULL, hidden INTEGER DEFAULT 0 NOT NULL);
            CREATE INDEX urls_url_index ON urls(url);
            CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER NOT NULL, visit_time INTEGER NOT NULL);");
        int id = 0;
        foreach (var (url, e) in pages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            id++;
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO urls(id, url, title, visit_count, last_visit_time) VALUES({id}, '{url}', '{e.Title}', {e.Count}, {N(ChromiumTime(e.Visits.DefaultIfEmpty(0).Max()))});");
            foreach (var t in e.Visits) sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO visits(url, visit_time) VALUES({id}, {N(ChromiumTime(t))});");
        }
        TestSqlite.Exec(Path.Combine(root, profile, "History"), sql.ToString());
    }

    static Dictionary<string, (int, double[], string)> Pages(params (string Url, int Count, double[] Visits)[] pages) =>
        pages.ToDictionary(p => p.Url, p => (p.Count, p.Visits, ""), StringComparer.Ordinal);

    [Fact]
    public void GiveTheFirstVisitAndTheVisitCount()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, Pages(("https://a.com/p", 7, [Jan1 + 500, Jan1, Jan1 + 9_000])));
        var found = HistoryLookupStore.Lookup(["https://a.com/p", "https://a.com/other"], HistoryLookupStore.Databases(dir.Path));
        Assert.Single(found);
        Assert.Equal(new HistoryResult(At(Jan1), 7), found["https://a.com/p"]);
    }

    [Fact]
    public void WwwAndTrailingSlashSpellingsAreTriedWhenThereIsNoExactMatch()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, Pages(("https://www.a.com/p/", 2, [Jan1]), ("https://b.com/", 3, [Jan1 + 60]), ("https://www.c.com/p", 4, [Jan1 + 120])));
        var found = HistoryLookupStore.Lookup(["https://a.com/p", "https://www.b.com", "https://c.com/p/"], HistoryLookupStore.Databases(dir.Path));
        Assert.Equal(2, found["https://a.com/p"].VisitCount);
        Assert.Equal(3, found["https://www.b.com"].VisitCount);
        Assert.Equal(4, found["https://c.com/p/"].VisitCount);
    }

    [Fact]
    public void AnExactMatchBeatsASpelling()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, Pages(("https://a.com/p", 1, [Jan1 + 10]), ("https://www.a.com/p", 9, [Jan1])));
        Assert.Equal(1, HistoryLookupStore.Lookup(["https://a.com/p"], HistoryLookupStore.Databases(dir.Path))["https://a.com/p"].VisitCount);
    }

    [Fact]
    public void ProfilesCombineEarliestVisitAndAllVisits()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, Pages(("https://a.com/p", 2, [Jan1 + 100])));
        MakeHistory(dir.Path, Pages(("https://a.com/p", 3, [Jan1])), "Profile 1");
        var found = HistoryLookupStore.Lookup(["https://a.com/p"], HistoryLookupStore.Databases(dir.Path));
        Assert.Equal(new HistoryResult(At(Jan1), 5), found["https://a.com/p"]);
    }

    [Fact]
    public void AMissingOrUnreadableFileAddsNothing()
    {
        using var dir = new TempDir();
        Assert.Empty(HistoryLookupStore.Databases(dir.Path));
        Assert.Empty(HistoryLookupStore.Lookup(["https://a.com/"], [Path.Combine(dir.Path, "Default", "History")]));
        Directory.CreateDirectory(dir.At("Default"));
        File.WriteAllText(dir.At(Path.Combine("Default", "History")), "not a database");
        Assert.Empty(HistoryLookupStore.Lookup(["https://a.com/"], HistoryLookupStore.Databases(dir.Path)));
    }

    [Fact]
    public void TheFileIsNeverChanged()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, Pages(("https://a.com/p", 1, [Jan1])));
        var path = dir.At(Path.Combine("Default", "History"));
        var before = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path);
        HistoryLookupStore.Lookup(["https://a.com/p"], [path]);
        HistoryLookupStore.LinksForTitles(["x"], [path], At(Jan1));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["History"], Directory.GetFiles(dir.At("Default")).Select(f => Path.GetFileName(f)).ToArray());   // no journal or lock files
    }

    [Fact]
    public void LinksForTitlesTakesTheMostRecentVisitAndIgnoresOldOnes()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, new Dictionary<string, (int, double[], string)>
        {
            ["https://a.com/old"] = (1, [Jan1], "Shared"),
            ["https://a.com/new"] = (1, [Jan1 + 5_000], "Shared"),
            ["https://a.com/stale"] = (1, [Jan1 - 100_000], "Stale"),
            ["https://a.com/other"] = (1, [Jan1 + 10], "Other"),
        });
        MakeHistory(dir.Path, new Dictionary<string, (int, double[], string)> { ["https://b.com/newest"] = (1, [Jan1 + 9_000], "Shared") }, "Profile 1");
        var links = HistoryLookupStore.LinksForTitles(["Shared", "Stale", "Other", "Nothing"], HistoryLookupStore.Databases(dir.Path), At(Jan1 - 10));
        Assert.Equal("https://b.com/newest", links["Shared"]);
        Assert.Equal("https://a.com/other", links["Other"]);
        Assert.False(links.ContainsKey("Stale"));
        Assert.False(links.ContainsKey("Nothing"));
    }

    [Fact]
    public void TitlesForLinksGivesTheStoredTitleOfAnExactAddress()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, new Dictionary<string, (int, double[], string)> { ["https://a.com/p"] = (1, [Jan1], "Page"), ["https://a.com/blank"] = (1, [Jan1], "") });
        MakeHistory(dir.Path, new Dictionary<string, (int, double[], string)> { ["https://a.com/blank"] = (1, [Jan1], "Second profile") }, "Profile 1");
        var titles = HistoryLookupStore.TitlesForLinks(["https://a.com/p", "https://a.com/blank", "https://a.com/none"], HistoryLookupStore.Databases(dir.Path));
        Assert.Equal("Page", titles["https://a.com/p"]);
        Assert.Equal("Second profile", titles["https://a.com/blank"]);
        Assert.False(titles.ContainsKey("https://a.com/none"));
    }

    [Fact]
    public void VariantsToggleTheSlashAndWww()
    {
        Assert.Equal(["https://a.com/p", "https://a.com/p/", "https://www.a.com/p", "https://www.a.com/p/"], HistoryLookupStore.Variants("https://a.com/p"));
        Assert.Equal(["https://www.a.com/p?x=1", "https://a.com/p?x=1"], HistoryLookupStore.Variants("https://www.a.com/p?x=1"));
        Assert.Equal(["not a url"], HistoryLookupStore.Variants("not a url"));
    }
}
