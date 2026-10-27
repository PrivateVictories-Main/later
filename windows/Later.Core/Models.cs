namespace Later.Core;

public enum Browser { Helium, Chrome }

public static class BrowserInfo
{
    public static readonly Browser[] All = [Browser.Helium, Browser.Chrome];

    public static string DisplayName(this Browser b) => b switch { Browser.Helium => "Helium", _ => "Chrome" };

    /// The name written in JSON files.
    public static string Wire(this Browser b) => b switch { Browser.Helium => "helium", _ => "chrome" };

    public static Browser? FromWire(string s) => s switch { "helium" => Browser.Helium, "chrome" => Browser.Chrome, _ => null };

    /// The browser's own product name, as its executable's version info gives it.
    public static Browser? FromProductName(string? productName) => productName switch
    {
        "Google Chrome" => Browser.Chrome,
        "Helium" => Browser.Helium,
        _ => null,
    };
}

public enum Kind { Shopping, Posts, School, Reading, Videos, Tools, Other }

public static class KindInfo
{
    public static readonly Kind[] All = [Kind.Shopping, Kind.Posts, Kind.School, Kind.Reading, Kind.Videos, Kind.Tools, Kind.Other];

    public static string Title(this Kind k) => k switch
    {
        Kind.Shopping => "Shopping",
        Kind.Posts => "Posts",
        Kind.School => "School",
        Kind.Reading => "Reading",
        Kind.Videos => "Videos",
        Kind.Tools => "Tools",
        _ => "Other",
    };

    public static string Wire(this Kind k) => k.Title().ToLowerInvariant();

    /// A kind this build doesn't know (a newer build's) reads as Other, so the list still opens.
    public static Kind FromWire(string s) => All.FirstOrDefault(k => k.Wire() == s, Kind.Other);
}

/// One open tab as the browser reports it. Tabs from private windows never become a `Tab`.
/// `IsActive` is the active tab of its window; `WindowIndex` the window's position, front window first (0); `TabIndex`
/// the place in the window's tab strip from 0; `WindowId` the browser's own id for the window, which ranks windows by age.
public sealed record Tab(string Url, string Title, bool IsActive, int WindowIndex, int TabIndex = 0, long WindowId = 0);

public sealed record Snapshot(Browser Browser, IReadOnlyList<Tab> Tabs);

public sealed record Verdict(bool IsSpecific, Kind Kind, string PageType);

/// The user's correction for one page type. Null fields mean "no correction".
public sealed record Correction(bool? IsSpecific = null, Kind? Kind = null);

public enum ItemStatus { Active, Done }

public sealed record Item(Guid Id, string Url, string Title, string Host, Kind Kind, string PageType, Browser Browser,
    DateTime FirstSaved, DateTime LastSeen, int Visits, ItemStatus Status, DateTime? DoneAt = null, DateTime? OpenedAt = null,
    int? HistoryVisits = null, int? TabIndex = null, int? WindowRank = null, string? HistoryChecked = null)
{
    /// When the page was opened, else when Later first saw it.
    public DateTime Opened => OpenedAt ?? FirstSaved;

    /// The larger of what Later counted and what the browser's history says.
    public int TotalVisits => Math.Max(Visits, HistoryVisits ?? 0);
}

public readonly record struct HistoryResult(DateTime FirstVisit, int VisitCount);

public sealed class LibraryData
{
    public int Version = 1;
    public List<Item> Items = new();
    public Dictionary<string, Correction> Corrections = new();
    /// SHA-256 of page keys of specific pages seen recently → when. Never links or titles.
    public Dictionary<string, DateTime> Seen = new();
    /// Seconds on screen per specific page, newest last, at most 200.
    public List<double> DwellSamples = new();
    /// SHA-256 of page keys the user deleted → when; kept until the page's tab closes.
    public Dictionary<string, DateTime> Dismissed = new();

    /// Items are shared (Item is immutable); the maps and lists are copied.
    public LibraryData Copy() => new()
    {
        Version = Version,
        Items = new List<Item>(Items),
        Corrections = new Dictionary<string, Correction>(Corrections),
        Seen = new Dictionary<string, DateTime>(Seen),
        DwellSamples = new List<double>(DwellSamples),
        Dismissed = new Dictionary<string, DateTime>(Dismissed),
    };
}
