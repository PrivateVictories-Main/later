using Later.Core;
using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class BrowserReaderTests
{
    sealed class FakeUia : IUiaSource
    {
        public readonly Dictionary<nint, RawWindow?> Windows = new();
        public RawWindow? ReadWindow(BrowserWindow w) => Windows.TryGetValue(w.Hwnd, out var r) ? r : null;
    }

    sealed class FakeHistory : IHistorySource
    {
        public int Calls;
        public readonly Dictionary<string, string> Links = new(StringComparer.Ordinal);
        public Dictionary<string, string> LinksForTitles(Browser b, IReadOnlyCollection<string> titles, DateTime since)
        {
            Calls++;
            return Links.Where(kv => titles.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
    }

    sealed class FakeSession : ISessionSource
    {
        public int Calls;
        public List<SessionTab> Tabs = [];
        public IReadOnlyList<SessionTab> Read(Browser b) { Calls++; return Tabs; }
    }

    sealed class Rig
    {
        public readonly FakeUia Uia = new(); public readonly FakeHistory History = new(); public readonly FakeSession Session = new();
        public List<BrowserWindow> Windows = [];
        public BrowserReader Reader => reader ??= new BrowserReader(Uia, History, Session, () => Windows, () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        BrowserReader? reader;

        public static BrowserWindow Win(nint hwnd, int z, bool minimized = false) => new(hwnd, 1, Browser.Chrome, "Window " + hwnd, z, minimized);
        public static RawTab Tab(string id, string title, bool selected = false, int index = 0) => new(id, title, selected, index);
        public static RawWindow Raw(nint hwnd, string? active, params RawTab[] tabs) => new(hwnd, false, tabs, active);
    }

    [Fact]
    public void WindowsAreNumberedByFirstSightAndKeepTheirNumber()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(100, 0), Rig.Win(200, 1)];
        r.Uia.Windows[100] = Rig.Raw(100, "https://a.example/1", Rig.Tab("a", "A", true));
        r.Uia.Windows[200] = Rig.Raw(200, "https://a.example/2", Rig.Tab("b", "B", true));
        var first = r.Reader.Read(Browser.Chrome).Snapshot!.Tabs;
        Assert.Equal(2, first.Single(t => t.Title == "A").WindowId);   // the first read numbers the window lowest in z-order first
        Assert.Equal(1, first.Single(t => t.Title == "B").WindowId);
        Assert.Equal(0, first.Single(t => t.Title == "A").WindowIndex);
        r.Windows = [Rig.Win(300, 0), Rig.Win(100, 1), Rig.Win(200, 2)];
        r.Uia.Windows[300] = Rig.Raw(300, "https://a.example/3", Rig.Tab("c", "C", true));
        var second = r.Reader.Read(Browser.Chrome).Snapshot!.Tabs;
        Assert.Equal(3, second.Single(t => t.Title == "C").WindowId);
        Assert.Equal(2, second.Single(t => t.Title == "A").WindowId);
        Assert.Equal(1, second.Single(t => t.Title == "B").WindowId);
        Assert.Equal(2, second.Single(t => t.Title == "B").WindowIndex);
        Assert.Equal([100, 200, 300], r.Reader.KnownWindows(Browser.Chrome).Order().ToArray());
    }

    [Fact]
    public void APrivateWindowsTabsNeverAppearButItsPositionCounts()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0), Rig.Win(2, 1)];
        r.Uia.Windows[1] = new RawWindow(1, true, [Rig.Tab("p", "Secret", true)], "https://private.example/secret");
        r.Uia.Windows[2] = Rig.Raw(2, "https://a.example/1", Rig.Tab("a", "A", true));
        var result = r.Reader.Read(Browser.Chrome);
        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Equal(1, result.PrivateWindowsSkipped);
        var tab = Assert.Single(result.Snapshot!.Tabs);
        Assert.Equal("A", tab.Title);
        Assert.Equal(1, tab.WindowIndex);
        Assert.Equal(1, result.WindowsRead);
    }

    [Fact]
    public void OnlyPrivateWindowsGiveAnEmptySnapshot()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0)];
        r.Uia.Windows[1] = new RawWindow(1, true, [], null);
        var result = r.Reader.Read(Browser.Chrome);
        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Empty(result.Snapshot!.Tabs);
        Assert.Equal(1, result.PrivateWindowsSkipped);
    }

    [Fact]
    public void NoWindowsMeansNotRunning()
    {
        var r = new Rig();
        Assert.Equal(ReadStatus.NotRunning, r.Reader.Read(Browser.Chrome).Status);
        Assert.Equal(ReadStatus.NotRunning, r.Reader.Read(Browser.Helium).Status);
    }

    [Fact]
    public void AMinimizedWindowWithNoTabsIsTransientAndAVisibleOneIsIgnored()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0), Rig.Win(2, 1, minimized: true)];
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/1", Rig.Tab("a", "A", true));
        r.Uia.Windows[2] = Rig.Raw(2, null);
        Assert.Equal(ReadStatus.Transient, r.Reader.Read(Browser.Chrome).Status);
        r.Windows = [Rig.Win(1, 0), Rig.Win(2, 1)];
        var result = r.Reader.Read(Browser.Chrome);
        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Single(result.Snapshot!.Tabs);
        Assert.Equal(1, result.WindowsRead);
    }

    [Fact]
    public void AWindowThatCouldNotBeReadIsTransient()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0), Rig.Win(2, 1)];
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/1", Rig.Tab("a", "A", true));
        r.Uia.Windows[2] = null;
        var result = r.Reader.Read(Browser.Chrome);
        Assert.Equal(ReadStatus.Transient, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void UnresolvedTabsAreOmittedAndTheOthersKeepTheirIndex()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0)];
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/1", Rig.Tab("a", "A", true, 0), Rig.Tab("b", "B", false, 1), Rig.Tab("c", "C", false, 2));
        r.History.Links["C"] = "https://a.example/3";
        var result = r.Reader.Read(Browser.Chrome);
        Assert.Equal(1, result.UnresolvedTabs);
        Assert.Equal([("A", 0, "https://a.example/1"), ("C", 2, "https://a.example/3")], result.Snapshot!.Tabs.Select(t => (t.Title, t.TabIndex, t.Url)).ToArray());
    }

    [Fact]
    public void TwoOpenTabsWithOneTitleStayUnresolvedEvenWhenTheSourcesWouldAnswer()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0)];
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/other", Rig.Tab("a", "Other", true, 0), Rig.Tab("b", "Same", false, 1), Rig.Tab("c", "Same", false, 2));
        r.History.Links["Same"] = "https://a.example/same";
        r.Session.Tabs = [new SessionTab(1, 1, "https://a.example/same", "Same")];
        var result = r.Reader.Read(Browser.Chrome);
        Assert.Equal(2, result.UnresolvedTabs);
        Assert.Equal(["Other"], result.Snapshot!.Tabs.Select(t => t.Title).ToArray());
    }

    [Fact]
    public void ASecondReadOfAnUnchangedStripAsksNeitherHistoryNorTheSession()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0)];
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/1", Rig.Tab("a", "A", true, 0), Rig.Tab("b", "B", false, 1), Rig.Tab("c", "C", false, 2));
        r.Session.Tabs = [new SessionTab(1, 1, "https://a.example/2", "B")];
        r.History.Links["C"] = "https://a.example/3";
        Assert.Equal(0, r.Reader.Read(Browser.Chrome).UnresolvedTabs);
        Assert.Equal((1, 1), (r.Session.Calls, r.History.Calls));
        var again = r.Reader.Read(Browser.Chrome);
        Assert.Equal(3, again.Snapshot!.Tabs.Count);
        Assert.Equal((1, 1), (r.Session.Calls, r.History.Calls));
    }

    [Fact]
    public void ATitleChangeOnABackgroundTabInvalidatesItsRememberedLink()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0)];
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/1", Rig.Tab("a", "A", true, 0), Rig.Tab("b", "B", false, 1));
        r.History.Links["B"] = "https://a.example/2";
        Assert.Equal(2, r.Reader.Read(Browser.Chrome).Snapshot!.Tabs.Count);
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/1", Rig.Tab("a", "A", true, 0), Rig.Tab("b", "B renamed", false, 1));
        var result = r.Reader.Read(Browser.Chrome);
        Assert.Equal(1, result.UnresolvedTabs);
        Assert.Equal(["A"], result.Snapshot!.Tabs.Select(t => t.Title).ToArray());
    }

    [Fact]
    public void BrowserQuitForgetsLinksAndTheCachedSession()
    {
        var r = new Rig();
        r.Windows = [Rig.Win(1, 0)];
        r.Uia.Windows[1] = Rig.Raw(1, "https://a.example/1", Rig.Tab("a", "A", true, 0), Rig.Tab("b", "B", false, 1));
        r.Session.Tabs = [new SessionTab(1, 1, "https://a.example/2", "B")];
        r.Reader.Read(Browser.Chrome);
        Assert.Equal(1, r.Session.Calls);
        r.Reader.BrowserQuit(Browser.Chrome);
        Assert.Empty(r.Reader.KnownWindows(Browser.Chrome));
        r.Reader.Read(Browser.Chrome);
        Assert.Equal(2, r.Session.Calls);
    }
}
