using System.Globalization;
using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

public class LibraryQueryTests
{
    static readonly DateTime Now = T0;
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo EnUs = new("en-US");

    /// The URL holds no title, so a title match is a title match. `doneDaysAgo` marks the item done.
    static Item Entry(string title, double daysAgo, Kind kind = Kind.Shopping, double? doneDaysAgo = null, string host = "a.com")
    {
        var seen = Now.AddSeconds(-daysAgo * 86_400);
        return new Item(Guid.NewGuid(), $"https://{host}/p/1", title, host, kind, $"{host}/p/*", Browser.Helium, seen, seen, 1,
                        doneDaysAgo is null ? ItemStatus.Active : ItemStatus.Done,
                        doneDaysAgo is { } d ? Now.AddSeconds(-d * 86_400) : null);
    }

    /// Deliberately out of order, so every expected order below comes from sorting.
    static LibraryData Library(params Item[] items) => new() { Items = [.. items] };

    static LibraryData Data() => Library(
        Entry("Lamp", 20),
        Entry("Essay", 2, Kind.School, host: "canvas.school.edu"),
        Entry("Chair", 3, doneDaysAgo: 3),
        Entry("Desk", 1));

    static List<string> Titles(IEnumerable<Item> items) => items.Select(i => i.Title).ToList();

    [Fact]
    public void SectionsSplitByKindAgeAndStatus()
    {
        Assert.Equal(new List<string> { "Desk", "Essay" }, Titles(LibraryQuery.Items(Data(), LibrarySection.All, "", Now)));
        Assert.Equal(new List<string> { "Essay" }, Titles(LibraryQuery.Items(Data(), LibrarySection.OfKind(Kind.School), "", Now)));
        Assert.Equal(new List<string> { "Lamp" }, Titles(LibraryQuery.Items(Data(), LibrarySection.Older, "", Now)));
        Assert.Equal(new List<string> { "Chair" }, Titles(LibraryQuery.Items(Data(), LibrarySection.Done, "", Now)));
    }

    [Fact]
    public void SearchLooksEverywhereWithActiveItemsFirst()
    {
        Assert.Equal(new List<string> { "Desk", "Essay", "Lamp", "Chair" },
                     Titles(LibraryQuery.Items(Data(), LibrarySection.OfKind(Kind.School), "a", Now)));
        Assert.Equal(new List<string> { "Essay" }, Titles(LibraryQuery.Items(Data(), LibrarySection.All, "CANVAS", Now)));
    }

    [Fact]
    public void SearchMatchesTitlesAlone()
    {
        Assert.Equal(new List<string> { "Lamp" }, Titles(LibraryQuery.Items(Data(), LibrarySection.Done, " lamp ", Now)));
    }

    [Fact]
    public void OlderStartsJustPastFourteenDays()
    {
        var d = Library(Entry("Past", 14 + 1.0 / 86_400), Entry("Edge", 14));
        Assert.Equal(new List<string> { "Edge" }, Titles(LibraryQuery.Items(d, LibrarySection.All, "", Now)));
        Assert.Equal(new List<string> { "Past" }, Titles(LibraryQuery.Items(d, LibrarySection.Older, "", Now)));
    }

    [Fact]
    public void TheListLooksTheSameUntilTheDayChangesOrAPageMovesToOlder()
    {
        var morning = Now.Date.AddHours(9);
        var d = Library(Entry("Recent", 1), Entry("Done", 3, doneDaysAgo: 3));
        Assert.True(LibraryQuery.LooksSame(d, morning, morning.AddSeconds(3_600), Utc));
        Assert.False(LibraryQuery.LooksSame(d, morning, morning.AddSeconds(16 * 3_600), Utc));
        // A page last seen 14 days before a quarter past nine moves to Older at a quarter past nine, the same day.
        var edge = Library(Entry("Edge", ((Now - morning).TotalSeconds - 900 + LibraryQuery.OlderAfter.TotalSeconds) / 86_400));
        Assert.True(LibraryQuery.LooksSame(edge, morning, morning.AddSeconds(600), Utc));
        Assert.False(LibraryQuery.LooksSame(edge, morning, morning.AddSeconds(1_200), Utc));
    }

