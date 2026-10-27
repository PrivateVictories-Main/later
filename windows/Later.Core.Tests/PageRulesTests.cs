using Xunit;

namespace Later.Core.Tests;

/// The table-driven page cases live in shared/vectors (see VectorTests); what stays here compares page types.
public class PageRulesTests
{
    [Fact]
    public void HashRoutesAreJudgedByTheirRoute()
    {
        var item = LinkCleaner.Clean("https://app.example.com/#/items/9876543")!;
        var itemVerdict = PageRules.Judge(item, "Walnut desk");
        Assert.True(itemVerdict.IsSpecific);
        Assert.Equal("app.example.com/#/items/*", itemVerdict.PageType);
        var dashboard = LinkCleaner.Clean("https://app.example.com/#/dashboard")!;
        var dashboardVerdict = PageRules.Judge(dashboard, "Dashboard");
        Assert.True(dashboardVerdict.IsSpecific);   // a plain route is kept too, under a page type of its own
        Assert.NotEqual(itemVerdict.PageType, dashboardVerdict.PageType);
    }

    public static TheoryData<string, string> RouteMarkers()
    {
        var data = new TheoryData<string, string>();
        foreach (var marker in new List<string> { "#/", "#!/", "#%2F", "#%21/" })
            foreach (var query in new List<string> { "", "?returnTo=%2Fsearch%3Fq%3Ddesk" })
                data.Add(marker, query);
        return data;
    }

    /// A correction on the item type must not touch the dashboard's verdict, whichever way the route is written.
    [Theory]
    [MemberData(nameof(RouteMarkers))]
    public void RouteMarkersKeepItemsAndDashboardsApart(string marker, string query)
    {
        var item = LinkCleaner.Clean($"https://app.example.com/{marker}items/ABCDEFG1{query}")!;
        var dashboard = LinkCleaner.Clean($"https://app.example.com/{marker}dashboard{query}")!;
        var itemVerdict = PageRules.Judge(item, "Walnut desk organizer");
        var dashboardVerdict = PageRules.Judge(dashboard, "Dashboard");
        Assert.NotEqual(itemVerdict.PageType, dashboardVerdict.PageType);
        var corrections = new Dictionary<string, Correction> { ["app.example.com/#/items/*"] = new Correction(IsSpecific: false) };
        Assert.Equal(dashboardVerdict, PageRules.Judge(dashboard, "Dashboard", corrections));
    }
}
