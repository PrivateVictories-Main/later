using System.Diagnostics;
using System.Runtime.InteropServices;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Native;
using Later.Windows.Platform;
using Later.Windows.Reader;
using Xunit;
using Xunit.Abstractions;
using static Later.Windows.Tests.Integration.ItSupport;

namespace Later.Windows.Tests.Integration;

/// The real `Later.exe --agent` (a Debug build, with the test hooks) beside real Chrome and Helium on throwaway profiles. The
/// agent's data folder is a temp folder (LATER_DATA_DIR), its browser reads look at the fixture's profile (LATER_TEST_PROFILE),
/// and a page counts as left open after five seconds. LATER_EXE names the executable.
[Trait("Category", "Integration")]
public class AgentIntegrationTests(ITestOutputHelper output) : IClassFixture<LeftoverGuard>
{
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(45);

    public static IEnumerable<object[]> Browsers() => ItSupport.Browsers();

    static string Exe => Environment.GetEnvironmentVariable("LATER_EXE") is { Length: > 0 } exe && File.Exists(exe)
        ? exe : throw new Xunit.Sdk.XunitException("LATER_EXE does not name the Debug Later.exe");

    /// The agent process, started the way a login item would start it.
    sealed class AgentProcess : IDisposable
    {
        public Process Process { get; }
        readonly string tracePath;
        readonly ITestOutputHelper? output;
        AgentProcess(Process process, string tracePath, ITestOutputHelper? output) { Process = process; this.tracePath = tracePath; this.output = output; }

