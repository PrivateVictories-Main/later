using System.Globalization;
using Later.Core;

namespace Later.App.ViewModels;

public enum InspectorMode { Overview, Page, Pages }

public sealed record Fact(string Label, string Value)
{
    /// What assistive technology reads for the row.
    public string Name => $"{Label}, {Value}";
}

/// How many of a kind are on the list, for the overview. The name is what assistive technology reads.
public sealed record KindCount(Kind Kind, int Count)
{
    public string Title => Kind.Title();
    public string CountText => Count.ToString(CultureInfo.InvariantCulture);
    public string Name => $"{Title}, {CountText}";
}

/// A site and how many pages it has on the list.
public sealed record SiteCount(string Host, int Count)
{
    public string CountText => Count.ToString(CultureInfo.InvariantCulture);
    public string Name => $"{Host}, {CountText}";
}

/// What the right pane says and offers, from the selection: nothing selected is an overview of the list, one page is its details
/// and what can be done with it, several are a summary with the same actions for all. Plain data, so the words are tested.
public sealed class InspectorModel
{
    InspectorModel(InspectorMode mode) => Mode = mode;

    public InspectorMode Mode { get; }
    public bool CanEdit { get; private init; }

    // Overview
    public string Caption { get; private init; } = "";
    public IReadOnlyList<KindCount> Kinds { get; private init; } = [];
    public IReadOnlyList<SiteCount> Sites { get; private init; } = [];
    /// A kind or a site takes the user to its pages, except in a search, whose results the rows only describe.
    public bool Navigates { get; private init; }

    // One page
    public Item? Item { get; private init; }
    public string Title { get; private init; } = "";
    public string Link { get; private init; } = "";
    public string Why { get; private init; } = "";
    public int Copies { get; private init; }
    public IReadOnlyList<Fact> Facts { get; private init; } = [];

    // The actions
    public IReadOnlyList<Item> Items { get; private init; } = [];
    public bool AllDone { get; private init; }
    /// The Done button is not offered for a regular that is still in the list: it has its own choice.
    public bool ShowsDone { get; private init; }
    public string DoneLabel => AllDone ? "Move Back" : "Mark Done";
    /// "Keep Pages Like This" for a regular, "This Is a Regular" for a place that is kept; none for a page that is a thing.
    public string? CorrectionLabel { get; private init; }
    public bool CorrectionKeeps => Item?.Regular == true;
    public string OpenLabel { get; private init; } = "Open";
    public string CopyLabel { get; private init; } = "Copy Link";
    public string NotAnIdeaLabel { get; private init; } = "Not an Idea";
    public string DeleteLabel { get; private init; } = "Delete";

    // Several pages
    public string Heading { get; private init; } = "";
    public string SiteSummary { get; private init; } = "";

    public static InspectorModel For(LibraryViewModel model, string? openIn = null) =>
        For(model.Current, model.SelectedItems, model.Searching, model.Query, model.Section, model.CanEdit, model.Now, model.Why, openIn);

    public static InspectorModel For(Listing listing, IReadOnlyList<Item> selected, bool searching, string query, LibrarySection section, bool canEdit,
                                     DateTime now, Func<Item, string> why, string? openIn = null, TimeZoneInfo? tz = null, CultureInfo? culture = null)
    {
        switch (selected.Count)
        {
            case 0: return Overview(listing, searching, query, section, canEdit);
            case 1:
                var item = selected[0];
                int copies = listing.Copies.GetValueOrDefault(item.Id);
                return new InspectorModel(InspectorMode.Page)
                {
                    CanEdit = canEdit, Item = item, Items = selected, Title = LibraryListing.DisplayTitle(item), Link = LibraryListing.Readable(item.Url),
                    Why = why(item), Copies = copies, Facts = BuildFacts(item, copies, now, tz, culture), AllDone = item.Status == ItemStatus.Done,
                    ShowsDone = !item.IsActiveRegular,
                    CorrectionLabel = item.Regular switch { true => "Keep Pages Like This", false => "This Is a Regular", null => null },
                    OpenLabel = openIn is null ? "Open" : "Open in " + openIn,
                };
            default:
                int sites = selected.Select(i => i.Host).Distinct(StringComparer.Ordinal).Count();
                string n = selected.Count.ToString(CultureInfo.InvariantCulture);
                return new InspectorModel(InspectorMode.Pages)
                {
                    CanEdit = canEdit, Items = selected, Heading = n + " Pages", SiteSummary = sites == 1 ? "From 1 site" : "From " + sites.ToString(CultureInfo.InvariantCulture) + " sites",
                    AllDone = selected.All(i => i.Status == ItemStatus.Done), ShowsDone = true,
                    OpenLabel = "Open " + n + " Pages", CopyLabel = "Copy Links", NotAnIdeaLabel = "Not Ideas", DeleteLabel = "Delete " + n,
                };
        }
    }

    static InspectorModel Overview(Listing listing, bool searching, string query, LibrarySection section, bool canEdit)
    {
        var items = listing.Items;
        var byKind = items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        // Only kinds that have pages, and no breakdown when there are none or the section already is one kind.
        var kinds = items.Count > 0 && (searching || section.Kind != SectionKind.OfKind)
            ? KindInfo.All.Where(byKind.ContainsKey).Select(k => new KindCount(k, byKind[k])).ToList() : [];
        // Only once some site has more than one page here; until then it would just list every page's site.
        var sites = LibraryListing.TopSites(items);
        return new InspectorModel(InspectorMode.Overview)
        {
            CanEdit = canEdit, Caption = LibraryListing.OverviewCaption(searching, items.Count == 0, query), Kinds = kinds,
            Sites = sites.Any(s => s.Count > 1) ? sites.Select(s => new SiteCount(s.Host, s.Count)).ToList() : [], Navigates = !searching,
        };
    }

    /// The Mac's details card: when it was opened (or first seen), saved if that differs, last seen, done, visits, open now, where from.
    static List<Fact> BuildFacts(Item item, int copies, DateTime now, TimeZoneInfo? tz, CultureInfo? culture)
    {
        string Stamp(DateTime d) => LibraryListing.Stamp(d, now, tz, culture);
        var facts = new List<Fact> { new("Kind", item.Kind.Title()), new("Site", item.Host) };
        facts.Add(new(item.OpenedAt is null ? "First seen" : "Opened", Stamp(item.Opened)));
        if (item.OpenedAt is not null && Math.Abs((item.Opened - item.FirstSaved).TotalSeconds) > 60) facts.Add(new("Saved", Stamp(item.FirstSaved)));
        facts.Add(new("Last seen", Stamp(item.LastSeen)));
        if (item.DoneAt is { } doneAt) facts.Add(new("Done", Stamp(doneAt)));
        facts.Add(new("Visits", item.TotalVisits == 1 ? "Once" : item.TotalVisits.ToString(CultureInfo.InvariantCulture) + " times"));
        if (copies > 0) facts.Add(new("Open now", copies == 1 ? "In 1 tab" : "In " + copies.ToString(CultureInfo.InvariantCulture) + " tabs"));
        facts.Add(new("From", item.Browser.DisplayName()));
        return facts;
    }
}
