using System.Globalization;
using Later.Core;
using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

/// The days each site was visited, from History files built the way Chromium makes them (with the visit's transition).
public class VisitDaysTests
{
    const double Day0 = 1_767_225_600.0;   // 2026-01-01 00:00 UTC
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
    static long ChromiumTime(double unix) => (long)((unix + 11_644_473_600) * 1_000_000);
    static double Unix(int day, double hour = 12) => Day0 + day * 86_400 + hour * 3_600;
    static DateTime Since => DateTime.UnixEpoch.AddSeconds(Day0);
    static int Number(int day) => Familiarity.DayNumber(DateTime.UnixEpoch.AddSeconds(Day0 + day * 86_400 + 3_600));

    static void MakeHistory(string root, (string Url, double Unix, int Transition)[] visits, string profile = "Default")
    {
        var sql = new System.Text.StringBuilder(@"
            CREATE TABLE urls(id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER DEFAULT 0 NOT NULL,
                              typed_count INTEGER DEFAULT 0 NOT NULL, last_visit_time INTEGER NOT NULL, hidden INTEGER DEFAULT 0 NOT NULL);
            CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER NOT NULL, visit_time INTEGER NOT NULL, transition INTEGER DEFAULT 0 NOT NULL);");
        var links = visits.Select(v => v.Url).Distinct().Order(StringComparer.Ordinal).ToList();
        for (int i = 0; i < links.Count; i++) sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO urls(id, url, last_visit_time) VALUES({i + 1}, '{links[i]}', 0);");
        foreach (var (url, time, transition) in visits)
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO visits(url, visit_time, transition) VALUES({links.IndexOf(url) + 1}, {N(ChromiumTime(time))}, {transition});");
        TestSqlite.Exec(Path.Combine(root, profile, "History"), sql.ToString());
    }

    static Dictionary<string, HashSet<int>> Ask(string root, TimeZoneInfo? zone = null) =>
        HistoryLookupStore.VisitDays(HistoryLookupStore.Databases(root), Since, zone ?? Utc);

    [Fact]
    public void GivesTheDaysEachSiteWasVisitedMergedAcrossProfilesAndWww()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, [("https://www.a.com/x", Unix(1), 0), ("https://a.com/y", Unix(1, 20), 1), ("https://a.com/", Unix(3), 0), ("https://b.org:8080/p?q=1#f", Unix(2), 0)]);
        MakeHistory(dir.Path, [("https://a.com/z", Unix(5), 0)], "Profile 1");
        var days = Ask(dir.Path);
        Assert.Equal([Number(1), Number(3), Number(5)], days["a.com"].Order().ToArray());
        Assert.Equal([Number(2)], days["b.org"].ToArray());
        Assert.Equal(2, days.Count);
    }

    [Fact]
    public void LeavesOutOlderVisitsFramesAndPagesThatAreNotTheWeb()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, [("https://old.com/", Unix(-1), 0), ("https://ad.example.net/frame", Unix(2), 3), ("https://ad.example.net/manual", Unix(2), 4),
            ("chrome://settings/", Unix(2), 0), ("file:///tmp/a.html", Unix(2), 0), ("https://kept.com/", Unix(2), 0x30000000 | 1)]);
        var days = Ask(dir.Path);
        Assert.Equal(["kept.com"], days.Keys.ToArray());   // the transition's qualifier bits don't hide a typed visit
        Assert.Equal([Number(2)], days["kept.com"].ToArray());
    }

    [Fact]
    public void AVisitJustBeforeMidnightBelongsToTheNewDayInTheUsersTimeZone()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, [("https://a.com/", Unix(1, 23.9), 0)]);
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");   // UTC+9: it is already day 2, 08:54
        Assert.Equal([Number(2)], Ask(dir.Path, tokyo)["a.com"].ToArray());
        Assert.Equal([Number(1)], Ask(dir.Path)["a.com"].ToArray());
    }

    [Fact]
    public void AMissingOrUnreadableHistoryGivesNothing()
    {
        using var dir = new TempDir();
        Assert.Empty(HistoryLookupStore.VisitDays([], Since, Utc));
        Directory.CreateDirectory(Path.Combine(dir.Path, "Default"));
        File.WriteAllText(Path.Combine(dir.Path, "Default", "History"), "not a database");
        Assert.Empty(Ask(dir.Path));
    }

    [Fact]
    public void TheHistoryFileIsNeverWritten()
    {
        using var dir = new TempDir();
        MakeHistory(dir.Path, [("https://a.com/", Unix(1), 0)]);
        var path = Path.Combine(dir.Path, "Default", "History");
        var before = File.ReadAllBytes(path);
        _ = Ask(dir.Path);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(["History"], Directory.GetFiles(Path.Combine(dir.Path, "Default")).Select(f => Path.GetFileName(f)).ToArray());   // no journal left behind
    }

    [Theory]
    [InlineData("https://www.Example.com/a?b#c", "example.com")]
    [InlineData("http://user:pw@example.com:8080/", "example.com")]
    [InlineData("HTTPS://a.com", "a.com")]
    [InlineData("chrome://settings", null)]
    [InlineData("file:///tmp/x", null)]
    [InlineData("about:blank", null)]
    [InlineData("https:///nohost", null)]
    public void TheSiteOfALink(string link, string? site) => Assert.Equal(site, HistoryLookupStore.SiteOf(link));
}
