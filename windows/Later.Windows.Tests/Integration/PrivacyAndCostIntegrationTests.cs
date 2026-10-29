using System.Collections.Concurrent;
using System.Diagnostics;
using Later.Core;
using Later.Windows.Native;
using Later.Windows.Reader;
using Xunit;
using Xunit.Abstractions;
using static Later.Windows.Tests.Integration.ItSupport;

namespace Later.Windows.Tests.Integration;

/// Private windows in other languages and profiles, what a read costs on heavy pages, and what the event hooks cost on a busy desktop.
[Trait("Category", "Integration")]
public class PrivacyAndCostIntegrationTests(ITestOutputHelper output) : IClassFixture<LeftoverGuard>
{
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(45);

    public static IEnumerable<object[]> Browsers() => ItSupport.Browsers();
    static readonly string[] Languages = ["de", "ja", "fr"];
    public static IEnumerable<object[]> BrowsersAndLanguages() => ItSupport.Browsers().SelectMany(b => Languages.Select(lang => new[] { b[0], lang }));

    static string Esc(string s) => string.Concat(s.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:x4}"));

    [Theory, MemberData(nameof(BrowsersAndLanguages))]
    public void PrivateWindowsAreNeverReadInAnyLanguage(Browser b, string lang)
    {
        using var server = new FixtureServer();
        using var normal = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1)], extra: $"--lang={lang}");
        Assert.True(WaitUntil(() => WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), Slow), "normal window did not appear");
        using var incognito = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(9)], incognito: true, profile: normal.Profile, extra: $"--lang={lang}");
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 2, Slow), "private window did not appear");
        Thread.Sleep(3000);
        using var reader = ReaderFor(normal.Profile);
        var result = ReadUntil(reader, b, r => r.Status == ReadStatus.Ok && r.PrivateWindowsSkipped >= 1 && r.Snapshot!.Tabs.Count >= 1, Slow);
        output.WriteLine($"LANG {b} {lang}: windows [{string.Join(" | ", WindowsOf(b).Select(w => Esc(w.Title)))}]; read {Describe(result)}");
        Assert.Equal(ReadStatus.Ok, result.Status);
        var tabs = result.Snapshot!.Tabs;
        Assert.DoesNotContain(tabs, t => t.Title.Contains("Fixture 9", StringComparison.Ordinal) || t.Url.EndsWith("/p/9", StringComparison.Ordinal));
        Assert.Equal(1, result.PrivateWindowsSkipped);
        Assert.Equal(1, result.WindowsRead);
        var read = Assert.Single(tabs);   // the normal window is read, in this language too
        Assert.Equal(FixtureServer.Url(1), read.Url);
        Assert.True(read.Title == "Fixture 1", Esc(read.Title));   // no localized "memory usage" suffix left on the tab name
    }

    [Theory, MemberData(nameof(Browsers))]
    public void TwoProfilesAreReadAndTheirPrivateWindowIsNot(Browser b)
    {
        using var server = new FixtureServer();
        using var first = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1)], extra: "--profile-directory=Default");
        Assert.True(WaitUntil(() => WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), Slow), "first window did not appear");
        using var second = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(2)], profile: first.Profile, extra: "--profile-directory=Profile 2");
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 2, Slow), "second profile's window did not appear");
        using var incognito = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(9)], incognito: true, profile: first.Profile, extra: "--profile-directory=Profile 2");
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 3, Slow), "private window did not appear");
        Thread.Sleep(3000);
        using var reader = ReaderFor(first.Profile);
        var result = ReadUntil(reader, b, r => r.Status == ReadStatus.Ok && r.PrivateWindowsSkipped >= 1 && r.Snapshot!.Tabs.Count >= 2, Slow);
        output.WriteLine($"TWO-PROFILES {b}: windows [{string.Join(" | ", WindowsOf(b).Select(w => Esc(w.Title)))}]; read {Describe(result)}");
        Assert.Equal(ReadStatus.Ok, result.Status);
        var tabs = result.Snapshot!.Tabs;
        Assert.DoesNotContain(tabs, t => t.Title.Contains("Fixture 9", StringComparison.Ordinal) || t.Url.EndsWith("/p/9", StringComparison.Ordinal));
        Assert.Equal(1, result.PrivateWindowsSkipped);
        Assert.Equal(2, result.WindowsRead);
        Assert.Equal([FixtureServer.Url(1), FixtureServer.Url(2)], tabs.Select(t => t.Url).Order().ToArray());
    }

    // ---- what a read costs on heavy pages

    /// (browser CPU ms, browser private bytes) summed over the browser processes that belong to one throwaway profile.
    static (double CpuMs, long PrivateBytes) BrowserUse(string marker)
    {
        double cpu = 0; long bytes = 0;
        foreach (var pid in LeftoverGuard.Pids(marker))
        {
            try { using var p = Process.GetProcessById(pid); cpu += p.TotalProcessorTime.TotalMilliseconds; bytes += p.PrivateMemorySize64; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return (cpu, bytes);
    }

    static double OwnCpuMs() { using var p = Process.GetCurrentProcess(); return p.TotalProcessorTime.TotalMilliseconds; }

    [Theory, MemberData(nameof(Browsers))]
    public void HeavyPagesReadCostStaysSmall(Browser b)
    {
        using var server = new FixtureServer();
        using var first = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.HeavyUrl(1), FixtureServer.HeavyUrl(2), FixtureServer.HeavyUrl(3), FixtureServer.HeavyUrl(4)]);
        Assert.True(WaitUntil(() => WindowsOf(b).Count > 0, Slow));
        using var second = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.HeavyUrl(5), FixtureServer.HeavyUrl(6)], profile: first.Profile);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Count >= 2 && WindowsOf(b).Sum(w => uia.Tabs(w.Hwnd).Count) >= 6, Slow), "six tabs in two windows did not appear");
        Thread.Sleep(10_000);   // pages load and settle
        var marker = Path.GetFileName(first.Profile);
        using var source = new UiaSource(_ => first.Profile);

        int ReadAll(out int tabCount, out int urls)
        {
            tabCount = 0; urls = 0; int windows = 0;
            foreach (var w in WindowsOf(b))
            {
                var raw = source.ReadWindow(w);
                if (raw is null || raw.IsPrivate) continue;
                windows++; tabCount += raw.Tabs.Count; if (raw.ActiveUrl is not null) urls++;
            }
            return windows;
        }

        // Idle control: the browser with heavy pages and no reader.
        var idle0 = BrowserUse(marker); var idleClock = Stopwatch.StartNew();
        Thread.Sleep(6000);
        var idle1 = BrowserUse(marker); idleClock.Stop();
        double idleRate = (idle1.CpuMs - idle0.CpuMs) / idleClock.Elapsed.TotalSeconds;   // browser ms per second with no reader
        output.WriteLine($"HEAVY {b} idle: browser cpu {idle1.CpuMs - idle0.CpuMs:F0} ms in {idleClock.Elapsed.TotalSeconds:F1} s ({idleRate:F0} ms/s), private {idle1.PrivateBytes / 1048576.0:F1} MB");

        // The first read switches the browser's accessibility on; report it apart from the steady state.
        var f0 = BrowserUse(marker); double o0 = OwnCpuMs(); var firstClock = Stopwatch.StartNew();
        int windowsRead = ReadAll(out int tabsRead, out int urlsRead);
        double firstOwn = OwnCpuMs() - o0; firstClock.Stop();
        Thread.Sleep(3000);
        var f1 = BrowserUse(marker);
        output.WriteLine($"HEAVY {b} first read: later {firstOwn:F1} ms ({firstClock.ElapsedMilliseconds} ms wall), browser cpu {f1.CpuMs - f0.CpuMs:F0} ms over read+3 s, private {(f1.PrivateBytes - f0.PrivateBytes) / 1048576.0:+0.0;-0.0} MB; windows={windowsRead} tabs={tabsRead} urls={urlsRead}");
        Assert.Equal(2, windowsRead); Assert.Equal(6, tabsRead); Assert.Equal(2, urlsRead);

        // Steady state: 40 reads of both windows.
        const int N = 40;
        var s0 = BrowserUse(marker); double own0 = OwnCpuMs(); var clock = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) { ReadAll(out _, out _); Thread.Sleep(150); }
        double own = OwnCpuMs() - own0; clock.Stop();
        var s1 = BrowserUse(marker);
        // Control with accessibility already switched on and no reader: the page keeps changing, and the browser serves that tree now.
        var a0 = BrowserUse(marker); var aClock = Stopwatch.StartNew();
        Thread.Sleep(6000);
        var a1 = BrowserUse(marker); aClock.Stop();
        idleRate = (a1.CpuMs - a0.CpuMs) / aClock.Elapsed.TotalSeconds;
        output.WriteLine($"HEAVY {b} idle with accessibility on, no reader: {idleRate:F0} ms/s");
        double browserTotal = s1.CpuMs - s0.CpuMs, browserExtra = browserTotal - idleRate * clock.Elapsed.TotalSeconds;
        double perRead = own / N;
        output.WriteLine($"HEAVY {b} bounded read of 2 windows / 6 heavy tabs x{N}: later {perRead:F1} ms per read; browser cpu {browserTotal:F0} ms total ({browserExtra / N:F1} ms per read above idle); private {(s1.PrivateBytes - s0.PrivateBytes) / 1048576.0:+0.0;-0.0} MB");

        // The same through the whole reader (History consulted for the background tabs): the cost Later pays per wake-up.
        using var reader = ReaderFor(first.Profile);
        reader.Read(b);
        own0 = OwnCpuMs();
        for (int i = 0; i < 20; i++) { reader.Read(b); Thread.Sleep(150); }
        double fullPerRead = (OwnCpuMs() - own0) / 20;
        output.WriteLine($"HEAVY {b} whole BrowserReader.Read x20: later {fullPerRead:F1} ms per read");

        // Contrast: a whole-tree search for the tabs and the document, as the first version did.
        var u0 = BrowserUse(marker); own0 = OwnCpuMs(); clock.Restart();
        for (int i = 0; i < 10; i++) { foreach (var w in WindowsOf(b)) _ = uia.Tabs(w.Hwnd); Thread.Sleep(150); }
        double unboundedOwn = (OwnCpuMs() - own0) / 10; clock.Stop();
        var u1 = BrowserUse(marker);
        output.WriteLine($"HEAVY {b} CONTRAST whole-tree search x10: later {unboundedOwn:F1} ms per read; browser cpu {u1.CpuMs - u0.CpuMs:F0} ms total ({(u1.CpuMs - u0.CpuMs - idleRate * clock.Elapsed.TotalSeconds) / 10:F1} ms per read above idle); private {(u1.PrivateBytes - u0.PrivateBytes) / 1048576.0:+0.0;-0.0} MB");

        Assert.True(perRead <= 50, $"bounded read cost {perRead:F1} ms per read of two windows");
        Assert.True(fullPerRead <= 50, $"whole read cost {fullPerRead:F1} ms");
    }

    // ---- the event hooks on a busy desktop

    /// A message-pumping thread that owns a WinEventWatcher; work for that thread goes through `Run`.
    sealed class WatcherHost : IDisposable
    {
        const uint WmApp = 0x8000;
        public WinEventWatcher Watcher { get; private set; } = null!;
        readonly ConcurrentQueue<Action> work = new();
        readonly ManualResetEventSlim started = new();
        readonly Thread thread;
        uint threadId; Exception? failure;

        public WatcherHost()
        {
            thread = new Thread(Pump) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(started.Wait(10_000)); Assert.Null(failure);
        }

        void Pump()
        {
            try
            {
                Watcher = new WinEventWatcher((_, _) => { });
                _ = NativeTest.PeekMessage(out _, 0, 0, 0, 0);   // creates this thread's message queue
                threadId = NativeTest.GetCurrentThreadId();
                Watcher.Start();
                started.Set();
                while (NativeTest.GetMessage(out var m, 0, 0, 0) > 0)
                {
                    if (m.Message == WmApp) { while (work.TryDequeue(out var a)) a(); continue; }
                    _ = NativeTest.TranslateMessage(in m); _ = NativeTest.DispatchMessage(in m);
                }
                Watcher.Dispose();
            }
            catch (Exception e) { failure = e; started.Set(); }
        }

        public void Run(Action a)
        {
            using var done = new ManualResetEventSlim();
            work.Enqueue(() => { try { a(); } finally { done.Set(); } });
            _ = NativeTest.PostThreadMessage(threadId, WmApp, 0, 0);
            Assert.True(done.Wait(10_000), "the watcher thread did not run the request");
        }

        public void Dispose() { _ = NativeTest.PostThreadMessage(threadId, NativeTest.WmQuit, 0, 0); thread.Join(5000); }
    }

    [Theory, MemberData(nameof(Browsers))]
    public void OtherProgramsWindowTrafficNeverReachesTheWatcher(Browser b)
    {
        using var server = new FixtureServer();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1)]);
        Assert.True(WaitUntil(() => WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), Slow), "window did not appear");
        uint pid = WindowsOf(b)[0].Pid;
        using var host = new WatcherHost();
        host.Run(() => host.Watcher.Retarget([pid]));
        Assert.Equal([pid], host.Watcher.WatchedPids.ToArray());
        Thread.Sleep(3000);

        // Another program retitles its own console window as fast as it can.
        using var busy = Process.Start(new ProcessStartInfo("cmd.exe", "/c for /l %i in (1,1,500000) do @title busy%i") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized })!;
        Thread.Sleep(1500);
        try
        {
            // Phase 1: the watcher as shipped (browser processes only).
            long e0 = host.Watcher.EventsSeen; double c0 = OwnCpuMs(); var clock = Stopwatch.StartNew();
            Thread.Sleep(8000);
            long seen = host.Watcher.EventsSeen - e0; double cpu = OwnCpuMs() - c0; clock.Stop();
            output.WriteLine($"BUSY {b} per-browser hooks: agent callbacks={seen} in {clock.Elapsed.TotalSeconds:F1} s, agent cpu={cpu:F0} ms ({cpu / clock.Elapsed.TotalSeconds / 10:F2}% of a core)");

            // Phase 2, for contrast: add one system-wide name-change hook (what the first version had) in the same process.
            long global = 0;
            User32.WinEventProc counter = (_, _, hwnd, idObject, _, _, _) => { if (idObject == User32.ObjIdWindow && hwnd != 0) Interlocked.Increment(ref global); };
            nint hook = 0;
            host.Run(() => hook = User32.SetWinEventHook(User32.EventObjectNameChange, User32.EventObjectNameChange, 0, counter, 0, 0, User32.WinEventOutOfContext | User32.WinEventSkipOwnProcess));
            Assert.NotEqual(0, hook);
            c0 = OwnCpuMs(); clock.Restart();
            Thread.Sleep(8000);
            long globalSeen = Interlocked.Read(ref global); double globalCpu = OwnCpuMs() - c0; clock.Stop();
            host.Run(() => _ = User32.UnhookWinEvent(hook));
            GC.KeepAlive(counter);
            output.WriteLine($"BUSY {b} CONTRAST one system-wide name-change hook: callbacks={globalSeen} in {clock.Elapsed.TotalSeconds:F1} s, agent cpu={globalCpu:F0} ms ({globalCpu / clock.Elapsed.TotalSeconds / 10:F2}% of a core)");

            Assert.True(globalSeen >= 100, $"the busy program produced only {globalSeen} window events, so this proves nothing");
            // The browser's own chatter reached 20 on CI; the contrast hook sees ~500+. Leave wide margin so this is not a flake, yet far below the global count.
            Assert.True(seen < 100 && seen * 5 < globalSeen, $"{seen} events reached the per-browser watcher while another program was busy (a system-wide hook saw {globalSeen})");
            Assert.True(cpu <= 150, $"agent cpu {cpu:F0} ms in 8 s");
        }
        finally { try { busy.Kill(true); } catch (InvalidOperationException) { } }
    }

    [Theory, MemberData(nameof(Browsers))]
    public void TheWatcherFollowsABrowserStartingAndQuitting(Browser b)
    {
        using var server = new FixtureServer();
        using var host = new WatcherHost();
        host.Run(() => host.Watcher.Retarget([]));
        Assert.Empty(host.Watcher.WatchedPids);
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1)]);
        Assert.True(WaitUntil(() => WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), Slow), "window did not appear");
        var window = WindowsOf(b)[0];

        // A browser's window coming to the front registers its process (the Arm runner's first-boot screen holds the foreground, so x64 only).
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            NativeTest.keybd_event(0x12, 0, 0, 0); NativeTest.keybd_event(0x12, 0, 2, 0);   // an Alt tap lifts the foreground lock for this process
            _ = NativeTest.SetForegroundWindow(window.Hwnd);
            bool seen = WaitUntil(() => host.Watcher.WatchedPids.Contains(window.Pid), TimeSpan.FromSeconds(10));
            if (!seen) { _ = NativeTest.ShowWindow(window.Hwnd, NativeTest.SwMinimize); _ = NativeTest.ShowWindow(window.Hwnd, NativeTest.SwRestore); seen = WaitUntil(() => host.Watcher.WatchedPids.Contains(window.Pid), TimeSpan.FromSeconds(10)); }
            output.WriteLine($"FOLLOW {b}: browser pid {window.Pid} registered by its window coming to the front = {seen}");
            Assert.True(seen, "the browser was not registered when its window came to the front");
        }

        host.Run(() => host.Watcher.Retarget([window.Pid]));
        Assert.Equal([window.Pid], host.Watcher.WatchedPids.ToArray());
        host.Run(() => host.Watcher.Retarget([]));   // the browser quit
        Assert.Empty(host.Watcher.WatchedPids);
        host.Run(() => host.Watcher.Retarget([window.Pid]));
        Assert.Equal([window.Pid], host.Watcher.WatchedPids.ToArray());
    }
}