    [Fact]
    public void DoneSortsByWhenItWasMarkedDone()
    {
        // Seen order is Recent, Early; done order is the reverse.
        var d = Library(Entry("Recent", 1, doneDaysAgo: 2), Entry("Early", 3, doneDaysAgo: 1));
        Assert.Equal(new List<string> { "Early", "Recent" }, Titles(LibraryQuery.Items(d, LibrarySection.Done, "", Now)));
    }

    [Fact]
    public void CountsMatchSections()
    {
        Assert.Equal(2, LibraryQuery.Count(Data(), LibrarySection.All, Now));
        Assert.Equal(1, LibraryQuery.Count(Data(), LibrarySection.OfKind(Kind.Shopping), Now));
        Assert.Equal(1, LibraryQuery.Count(Data(), LibrarySection.Older, Now));
        Assert.Equal(1, LibraryQuery.Count(Data(), LibrarySection.Done, Now));
    }

    [Fact]
    public void OneCountingPassMatchesEverySection()
    {
        var data = Data();
        var counts = LibraryQuery.Counts(data, Now);
        foreach (var section in LibrarySection.Ordered)
            Assert.Equal(LibraryQuery.Count(data, section, Now), counts.GetValueOrDefault(section));
    }

    [Fact]
    public void GroupsBySiteMostRecentSiteFirst()
    {
        var items = new[] { Entry("A1", 1, host: "a.com"), Entry("B1", 2, host: "b.com"), Entry("A2", 3, host: "a.com") };
        var groups = LibraryQuery.Groups(items, LibraryGrouping.Site, false, Now);
        Assert.Equal(new List<string> { "a.com", "b.com" }, groups.Select(g => g.Title).ToList());
        Assert.Equal(new List<string> { "A1", "A2" }, Titles(groups[0].Items));
        Assert.Equal(new List<string> { "B1" }, Titles(groups[1].Items));
    }

    [Fact]
    public void GroupsByKindInKindOrder()
    {
        var items = new[] { Entry("Essay", 1, Kind.School), Entry("Desk", 2), Entry("Notes", 3, Kind.School) };
        var groups = LibraryQuery.Groups(items, LibraryGrouping.Kind, false, Now);
        Assert.Equal(new List<string> { "Shopping", "School" }, groups.Select(g => g.Title).ToList());
        Assert.Equal(new List<string> { "Desk" }, Titles(groups[0].Items));
        Assert.Equal(new List<string> { "Essay", "Notes" }, Titles(groups[1].Items));
    }

    [Fact]
    public void ASearchGroupsByStatusWhateverTheGrouping()
    {
        var found = LibraryQuery.Items(Data(), LibrarySection.All, "a", Now);
        foreach (var grouping in Enum.GetValues<LibraryGrouping>())
            Assert.Equal(new List<string> { "Saved", "Done" },
                         LibraryQuery.Groups(found, grouping, true, Now, tz: Utc, culture: EnUs).Select(g => g.Title).ToList());
    }

    /// Saturday 2026-10-24 12:00 UTC, in a fixed zone so headings and dates don't depend on the machine.
    static readonly DateTime Saturday = Unix(1_792_843_200);

    [Fact]
    public void SectionsGroupByDayThenMonth()
    {
        var dates = new[]
        {
            "2026-10-24T11:00:00", "2026-10-23T23:00:00", "2026-10-21T12:00:00", "2026-10-17T00:00:00",
            "2026-10-16T23:59:59", "2026-09-24T00:00:00", "2026-09-09T12:00:00", "2025-12-02T12:00:00",
        };
        var items = dates.Select(date => new Item(Guid.NewGuid(), $"https://a.com/{date}", date, "a.com", Kind.Reading, "a.com/*",
                                                  Browser.Helium, At(date), At(date), 1, ItemStatus.Active)).ToList();
        var groups = LibraryQuery.Groups(items, false, Saturday, tz: Utc, culture: EnUs);
        Assert.Equal(new List<string> { "Today", "Yesterday", "Previous 7 Days", "Previous 30 Days", "September", "December 2025" },
                     groups.Select(g => g.Title).ToList());
        Assert.Equal(new List<string> { dates[0] }, Titles(groups[0].Items));
        Assert.Equal(new List<string> { dates[1] }, Titles(groups[1].Items));
        Assert.Equal(new List<string> { dates[2], dates[3] }, Titles(groups[2].Items));
        Assert.Equal(new List<string> { dates[4], dates[5] }, Titles(groups[3].Items));
        Assert.Equal(new List<string> { dates[6] }, Titles(groups[4].Items));
        Assert.Equal(new List<string> { dates[7] }, Titles(groups[5].Items));
    }

