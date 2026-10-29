using System.Diagnostics;
using Later.Core;
using Later.Windows.Reader;
using Later.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Later.Windows.Tests.Integration;

/// Diagnostic only: prints what a browser shows to UI Automation (private-window markers, minimized behavior, name shapes).
/// Everything goes to the test output with a PROBE prefix.
[Trait("Category", "Probe")]
public class ProbeDump(ITestOutputHelper output)
{
    void P(string line) => output.WriteLine("PROBE " + line);

    [Theory]
    [MemberData(nameof(Browsers))]
    public void Dump(Browser browser)
    {
        var exe = ItSupport.ExeOf(browser);
        P($"=== {browser} exe={exe}");
        var info = FileVersionInfo.GetVersionInfo(exe);
        P($"{browser} version ProductName='{info.ProductName}' FileDescription='{info.FileDescription}' Version={info.FileVersion} CompanyName='{info.CompanyName}'");
        using var server = new FixtureServer();
        using var normal = BrowserFixture.Start(exe, server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        bool appeared = ItSupport.WaitUntil(() => ItSupport.WindowsOf(browser).Any(w => w.Title.Contains("Fixture", StringComparison.Ordinal)), TimeSpan.FromSeconds(40));
        P($"normal window appeared={appeared}");
        AllChromeWindows();
        using var incognito = BrowserFixture.Start(exe, server, [FixtureServer.Url(9)], incognito: true, profile: normal.Profile);
        bool two = ItSupport.WaitUntil(() => ItSupport.WindowsOf(browser).Count >= 2, TimeSpan.FromSeconds(40));
        P($"second window appeared={two}");
        P($"private page loaded={ItSupport.WaitUntil(() => ItSupport.WindowsOf(browser).Any(w => w.Title.StartsWith("Fixture 9", StringComparison.Ordinal)), TimeSpan.FromSeconds(20))}");
        Thread.Sleep(5000);   // let titles, tab strips and the accessibility tree settle
        AllChromeWindows();
        using var uia = new UiaTest();
        using var source = new UiaSource();
        var foreground = BrowserDiscovery.FrontBrowser();
        P($"foreground browser={foreground}");

        foreach (var w in ItSupport.WindowsOf(browser))
        {
            P($"--- window hwnd={w.Hwnd} z={w.ZIndex} minimized={w.IsMinimized} pid={w.Pid} title='{w.Title}'");
            P($"top-level UIA Name='{uia.RootName(w.Hwnd)}'");
            foreach (var line in uia.Dump(w.Hwnd, 8, 300)) P("  " + line);
            foreach (var t in uia.Tabs(w.Hwnd)) P($"  tab[{t.Index}] selected={t.Selected} class='{t.ClassName}' id='{t.AutomationId}' name='{t.Name}'");
            var raw = source.ReadWindow(w);
            P($"product read: private={raw?.IsPrivate} tabs={raw?.Tabs.Count} activeUrl='{raw?.ActiveUrl}'" + (raw is null ? " (unreadable)" : ""));
            if (raw is not null) foreach (var t in raw.Tabs) P($"  raw tab[{t.Index}] selected={t.IsSelected} id={t.RuntimeId} title='{t.Title}'");
        }

        // Minimized behavior: the normal window (the one with three tabs).
        var normalWindow = ItSupport.WindowsOf(browser).FirstOrDefault(w => uia.Tabs(w.Hwnd).Count >= 3);
        if (normalWindow is not null)
        {
            _ = NativeTest.ShowWindow(normalWindow.Hwnd, NativeTest.SwMinimize);
            Thread.Sleep(2000);
            var min = ItSupport.WindowsOf(browser).FirstOrDefault(w => w.Hwnd == normalWindow.Hwnd);
            P($"after minimize: listed={min is not null} minimized={min?.IsMinimized}");
            if (min is not null)
            {
                var raw = source.ReadWindow(min);
                P($"after minimize: product read tabs={raw?.Tabs.Count} activeUrl='{raw?.ActiveUrl}'" + (raw is null ? " (unreadable)" : ""));
                P($"after minimize: live UIA tab count={uia.Tabs(min.Hwnd).Count}");
            }
            _ = NativeTest.ShowWindow(normalWindow.Hwnd, NativeTest.SwRestore);
            Thread.Sleep(1500);

            // Tab name shapes over time, after switching tabs (the browser may add suffixes to a name).
            for (int i = 0; i < 3; i++)
            {
                uia.SelectTab(normalWindow.Hwnd, i);
                Thread.Sleep(1500);
                P($"after select {i}: " + string.Join(" | ", uia.Tabs(normalWindow.Hwnd).Select(t => $"{(t.Selected ? "*" : "")}'{t.Name}'")));
                var raw = source.ReadWindow(normalWindow);
                P($"after select {i}: activeUrl='{raw?.ActiveUrl}' title='{ItSupport.WindowsOf(browser).FirstOrDefault(x => x.Hwnd == normalWindow.Hwnd)?.Title}'");
            }
            Thread.Sleep(8000);
            P("after 8 s idle: " + string.Join(" | ", uia.Tabs(normalWindow.Hwnd).Select(t => $"{(t.Selected ? "*" : "")}'{t.Name}'")));
        }
        P($"=== end {browser}");
    }

    /// Every top-level window of class Chrome_WidgetWin_1, listed or not, with why the product may skip it.
    void AllChromeWindows()
    {
        Native.User32.EnumWindows((hwnd, lParam) =>
        {
            if (Native.User32.Class(hwnd) == "Chrome_WidgetWin_1")
            {
                _ = Native.User32.GetWindowThreadProcessId(hwnd, out var pid);
                P($"any Chrome_WidgetWin_1 hwnd={hwnd} visible={Native.User32.IsWindowVisible(hwnd)} owner={Native.User32.GetWindow(hwnd, Native.User32.GwOwner)} exstyle=0x{Native.User32.GetWindowLong(hwnd, Native.User32.GwlExStyle):x} pid={pid} title='{Native.User32.Title(hwnd)}' exe={Native.User32.ImagePath(pid)}");
            }
            return 1;
        }, 0);
    }

    public static IEnumerable<object[]> Browsers() => ItSupport.Browsers();
}