        public static AgentProcess Start(string dataDir, string profile, bool onboarded = true, string flag = "--agent", ITestOutputHelper? output = null)
        {
            File.WriteAllText(Path.Combine(dataDir, "settings.json"), onboarded ? "{\"onboarded\":true}" : "{\"onboarded\":false}");
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe)! };
            psi.ArgumentList.Add(flag);
            psi.Environment["LATER_DATA_DIR"] = dataDir;
            psi.Environment["LATER_TEST_LEFT_OPEN_SECONDS"] = "5";
            psi.Environment["LATER_TEST_PROFILE"] = profile;
            var tracePath = dataDir + "-" + flag.TrimStart('-') + ".trace";   // next to the data folder, so the folder holds only what the agent made
            psi.Environment["LATER_TEST_TRACE"] = tracePath;
            psi.Environment["LATER_TEST_LOAD_RETRY_SECONDS"] = "2";   // a list that is only locked is asked for again after two seconds
            return new AgentProcess(Process.Start(psi)!, tracePath, output);
        }

        public void Dispose()
        {
            try { if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(10_000); } }
            catch (InvalidOperationException) { }
            Process.Dispose();
            try
            {
                if (File.Exists(tracePath))
                {
                    var lines = File.ReadAllLines(tracePath);
                    output?.WriteLine($"AGENT trace ({lines.Length} events, the last 40): counts and timings only");
                    foreach (var line in lines.TakeLast(40)) output?.WriteLine("  " + line);
                    File.Delete(tracePath);
                }
            }
            catch (IOException) { }
        }
    }

    /// Acts as the app: asks the agent for the engine and holds it until released. On a thread of its own: the mutex belongs to it.
    sealed class AppStandIn : IDisposable
    {
        readonly Thread thread;
        readonly ManualResetEventSlim acquired = new(), release = new();
        Exception? failure;
        public bool Got { get; private set; }
        public TimeSpan Took { get; private set; }

        public AppStandIn(string suffix)
        {
            thread = new Thread(() =>
            {
                try
                {
                    using var gate = EngineLock.Open(suffix);
                    var timer = Stopwatch.StartNew();
                    Got = gate.AcquireForApp(TimeSpan.FromSeconds(5)); Took = timer.Elapsed;
                    acquired.Set();
                    if (Got) { release.Wait(); gate.Release(); }
                }
                catch (Exception e) { failure = e; acquired.Set(); }
            });
            thread.Start();
        }

        public bool WaitAcquired() { var ok = acquired.Wait(20_000); Assert.Null(failure); return ok; }
        public void Release() => release.Set();

        public void Dispose()
        {
            release.Set(); thread.Join(20_000);
            acquired.Dispose(); release.Dispose();
        }
    }

    /// The saved list as the agent last wrote it; null while it can't be read. Never moves anything aside, as `LibraryStore.Load` may.
    static LibraryData? ReadLibrary(string dir)
    {
        try
        {
            using var f = new FileStream(Path.Combine(dir, "library.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[f.Length];
            f.ReadExactly(bytes);
            return LibraryJson.Decode(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { return null; }
    }

    static int PageOf(string url)
    {
        int at = url.LastIndexOf("/p/", StringComparison.Ordinal);
        if (at < 0) return -1;
        var digits = new string([.. url[(at + 3)..].TakeWhile(char.IsAsciiDigit)]);
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    static Item? Saved(string dir, int page) => ReadLibrary(dir)?.Items.FirstOrDefault(i => PageOf(i.Url) == page);

    /// Only where a test truly needs a new event: a tab opened in front of the others is on screen, and it is saved once it has been
    /// left in the background, which takes a tab change. So the test changes the tab now and then (the way a user does).
    static bool Poll(Func<bool> done, TimeSpan timeout, UiaTest uia, Func<nint> window)
    {
        var timer = Stopwatch.StartNew(); int next = 0;
        while (timer.Elapsed < timeout)
        {
            if (done()) return true;
            try
            {
                var tabs = uia.Tabs(window()).Count;
                if (tabs > 0) uia.SelectTab(window(), next++ % tabs);
            }
            catch (COMException) { }
            var turn = Stopwatch.StartNew();
            while (turn.Elapsed < TimeSpan.FromSeconds(6)) { if (done()) return true; Thread.Sleep(250); }
        }
        return done();
    }

    static string Suffix(string dir) => new AppPaths(dir).InstanceSuffix;

    /// The classes (and whether each is visible) of the top-level windows a process owns.
    static List<(string Class, bool Visible)> WindowsOfProcess(Process process)
    {
        var pid = (uint)process.Id;
        var found = new List<(string, bool)>();
        User32.EnumWindows((hwnd, lParam) =>
        {
            _ = User32.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) found.Add((User32.Class(hwnd), User32.IsWindowVisible(hwnd) != 0));
            return 1;
        }, 0);
        return found;
    }

    /// The agent has an engine only while its hidden session window exists.
    static bool Watching(AgentProcess agent) => WindowsOfProcess(agent.Process).Any(w => w.Class == "Later.Hidden");

    void ReportSaved(string what, Browser b, string dir) =>
        output.WriteLine($"AGENT {what} {b}: saved pages = {string.Join(",", (ReadLibrary(dir)?.Items ?? []).Select(i => PageOf(i.Url)).Order())}");

    /// The page opened last is the one meant to be shown, but browsers differ in which tab they show after a launch with three
    /// pages. Showing the third once, before the agent starts, puts pages 1 and 2 in the background: the setup, not a nudge.
    static void ShowThirdTab(UiaTest uia, Browser b) =>
        Assert.True(WaitUntil(() =>
        {
            try
            {
                var window = WindowsOf(b)[0].Hwnd;
                uia.SelectTab(window, 2);
                return uia.Tabs(window) is { Count: >= 3 } tabs && tabs[2].Selected;
            }
            catch (COMException) { return false; }
        }, Slow), "the third tab could not be shown");

    /// Brings the browser's window to the front if Windows lets this process (an Alt tap lifts the foreground lock; the Arm
    /// runner's first-boot screen may still hold it). True when the browser is in front afterwards.
    static bool BringToFront(Browser b, nint hwnd)
    {
        for (int attempt = 0; attempt < 2 && BrowserDiscovery.FrontBrowser() != b; attempt++)
        {
            NativeTest.keybd_event(0x12, 0, 0, 0); NativeTest.keybd_event(0x12, 0, 2, 0);
            _ = NativeTest.SetForegroundWindow(hwnd);
            if (WaitUntil(() => BrowserDiscovery.FrontBrowser() == b, TimeSpan.FromSeconds(3))) break;
            _ = NativeTest.ShowWindow(hwnd, NativeTest.SwMinimize);
            _ = NativeTest.ShowWindow(hwnd, NativeTest.SwRestore);
            WaitUntil(() => BrowserDiscovery.FrontBrowser() == b, TimeSpan.FromSeconds(3));
        }
        return BrowserDiscovery.FrontBrowser() == b;
    }

    /// The claim itself: pages open in background tabs are saved by the one read the engine arms for the moment they qualify,
    /// with no tab change, title change or any other event after the start. The page on screen is not saved.
    [Theory, MemberData(nameof(Browsers))]
    public void TheAgentSavesAPageLeftOpenInABackgroundTab(Browser b)
    {
        using var server = new FixtureServer();
        using var dir = new TempDir();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Count > 0 && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 3, Slow), "three tabs did not appear");
        ShowThirdTab(uia, b);
        bool inFront = BringToFront(b, WindowsOf(b)[0].Hwnd);
        output.WriteLine($"AGENT {b}: browser in front before the start = {inFront}");
        using var agent = AgentProcess.Start(dir.Path, f.Profile, output: output);

        Assert.True(WaitUntil(() => Saved(dir.Path, 1) is not null && Saved(dir.Path, 2) is not null, TimeSpan.FromSeconds(90)),
            "pages 1 and 2 were not saved by the armed read alone");
        ReportSaved("background tabs", b, dir.Path);
        foreach (var page in new[] { 1, 2 })
        {
            var item = Saved(dir.Path, page)!;
            Assert.Equal($"Fixture {page}", item.Title);
            Assert.True(item.Kind is Kind.Tools or Kind.Other, $"kind {item.Kind}");
            Assert.Equal(b, item.Browser);
        }
        if (inFront && BrowserDiscovery.FrontBrowser() == b) Assert.Null(Saved(dir.Path, 3));   // on screen, and far from its two minutes
        else output.WriteLine($"AGENT {b}: tab 3 not asserted, the browser was not in front (the Arm runner's first-boot screen holds it)");
        Assert.False(agent.Process.HasExited);
    }

    [Theory, MemberData(nameof(Browsers))]
    public void HandoffReleasesTheEngineAndTheAgentResumes(Browser b)
    {
        using var server = new FixtureServer();
        using var dir = new TempDir();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Count > 0 && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 3, Slow), "three tabs did not appear");
        ShowThirdTab(uia, b);
        using var agent = AgentProcess.Start(dir.Path, f.Profile, output: output);
        Func<nint> window = () => WindowsOf(b)[0].Hwnd;
        Assert.True(WaitUntil(() => Saved(dir.Path, 1) is not null && Saved(dir.Path, 2) is not null, TimeSpan.FromSeconds(90)), "pages 1 and 2 were not saved");

        using var app = new AppStandIn(Suffix(dir.Path));
        Assert.True(app.WaitAcquired());
        Assert.True(app.Got, "the agent did not give the engine up within 5 s");
        Assert.True(app.Took < TimeSpan.FromSeconds(2), $"handoff took {app.Took.TotalMilliseconds:F0} ms");
        Assert.False(agent.Process.HasExited);
        Assert.NotNull(ReadLibrary(dir.Path));   // the list the agent wrote while letting go parses

        // A new tab while the app holds the engine: nothing reads it.
        using var more = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(4)], profile: f.Profile, newWindow: false);
        Assert.True(WaitUntil(() => uia.Tabs(window()).Count >= 4, Slow), "the fourth tab did not appear");
        Assert.False(Poll(() => Saved(dir.Path, 4) is not null, TimeSpan.FromSeconds(15), uia, window), "the agent saved a page while the app held the engine");
        Assert.False(agent.Process.HasExited);

        app.Release();
        Assert.True(Poll(() => Saved(dir.Path, 4) is not null, TimeSpan.FromSeconds(60), uia, window), "the agent did not resume after the app let go");
        ReportSaved("handoff", b, dir.Path);
    }

    void ResumesAfterTheHoldingProcessEnds(Browser b, string flag, bool expectCrash)
    {
        using var server = new FixtureServer();
        using var dir = new TempDir();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Count > 0 && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 3, Slow), "three tabs did not appear");
        ShowThirdTab(uia, b);
        using var agent = AgentProcess.Start(dir.Path, f.Profile, output: output);
        Func<nint> window = () => WindowsOf(b)[0].Hwnd;
        Assert.True(WaitUntil(() => Saved(dir.Path, 1) is not null && Saved(dir.Path, 2) is not null, TimeSpan.FromSeconds(90)), "pages 1 and 2 were not saved");

        int page = expectCrash ? 5 : 6;
        using var holder = AgentProcess.Start(dir.Path, f.Profile, flag: flag, output: output);   // takes the engine, then ends (badly, or after three seconds)
        Assert.True(holder.Process.WaitForExit(40_000), "the lock holder did not end");
        Assert.True(File.Exists(Path.Combine(dir.Path, "held.txt")), "the lock holder never held the lock");
        if (expectCrash) Assert.NotEqual(0, holder.Process.ExitCode); else Assert.Equal(0, holder.Process.ExitCode);
        Assert.False(agent.Process.HasExited, "the agent ended with the app");

        using var more = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(page)], profile: f.Profile, newWindow: false);
        Assert.True(WaitUntil(() => uia.Tabs(window()).Count >= 4, Slow), "the fourth tab did not appear");
        Assert.True(Poll(() => Saved(dir.Path, page) is not null, TimeSpan.FromSeconds(60), uia, window), $"the agent did not resume and save page {page}");
        ReportSaved(flag, b, dir.Path);
    }

    [Theory, MemberData(nameof(Browsers))]
    public void AnAppCrashResumesTheAgent(Browser b) => ResumesAfterTheHoldingProcessEnds(b, "--test-hold-lock", expectCrash: true);

    [Theory, MemberData(nameof(Browsers))]
    public void ACleanAppExitResumesTheAgent(Browser b) => ResumesAfterTheHoldingProcessEnds(b, "--test-hold-lock-clean", expectCrash: false);

    [Fact]
    public void TheAgentDoesNothingBeforeOnboarding()
    {
        using var dir = new TempDir();
        using var agent = AgentProcess.Start(dir.Path, dir.At("no-profile"), onboarded: false);
        Assert.True(agent.Process.WaitForExit(3000), "the agent kept running before onboarding");
        Assert.Equal(0, agent.Process.ExitCode);
        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(dir.Path).Select(p => Path.GetFileName(p)!).ToArray());   // only what the test wrote
    }

    [Fact]
    public void ASecondAgentLeavesAtOnceAndTouchesNothingWhileTheFirstKeepsTheEngine()
    {
        using var dir = new TempDir();
        using var first = AgentProcess.Start(dir.Path, dir.At("no-profile"), output: output);
        Assert.True(WaitUntil(() => Watching(first), Slow), "the first agent never started its engine");
        var before = Directory.GetFileSystemEntries(dir.Path).Select(p => Path.GetFileName(p)!).Order().ToArray();
        using var second = AgentProcess.Start(dir.Path, dir.At("no-profile"), output: output);
        Assert.True(second.Process.WaitForExit(3000), "a second agent kept running");
        Assert.Equal(0, second.Process.ExitCode);
        Assert.Equal(before, Directory.GetFileSystemEntries(dir.Path).Select(p => Path.GetFileName(p)!).Order().ToArray());
        Assert.False(first.Process.HasExited);
        Assert.True(Watching(first));
        using var app = new AppStandIn(Suffix(dir.Path));   // the first agent still answers the app, so nobody else holds the lock
        Assert.True(app.WaitAcquired());
        Assert.True(app.Got, "the first agent did not give the engine up");
    }

    [Fact]
    public void ADamagedListIsLeftToTheAppAndSaidSo()
    {
        using var dir = new TempDir();
        var damaged = "{garbage"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir.Path, "library.json"), damaged);
        using var agent = AgentProcess.Start(dir.Path, dir.At("no-profile"), output: output);
        var statusFile = new AgentStatusFile(Path.Combine(dir.Path, "agent-status.json"));
        Assert.True(WaitUntil(() => statusFile.Read() is not null, Slow), "no status was written");
        Assert.Equal(AgentState.NeedsRecovery, statusFile.Read()!.State);
        Thread.Sleep(3000);
        Assert.False(Watching(agent), "the agent watched with a damaged list");
        Assert.Equal(damaged, File.ReadAllBytes(Path.Combine(dir.Path, "library.json")));
        Assert.DoesNotContain(Directory.GetFiles(dir.Path), f => Path.GetFileName(f).StartsWith("library.corrupt-", StringComparison.Ordinal));   // set aside by the app, not here
        using var app = new AppStandIn(Suffix(dir.Path));   // the idle agent still lets the app have the lock
        Assert.True(app.WaitAcquired());
        Assert.True(app.Got && app.Took < TimeSpan.FromSeconds(2));
        Assert.Equal(AgentState.NeedsRecovery, statusFile.Read()!.State);   // for the app to show and clear
    }

    [Fact]
    public void AListThatIsOnlyLockedIsAskedForAgainAndThenWatched()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "library.json");
        File.WriteAllBytes(path, LibraryJson.Encode(new LibraryData()));
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            using var agent = AgentProcess.Start(dir.Path, dir.At("no-profile"), output: output);
            Thread.Sleep(5000);   // several tries of two seconds
            Assert.False(agent.Process.HasExited);
            Assert.False(Watching(agent), "the agent watched with a list it could not read");
            Assert.False(File.Exists(Path.Combine(dir.Path, "agent-status.json")));   // not damaged: nothing to tell the app
            locked.Dispose();
            Assert.True(WaitUntil(() => Watching(agent), Slow), "the agent never came back to the list");
        }
        finally { locked.Dispose(); }
    }

    [Fact]
    public void NoWindowIsCreated()
    {
        using var dir = new TempDir();
        using var agent = AgentProcess.Start(dir.Path, dir.At("no-profile"), output: output);
        List<(string Class, bool Visible)> Windows() => WindowsOfProcess(agent.Process);
        Assert.True(WaitUntil(() => Windows().Any(w => w.Class == "Later.Hidden"), Slow), "the session window never appeared");
        Thread.Sleep(2000);
        var all = Windows();
        output.WriteLine($"AGENT windows: {string.Join(", ", all.Select(w => $"{w.Class}{(w.Visible ? " (visible)" : "")}"))}");
        Assert.DoesNotContain(all, w => w.Visible);
        Assert.Contains(all, w => w.Class == "Later.Hidden");
        // Windows' own hidden helpers (input method windows) may be listed too; none of them is visible.
        agent.Process.Refresh();
        Assert.Equal(0, (long)agent.Process.MainWindowHandle);
    }
}