    [Fact]
    public void DoneGroupsByWhenItWasMarkedDone()
    {
        var seenLongAgo = Entry("Chair", 90, doneDaysAgo: 0);
        var done = seenLongAgo with { DoneAt = Saturday.AddSeconds(-3600) };
        Assert.Equal(new List<string> { "Today" }, LibraryQuery.Groups([done], false, Saturday, tz: Utc, culture: EnUs).Select(g => g.Title).ToList());
    }

    [Fact]
    public void SearchGroupsByStatus()
    {
        var found = LibraryQuery.Items(Data(), LibrarySection.All, "a", Now);
        var groups = LibraryQuery.Groups(found, true, Now, tz: Utc, culture: EnUs);
        Assert.Equal(new List<string> { "Saved", "Done" }, groups.Select(g => g.Title).ToList());
        Assert.Equal(new List<string> { "Desk", "Essay", "Lamp" }, Titles(groups[0].Items));
        Assert.Equal(new List<string> { "Chair" }, Titles(groups[1].Items));
        var saved = LibraryQuery.Items(Data(), LibrarySection.All, "desk", Now);
        Assert.Equal(new List<string> { "Saved" }, LibraryQuery.Groups(saved, true, Now, tz: Utc, culture: EnUs).Select(g => g.Title).ToList());
    }

    [Theory]
    [InlineData("2026-10-24T09:41:00", "9:41 AM")]
    [InlineData("2026-10-23T21:05:00", "9:05 PM")]
    [InlineData("2026-10-21T12:00:00", "Wed")]
    [InlineData("2026-10-18T00:00:00", "Sun")]
    [InlineData("2026-10-17T23:00:00", "Oct 17")]   // a week ago would share today's weekday
    [InlineData("2025-12-02T12:00:00", "Dec 2, 2025")]
    public void ShortDatesMatchTheHeadings(string when, string expected)
    {
        Assert.Equal(expected, Text.NormalizeSpaces(LibraryQuery.ShortDate(At(when), Saturday, Utc, EnUs)));
    }

    /// New York moved its clocks forward on 2026-03-08: its days that week are not 24 hours long.
    [Fact]
    public void DaysFollowTheLocalClockAcrossADaylightSavingChange()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var monday = At("2026-03-09T16:00:00");   // 12:00 EDT
        var items = new[]
        {
            Entry("Today", 0) with { LastSeen = At("2026-03-09T15:00:00") },
            Entry("Late last night", 0) with { LastSeen = At("2026-03-09T03:30:00") },   // 23:30 EDT on Sunday
            Entry("Early Sunday", 0) with { LastSeen = At("2026-03-08T05:30:00") },      // 00:30 EST on Sunday
            Entry("Saturday night", 0) with { LastSeen = At("2026-03-08T04:30:00") },    // 23:30 EST on Saturday
        };
        var groups = LibraryQuery.Groups(items, false, monday, tz: newYork, culture: EnUs);
        Assert.Equal(new List<string> { "Today", "Yesterday", "Previous 7 Days" }, groups.Select(g => g.Title).ToList());
        Assert.Equal(new List<string> { "Late last night", "Early Sunday" }, Titles(groups[1].Items));
        Assert.Equal("11:30 PM", Text.NormalizeSpaces(LibraryQuery.ShortDate(At("2026-03-09T03:30:00"), monday, newYork, EnUs)));
        Assert.Equal("Sat", LibraryQuery.ShortDate(At("2026-03-08T04:30:00"), monday, newYork, EnUs));
        Assert.Equal("yesterday", LibraryQuery.Ago(At("2026-03-09T03:30:00"), monday, newYork));
    }
}
