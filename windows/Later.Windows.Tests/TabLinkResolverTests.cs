using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class TabLinkResolverTests
{
    static RawTab Tab(string id, string title, bool selected = false, int index = 0) => new(id, title, selected, index);
    static SessionTab Saved(string url, string title) => new(1, 0, url, title);

    [Fact]
    public void RecallWorksWhileTheTitleMatchesAndNotAfterItChanges()
    {
        var r = new TabLinkResolver();
        r.Remember(Tab("1", "Page", true), "https://a.example/p");
        Assert.Equal("https://a.example/p", r.Recall(Tab("1", "Page")));
        Assert.Null(r.Recall(Tab("1", "Page changed")));
        Assert.Null(r.Recall(Tab("2", "Page")));
    }

    [Fact]
    public void TwoTabsWithOneTitleStayUnresolved()
    {
        var tabs = new[] { Tab("1", "Discussion"), Tab("2", "Discussion"), Tab("3", "Other") };
        var ambiguous = TabLinkResolver.AmbiguousTitles(tabs);
        Assert.Contains("Discussion", ambiguous);
        Assert.DoesNotContain("Other", ambiguous);
        var session = new[] { Saved("https://a.example/1", "Discussion"), Saved("https://a.example/3", "Other") };
        var history = new Dictionary<string, string> { ["Discussion"] = "https://a.example/1", ["Other"] = "https://a.example/3" };
        Assert.Null(TabLinkResolver.FromSession("Discussion", session, ambiguous));
        Assert.Null(TabLinkResolver.FromHistory("Discussion", history, ambiguous));
        Assert.Equal("https://a.example/3", TabLinkResolver.FromSession("Other", session, ambiguous));
        Assert.Equal("https://a.example/3", TabLinkResolver.FromHistory("Other", history, ambiguous));
    }

    [Fact]
    public void ASessionTitleWithTwoDistinctUrlsIsNotResolved()
    {
        var none = new HashSet<string>();
        var session = new[] { Saved("https://a.example/1", "Same"), Saved("https://a.example/2", "Same") };
        Assert.Null(TabLinkResolver.FromSession("Same", session, none));
        var twice = new[] { Saved("https://a.example/1", "Same"), Saved("https://a.example/1", "Same") };
        Assert.Equal("https://a.example/1", TabLinkResolver.FromSession("Same", twice, none));
    }

    [Fact]
    public void SessionTitlesWithAMemoryUsageSuffixStillMatch()
    {
        var session = new[] { Saved("https://a.example/1", "Page - Memory usage - 85 MB") };
        Assert.Equal("https://a.example/1", TabLinkResolver.FromSession("Page", session, new HashSet<string>()));
    }

    [Fact]
    public void AnUnloadedRestoredTabNamedByItsAddressMatchesTheSessionUrl()
    {
        var none = new HashSet<string>();
        var session = new[] { Saved("http://a.example/p/2", ""), Saved("https://b.example/", "") };
        Assert.Equal("http://a.example/p/2", TabLinkResolver.FromSession("a.example/p/2", session, none));
        Assert.Equal("https://b.example/", TabLinkResolver.FromSession("b.example", session, none));
        Assert.Null(TabLinkResolver.FromSession("a.example/p/3", session, none));
        Assert.Null(TabLinkResolver.FromSession("", session, none));
    }

    [Fact]
    public void PruneDropsClosedTabsAndForgetDropsAll()
    {
        var r = new TabLinkResolver();
        r.Remember(Tab("1", "A"), "https://a.example/1");
        r.Remember(Tab("2", "B"), "https://a.example/2");
        r.Prune(new HashSet<string> { "2" });
        Assert.Null(r.Recall(Tab("1", "A")));
        Assert.Equal("https://a.example/2", r.Recall(Tab("2", "B")));
        r.Forget();
        Assert.Null(r.Recall(Tab("2", "B")));
    }

    [Fact]
    public void AGuessIsGivenUpWhenItsTitleBecomesAmbiguousAndAnExactLinkIsKept()
    {
        var r = new TabLinkResolver();
        r.Remember(Tab("1", "Page"), "https://a.example/guess", exact: false);
        r.Remember(Tab("2", "Page"), "https://a.example/exact");   // the selected tab's address
        var shared = new HashSet<string> { "Page" };
        Assert.Equal("https://a.example/guess", r.Recall(Tab("1", "Page"), []));
        Assert.Null(r.Recall(Tab("1", "Page"), shared));
        Assert.Equal("https://a.example/exact", r.Recall(Tab("2", "Page"), shared));
        r.Remember(Tab("1", "Page"), "https://a.example/seen", exact: true);   // an exact sighting overrides the guess
        Assert.Equal("https://a.example/seen", r.Recall(Tab("1", "Page"), shared));
    }
}
