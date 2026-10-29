using System.Diagnostics;
using Later.Core;
using Later.Windows.Reader;
using Xunit;
using Xunit.Abstractions;
using static Later.Windows.Tests.Integration.ItSupport;

namespace Later.Windows.Tests.Integration;

/// The reader against real, headed Chrome and Helium on throwaway profiles, pointed at local pages. Facts run one at a time.
[Trait("Category", "Integration")]
public class ReaderIntegrationTests(ITestOutputHelper output) : IClassFixture<LeftoverGuard>
{
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(45);

    public static IEnumerable<object[]> Browsers() => ItSupport.Browsers();

    static int PageOf(string url) => int.Parse(url[(url.LastIndexOf('/') + 1)..].Split('?')[0], System.Globalization.CultureInfo.InvariantCulture);
    static string TitleOf(int page) => $"Fixture {page}";

    /// The tabs of every window of a browser, read straight from UI Automation (no link resolution).
    static List<RawWindow> RawWindows(Browser b)
    {
        using var source = new UiaSource();
        return WindowsOf(b).Select(source.ReadWindow).Where(r => r is not null).Select(r => r!).ToList();
    }

    [Theory, MemberData(nameof(Browsers))]
    public void DiscoversTheBrowserAndItsWindow(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1)]);
        Assert.True(WaitUntil(() => WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), Slow), "no window titled Fixture 1");
        var other = b == Browser.Chrome ? Browser.Helium : Browser.Chrome;
        Assert.DoesNotContain(BrowserDiscovery.Enumerate(), w => w.Browser == other);
        Assert.True(BrowserDiscovery.IsBrowserRunning(b));
        Assert.False(BrowserDiscovery.IsBrowserRunning(other));
        Assert.Equal(Path.GetFullPath(ExeOf(b)), Path.GetFullPath(BrowserDiscovery.ExePath(b)!), ignoreCase: true);
    }

    [Theory, MemberData(nameof(Browsers))]
    public void ReadsTitlesOrderAndTheSelectedTab(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        RawWindow? raw = null;
        Assert.True(WaitUntil(() => (raw = RawWindows(b).FirstOrDefault(r => r.Tabs.Count >= 3 && r.Tabs.All(t => t.Title.StartsWith("Fixture", StringComparison.Ordinal)))) is not null, Slow),
            "three Fixture tabs did not appear");
        Assert.Equal(["Fixture 1", "Fixture 2", "Fixture 3"], raw!.Tabs.Select(t => t.Title).ToArray());
        Assert.Equal([0, 1, 2], raw.Tabs.Select(t => t.Index).ToArray());
        Assert.Single(raw.Tabs, t => t.IsSelected);
        output.WriteLine($"SELECTED-AT-START {b}: tab {raw.Tabs.Single(t => t.IsSelected).Index} of 3 (command-line order), names {string.Join(" | ", raw.Tabs.Select(t => "'" + t.Title + "'"))}");
    }

    [Theory, MemberData(nameof(Browsers))]
    public void ReadsTheActiveTabUrl(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var reader = ReaderFor(f.Profile);
        var result = ReadUntil(reader, b, r => r.Snapshot?.Tabs.Any(t => t.IsActive) == true, Slow);
        var active = result.Snapshot?.Tabs.SingleOrDefault(t => t.IsActive);
        Assert.True(active is not null, Describe(result));
        Assert.Equal(FixtureServer.Url(PageOf(active!.Url)), active.Url);
        Assert.Equal(TitleOf(PageOf(active.Url)), active.Title);
    }

    [Theory, MemberData(nameof(Browsers))]
    public void RemembersLinksForBackgroundTabs(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var uia = new UiaTest();
        using var reader = MemoryOnlyReader();   // no History, no session: only what each tab showed while it was active
        Assert.True(WaitUntil(() => WindowsOf(b).Any() && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 3, Slow), "three tabs did not appear");
        var hwnd = WindowsOf(b)[0].Hwnd;
        foreach (var index in new[] { 0, 1, 2 })   // every tab active once, last one stays active
        {
            uia.SelectTab(hwnd, index);
            var result = ReadUntil(reader, b, r => r.Snapshot?.Tabs.Any(t => t.IsActive && t.TabIndex == index) == true, Slow);
            Assert.True(result.Snapshot?.Tabs.Any(t => t.IsActive && t.TabIndex == index) == true, $"tab {index} never read as active: {Describe(result)}");
        }
        var final = ReadUntil(reader, b, r => r.Snapshot?.Tabs.Count == 3, Slow);
        Assert.Equal(0, final.UnresolvedTabs);
        Assert.Equal([FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)], final.Snapshot!.Tabs.OrderBy(t => t.TabIndex).Select(t => t.Url).ToArray());
    }

    [Theory, MemberData(nameof(Browsers))]
    public void PrivateWindowsAreNeverRead(Browser b)   // the private-window gate
    {
        using var server = new FixtureServer();
        using var normal = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1)]);
        Assert.True(WaitUntil(() => WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), Slow), "normal window did not appear");
        using var incognito = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(9)], incognito: true, profile: normal.Profile);
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 2, Slow), "private window did not appear");
        Thread.Sleep(3000);
        using var reader = ReaderFor(normal.Profile);

        void AssertOnlyNormalTabs(string phase, int normalWindows)
        {
            var result = ReadUntil(reader, b, r => r.Status == ReadStatus.Ok && r.PrivateWindowsSkipped >= 1 && r.WindowsRead >= normalWindows && r.Snapshot!.Tabs.Count >= normalWindows, Slow);
            var tabs = result.Snapshot?.Tabs ?? [];
            var order = WindowsOf(b);
            output.WriteLine($"PRIVATE-GATE {b} {phase}: window order [{string.Join(", ", order.Select((w, i) => $"{i}:'{w.Title}'"))}]; read {Describe(result)}");
            Assert.True(result.Status == ReadStatus.Ok, $"{phase}: {Describe(result)}");
            Assert.DoesNotContain(tabs, t => t.Title.Contains("Fixture 9", StringComparison.Ordinal) || t.Url.EndsWith("/p/9", StringComparison.Ordinal));
            Assert.Equal(1, result.PrivateWindowsSkipped);
            Assert.Equal(normalWindows, result.WindowsRead);
            // The private window still counts in the ordering: an active tab's WindowIndex is its window's place among all windows.
            int privateIndex = order.FindIndex(w => w.Title.StartsWith("Fixture 9", StringComparison.Ordinal));
            Assert.True(privateIndex >= 0, $"{phase}: private window not found in {string.Join(", ", order.Select(w => w.Title))}");
            foreach (var t in tabs)
            {
                Assert.NotEqual(privateIndex, t.WindowIndex);
                if (t.IsActive) Assert.Equal(order.FindIndex(w => w.Title.StartsWith(t.Title, StringComparison.Ordinal)), t.WindowIndex);
            }
        }

        AssertOnlyNormalTabs("private created last", 1);

        // A third window, normal, in front of the private one.
        using var third = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(2)], profile: normal.Profile);
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 3, Slow), "third window did not appear");
        Thread.Sleep(3000);
        AssertOnlyNormalTabs("normal window in front", 2);
    }

    [Theory, MemberData(nameof(Browsers))]
    public void APrivateOnlyBrowserGivesNoTabs(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(9)], incognito: true);
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 1, Slow), "private window did not appear");
        Thread.Sleep(3000);
        using var reader = ReaderFor(f.Profile);
        var result = ReadUntil(reader, b, r => r.PrivateWindowsSkipped >= 1, Slow);
        output.WriteLine($"PRIVATE-ONLY {b}: {Describe(result)}");
        Assert.Empty(result.Snapshot?.Tabs ?? []);
        Assert.True(result.Status is ReadStatus.Ok or ReadStatus.NotRunning, Describe(result));
        Assert.Equal(1, result.PrivateWindowsSkipped);
    }

    [Theory, MemberData(nameof(Browsers))]
    public void HooksFireOnTabSwitchAndStayQuietWhenIdle(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Any() && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 3, Slow), "three tabs did not appear");
        var hwnd = WindowsOf(b)[0].Hwnd;
        Thread.Sleep(3000);

        var events = new List<(WatchEventKind Kind, nint Hwnd, long At)>();
        WinEventWatcher? watcher = null; uint pumpThread = 0; Exception? failure = null;
        using var started = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                watcher = new WinEventWatcher((k, h) => { lock (events) events.Add((k, h, Environment.TickCount64)); });
                watcher.SetKnownBrowserWindows([hwnd]);
                _ = NativeTest.PeekMessage(out _, 0, 0, 0, 0);   // creates this thread's message queue
                pumpThread = NativeTest.GetCurrentThreadId();
                watcher.Start();
                started.Set();
                NativeTest.Pump();
                watcher.Dispose();
            }
            catch (Exception e) { failure = e; started.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.True(started.Wait(10_000)); Assert.Null(failure);
        try
        {
            int Count(WatchEventKind k) { lock (events) return events.Count(e => e.Kind == k); }
            // A tab switch retitles the window.
            int before = Count(WatchEventKind.NameChanged); long t0 = Environment.TickCount64;
            uia.SelectTab(hwnd, 0); uia.SelectTab(hwnd, 1);
            Assert.True(WaitUntil(() => Count(WatchEventKind.NameChanged) > before, TimeSpan.FromSeconds(1)), "no NameChanged within 1 s of a tab switch");
            long latency; lock (events) latency = events.First(e => e.Kind == WatchEventKind.NameChanged && e.At >= t0).At - t0;
            output.WriteLine($"HOOK {b}: first NameChanged {latency} ms after the switch started");

            // Quiet when idle: no browser-related event in 5 s without input.
            Thread.Sleep(1500);
            int quietFrom; lock (events) quietFrom = events.Count;
            Thread.Sleep(5000);
            List<(WatchEventKind Kind, nint Hwnd, long At)> idle; lock (events) idle = events.Skip(quietFrom).ToList();
            output.WriteLine($"HOOK {b}: events in 5 s idle = {idle.Count} ({string.Join(", ", idle.Select(e => e.Kind))})");
            Assert.Empty(idle.Where(e => e.Kind != WatchEventKind.Foreground));   // foreground changes belong to other programs

            // A new window shows; closing it is reported for a known hwnd.
            var knownBefore = WindowsOf(b).Select(w => w.Hwnd).ToHashSet();
            int shown = Count(WatchEventKind.WindowShown);
            using var second = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(4)], profile: f.Profile);
            Assert.True(WaitUntil(() => WindowsOf(b).Any(w => !knownBefore.Contains(w.Hwnd)), Slow), "second window did not appear");
            Assert.True(Count(WatchEventKind.WindowShown) > shown, "no WindowShown for the new window");
            var newWindow = WindowsOf(b).First(w => !knownBefore.Contains(w.Hwnd)).Hwnd;
            // Foreground: x64 only (the Arm runner's first-boot privacy screen holds the foreground there). The new window
            // is in front now; moving the first one forward is a foreground change in another process's window.
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
            if (arch == System.Runtime.InteropServices.Architecture.X64)
            {
                int fg = Count(WatchEventKind.Foreground);
                NativeTest.keybd_event(0x12, 0, 0, 0); NativeTest.keybd_event(0x12, 0, 2, 0);   // an Alt tap lifts the foreground lock for this process
                bool forced = NativeTest.SetForegroundWindow(hwnd) != 0;
                bool seen = WaitUntil(() => Count(WatchEventKind.Foreground) > fg, TimeSpan.FromSeconds(3));
                string how = "SetForegroundWindow";
                if (!seen)
                {
                    fg = Count(WatchEventKind.Foreground); how = "minimize and restore";
                    _ = NativeTest.ShowWindow(hwnd, NativeTest.SwMinimize);
                    seen = WaitUntil(() => Count(WatchEventKind.Foreground) > fg, TimeSpan.FromSeconds(3));
                    _ = NativeTest.ShowWindow(hwnd, NativeTest.SwRestore);
                }
                output.WriteLine($"HOOK {b}: foreground event seen={seen} via {how} (SetForegroundWindow returned {forced})");
                Assert.True(seen, "no Foreground event");
            }
            else output.WriteLine($"HOOK {b}: foreground assertion skipped on {arch}");

            watcher!.SetKnownBrowserWindows([hwnd, newWindow]);
            _ = NativeTest.PostMessage(newWindow, NativeTest.WmClose, 0, 0);
            Assert.True(WaitUntil(() => { lock (events) return events.Any(e => e.Kind == WatchEventKind.WindowDestroyed && e.Hwnd == newWindow); }, TimeSpan.FromSeconds(10)),
                "no WindowDestroyed for the closed window");
        }
        finally
        {
            _ = NativeTest.PostThreadMessage(pumpThread, NativeTest.WmQuit, 0, 0);
            thread.Join(5000);
        }
    }

    [Theory, MemberData(nameof(Browsers))]
    public void HistoryGivesLinksToTabsOpenedBeforeLaterStarted(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Any() && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 3, Slow), "three tabs did not appear");
        uia.SelectTab(WindowsOf(b)[0].Hwnd, 2);
        string[] titles = [TitleOf(1), TitleOf(2), TitleOf(3)];
        var stamp = Stopwatch.StartNew();
        Assert.True(WaitUntil(() => HistoryLookupStore.LinksForTitles(titles, HistoryLookupStore.Databases(f.Profile), DateTime.UtcNow.AddDays(-1)).Count == 3, TimeSpan.FromSeconds(60)),
            "History never held the three titles");
        output.WriteLine($"HISTORY {b}: all three pages in History after {stamp.ElapsedMilliseconds} ms");

        using var fresh = ReaderFor(f.Profile);   // no memory
        var result = ReadUntil(fresh, b, r => r.Snapshot?.Tabs.Count == 3, Slow);
        Assert.Equal(0, result.UnresolvedTabs);
        Assert.Equal([FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)], result.Snapshot!.Tabs.OrderBy(t => t.TabIndex).Select(t => t.Url).ToArray());
    }

    [Theory, MemberData(nameof(Browsers))]
    public void TwoTabsWithOneTitleStayUnresolved(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(1, "?x=2")]);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Any() && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 2, Slow), "two tabs did not appear");
        Assert.True(WaitUntil(() => HistoryLookupStore.LinksForTitles([TitleOf(1)], HistoryLookupStore.Databases(f.Profile), DateTime.UtcNow.AddDays(-1)).Count == 1, TimeSpan.FromSeconds(60)),
            "History never held the title");
        using var fresh = ReaderFor(f.Profile);
        var result = ReadUntil(fresh, b, r => r.Snapshot?.Tabs.Count >= 1, Slow);
        output.WriteLine($"AMBIGUOUS {b}: {Describe(result)}");
        // The active tab's link is exact; the background tab shares its title, so History is not allowed to guess for it.
        Assert.Equal(1, result.UnresolvedTabs);
        var only = Assert.Single(result.Snapshot!.Tabs);
        Assert.True(only.IsActive);
    }

    [Theory, MemberData(nameof(Browsers))]
    public void PreviousSessionRestoresLinks(Browser b)
    {
        using var server = new FixtureServer();
        string[] urls = [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3), FixtureServer.Url(4)];
        using var first = BrowserFixture.Start(ExeOf(b), server, urls);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Any() && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 4, Slow), "four tabs did not appear");
        Assert.True(WaitUntil(() => HistoryLookupStore.LinksForTitles([TitleOf(1), TitleOf(2), TitleOf(3), TitleOf(4)], HistoryLookupStore.Databases(first.Profile), DateTime.UtcNow.AddDays(-1)).Count == 4, TimeSpan.FromSeconds(60)),
            "History never held the four titles");
        Thread.Sleep(3000);
        first.Close();
        Assert.True(WaitUntil(() => WindowsOf(b).Count == 0, TimeSpan.FromSeconds(20)), "the first run's window did not close");

        using var second = BrowserFixture.Start(ExeOf(b), server, [], profile: first.Profile, restore: true);
        Assert.True(WaitUntil(() => WindowsOf(b).Any() && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 4, Slow), "the restored window did not get four tabs");
        var sessionsDir = Path.Combine(first.Profile, "Default", "Sessions");
        foreach (var file in Directory.Exists(sessionsDir) ? Directory.GetFiles(sessionsDir).OrderBy(x => x, StringComparer.Ordinal) : [])
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length]; fs.ReadExactly(bytes);
                var parsed = PreviousSession.Parse(bytes);
                output.WriteLine($"SESSION-FILE {b}: {Path.GetFileName(file)} {bytes.Length} bytes, header {Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)))}, parsed {parsed.Count} tabs");
            }
            catch (IOException e) { output.WriteLine($"SESSION-FILE {b}: {Path.GetFileName(file)} unreadable: {e.Message}"); }
        }
        var session = PreviousSession.Read(first.Profile);
        output.WriteLine($"SESSION {b}: {string.Join(" | ", session.OrderBy(s => s.Index).Select(s => $"w{s.WindowId} t{s.Index} {s.Url} '{s.Title}'"))}");
        Assert.Equal(urls, session.OrderBy(s => s.Index).Select(s => s.Url).ToArray());

        // A fresh reader fills the background tabs from the session file; History is never consulted.
        var history = new CountingHistory();
        using var fresh = new BrowserReader(new UiaSource(), history, new SessionSource(_ => first.Profile), BrowserDiscovery.Enumerate, () => DateTime.UtcNow);
        var result = ReadUntil(fresh, b, r => r.Snapshot?.Tabs.Count == 4, Slow);
        output.WriteLine($"SESSION {b}: read {Describe(result)}; history calls {history.Calls}");
        Assert.Equal(0, result.UnresolvedTabs);
        Assert.Equal(0, history.Calls);
        Assert.Equal(urls, result.Snapshot!.Tabs.OrderBy(t => t.TabIndex).Select(t => t.Url).ToArray());
    }

    sealed class CountingHistory : IHistorySource
    {
        public int Calls;
        public Dictionary<string, string> LinksForTitles(Browser b, IReadOnlyCollection<string> titles, DateTime since) { Calls++; return new(StringComparer.Ordinal); }
    }

    [Theory, MemberData(nameof(Browsers))]
    public void ReadCostIsBounded(Browser b)
    {
        using var server = new FixtureServer();
        using var first = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3), FixtureServer.Url(4), FixtureServer.Url(5)]);
        Assert.True(WaitUntil(() => WindowsOf(b).Any(), Slow));
        using var second = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(6), FixtureServer.Url(7), FixtureServer.Url(8), FixtureServer.Url(9), FixtureServer.Url(10)], profile: first.Profile);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 2 && WindowsOf(b).Sum(w => uia.Tabs(w.Hwnd).Count) >= 10, Slow), "ten tabs in two windows did not appear");
        Thread.Sleep(3000);
        using var reader = ReaderFor(first.Profile);
        var costs = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var result = reader.Read(b);
            proc.Refresh();
            double ms = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            costs.Add(ms);
            output.WriteLine($"READCOST ms={ms:F1} read={i + 1} status={result.Status} tabs={result.Snapshot?.Tabs.Count} unresolved={result.UnresolvedTabs}");
            Thread.Sleep(300);
        }
        double average = costs.Average();
        output.WriteLine($"READCOST {b} average ms={average:F1} max={costs.Max():F1}");
        Assert.True(average <= 150, $"average read cost {average:F1} ms");
    }

    [Fact]
    public void NoLeftoverBrowsers()
    {
        Assert.True(WaitUntil(() => LeftoverGuard.Pids().Count == 0, TimeSpan.FromSeconds(20)), "browser processes from earlier facts are still running");
    }
}

/// After the class: no browser process whose command line names a throwaway profile may still run (kills and fails otherwise).
public sealed class LeftoverGuard : IDisposable
{
    public static List<int> Pids(string marker = "LaterIT-")
    {
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'chrome.exe' -and $_.CommandLine -like '*" + marker + "*' } | ForEach-Object { $_.ProcessId }" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => int.Parse(t.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

    public void Dispose()
    {
        var left = Pids();
        foreach (var pid in left) { try { Process.GetProcessById(pid).Kill(true); } catch (ArgumentException) { } catch (InvalidOperationException) { } }
        if (left.Count > 0) throw new InvalidOperationException($"{left.Count} browser processes were left running");
    }
}
