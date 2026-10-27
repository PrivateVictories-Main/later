using Xunit;
using static Later.Core.Tests.Fixtures;

namespace Later.Core.Tests;

/// How many tabs hold each page, across every watched browser.
public class OpenCopiesTests
{
    const string WwwLink = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    const string BareLink = "https://youtube.com/watch?v=dQw4w9WgXcQ&t=5s";

    static Harness BothBrowsers() => new(watched: new HashSet<Browser> { Browser.Helium, Browser.Chrome });

    static string KeyOf(string link) => LinkCleaner.PageKey(LinkCleaner.Clean(link)!);

    [Fact]
    public void CountsCopiesAcrossTabsAndBrowsers()
    {
        var h = BothBrowsers();
        h.Read(Browser.Helium, T(WwwLink, "Video", active: true), T(BareLink, "Video"), T(ProductA));
        h.Read(Browser.Chrome, T(WwwLink, "Video", active: true));
        Assert.Equal(3, h.Judge.OpenCopies[KeyOf(WwwLink)]);
        Assert.Equal(1, h.Judge.OpenCopies[KeyOf(ProductA)]);
    }

    [Fact]
    public void ClosingTabsAndQuittingLowerTheCount()
    {
        var h = BothBrowsers();
        h.Read(Browser.Helium, T(WwwLink, "Video", active: true), T(BareLink, "Video"));
        h.Read(Browser.Chrome, T(WwwLink, "Video", active: true));
        var video = KeyOf(WwwLink);
        h.Read(Browser.Helium, T(WwwLink, "Video", active: true));
        Assert.Equal(2, h.Judge.OpenCopies[video]);
        h.Judge.BrowserQuit(Browser.Chrome, h.Now);
        Assert.Equal(1, h.Judge.OpenCopies[video]);
        h.Read(Browser.Helium);
        Assert.Empty(h.Judge.OpenCopies);
    }

    [Fact]
    public void APageOpenAsWwwAndBareIsOneItem()
    {
        var h = new Harness();
        h.Show(T("https://example.com/", "Home", active: true), T(WwwLink, "Video"), T(BareLink, "Video"));
        h.Wait(61); h.Show(T("https://example.com/", "Home", active: true), T(WwwLink, "Video"), T(BareLink, "Video"));
        Assert.Single(h.Library.Data.Items, i => i.Host == "youtube.com");
    }

    [Fact]
    public void APageDeletedAsWwwStaysDeletedWhenItsTabIsOpenAsBare()
    {
        var h = new Harness();
        h.Show(T("https://example.com/", "Home", active: true), T(WwwLink, "Video"));
        h.Wait(61); h.Show(T("https://example.com/", "Home", active: true), T(WwwLink, "Video"));
        h.Library.Delete(h.Library.Data.Items.First(i => i.Host == "youtube.com").Id, h.Now);
        h.Wait(300); h.Show(T("https://example.com/", "Home", active: true), T(BareLink, "Video"));
        h.Wait(300); h.Show(T("https://example.com/", "Home", active: true), T(BareLink, "Video"));
        Assert.DoesNotContain(h.Library.Data.Items, i => i.Host == "youtube.com");
    }
}
