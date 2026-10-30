using Later.App.Actions;
using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Engine;
using Xunit;

namespace Later.Windows.Tests;

/// Opening a saved link: web links only, as one argument of the browser's own process, never through a shell.
public sealed class OpenerTests
{
    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/")]
    public void WebLinksOpen(string url) => Assert.True(Opener.IsWebLink(url));

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("mailto:a@example.com")]
    [InlineData("ftp://example.com/x")]
    [InlineData("data:text/html,hi")]
    [InlineData("calc.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("https://")]
    [InlineData("")]
    public void NothingElseEverOpens(string url)
    {
        Assert.False(Opener.IsWebLink(url));
        Assert.Null(Opener.Plan(url, @"C:\Program Files\browser\chrome.exe"));
        Assert.False(Opener.Open(url, Browser.Chrome));
        Assert.False(Opener.Open(url, (Browser?)null));
    }

    [Fact]
    public void TheLinkIsOneArgumentOfTheBrowsersOwnProcessAndNoShellIsInvolved()
    {
        var url = "https://example.com/search?q=a b&x=\"quoted\" & calc.exe";
        var start = Opener.Plan(url, @"C:\Program Files\browser\chrome.exe")!;
        Assert.False(start.UseShellExecute);
        Assert.Equal(@"C:\Program Files\browser\chrome.exe", start.FileName);
        Assert.Equal([url], start.ArgumentList.ToArray());   // one entry, exactly as it was: nothing split it at a space or an ampersand
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void ARunningBrowserBeatsAnInstalledOneAndThePreferredOneComesFirst()
    {
        var running = new HashSet<Browser> { Browser.Chrome };
        Assert.Equal(Browser.Chrome, Opener.BrowserFor(Browser.Helium, running.Contains, _ => true));   // Helium is preferred but not running
        running.Add(Browser.Helium);
        Assert.Equal(Browser.Helium, Opener.BrowserFor(Browser.Helium, running.Contains, _ => true));
        Assert.Equal(Browser.Chrome, Opener.BrowserFor(Browser.Chrome, running.Contains, _ => true));
    }

    [Fact]
    public void WithNoneRunningThePreferredInstalledOneThenHeliumThenChrome()
    {
        Assert.Equal(Browser.Chrome, Opener.BrowserFor(Browser.Chrome, _ => false, _ => true));
        Assert.Equal(Browser.Helium, Opener.BrowserFor(null, _ => false, _ => true));
        Assert.Equal(Browser.Chrome, Opener.BrowserFor(null, _ => false, b => b == Browser.Chrome));
        Assert.Null(Opener.BrowserFor(Browser.Helium, _ => false, _ => false));
    }

    static Browser? OpenIn(Browser? last, BrowserStatus chrome, BrowserStatus helium) =>
        new EngineState(new Dictionary<Browser, BrowserStatus> { [Browser.Chrome] = chrome, [Browser.Helium] = helium }, false, null, true, true, last).OpenIn;

    [Fact]
    public void TheOpenLabelFollowsTheMacsRuleFromTheEnginesStatusesAndNeverFromTheMachinesProcesses()
    {
        const BrowserStatus Up = BrowserStatus.Watching, Idle = BrowserStatus.NotRunning, Gone = BrowserStatus.NotInstalled;
        Assert.Equal(Browser.Chrome, OpenIn(Browser.Chrome, Up, Up));            // the one last in front, when it runs
        Assert.Equal(Browser.Helium, OpenIn(Browser.Helium, Up, Up));
        Assert.Equal(Browser.Helium, OpenIn(Browser.Chrome, Idle, Up));          // else any running one
        Assert.Equal(Browser.Chrome, OpenIn(null, Up, Idle));
        Assert.Equal(Browser.Helium, OpenIn(Browser.Helium, Up, BrowserStatus.ReadFailed));   // a browser that runs but could not be read still runs
        Assert.Equal(Browser.Chrome, OpenIn(Browser.Chrome, Idle, Idle));        // none runs: the last one, if installed
        Assert.Equal(Browser.Helium, OpenIn(null, Idle, Idle));                  // else the first installed, Helium first
        Assert.Equal(Browser.Chrome, OpenIn(null, Idle, Gone));
        Assert.Null(OpenIn(null, Gone, Gone));
        Assert.Null(EngineState.None.OpenIn);                                    // no engine: "Open"
    }

    [Fact]
    public void TheOpenClickAndTheLabelNameTheSameBrowserEvenWhenTheStatusesStandStill()
    {
        // Watching is paused: Chrome was in front and its status froze at Watching, while Helium is the one that runs now.
        var frozen = new EngineState(new Dictionary<Browser, BrowserStatus> { [Browser.Chrome] = BrowserStatus.Watching, [Browser.Helium] = BrowserStatus.NotRunning }, true, null, true, true, Browser.Chrome);
        Assert.Equal(Browser.Chrome, frozen.OpenIn);                  // what the label says
        Assert.Equal(frozen.OpenIn, frozen.Target());                 // where the click and the "open N pages?" question go
        Assert.Equal(Browser.Helium, frozen.Target(Browser.Helium));  // a browser the user picked from the menu wins
        Assert.Null(EngineState.None.Target());                       // no engine to ask: the machine's own browsers are looked at
    }
}
