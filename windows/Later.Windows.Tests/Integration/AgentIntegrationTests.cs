using System.Diagnostics;
using System.Runtime.InteropServices;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Native;
using Later.Windows.Platform;
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
        AgentProcess(Process process) => Process = process;

        public static AgentProcess Start(string dataDir, string profile, bool onboarded = true, string flag = "--agent")
        {
            File.WriteAllText(Path.Combine(dataDir, "settings.json"), onboarded ? "{\"onboarded\":true}" : "{\"onboarded\":false}");
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe)! };
            psi.ArgumentList.Add(flag);
            psi.Environment["LATER_DATA_DIR"] = dataDir;
            psi.Environment["LATER_TEST_LEFT_OPEN_SECONDS"] = "5";
            psi.Environment["LATER_TEST_PROFILE"] = profile;
            return new AgentProcess(Process.Start(psi)!);
        }

        public void Dispose()
        {
            try { if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(10_000); } }
            catch (InvalidOperationException) { }
            Process.Dispose();
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
                    gate.SignalHandoff();
                    Got = gate.TryAcquire(TimeSpan.FromSeconds(5)); Took = timer.Elapsed;
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

    /// A page is left open in the background for five seconds, and it is saved at the next read; reads follow what changes in the
    /// browser. So the test changes the tab now and then (the way a user does) until the condition holds.
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

    void ReportSaved(string what, Browser b, string dir) =>
        output.WriteLine($"AGENT {what} {b}: saved pages = {string.Join(",", (ReadLibrary(dir)?.Items ?? []).Select(i => PageOf(i.Url)).Order())}");

    [Theory, MemberData(nameof(Browsers))]
    public void TheAgentSavesAPageLeftOpenInABackgroundTab(Browser b)
    {
        using var server = new FixtureServer();
        using var dir = new TempDir();
        using var f = BrowserFixture.Start(ExeOf(b), server, [FixtureServer.Url(1), FixtureServer.Url(2), FixtureServer.Url(3)]);
        using var uia = new UiaTest();
        Assert.True(WaitUntil(() => WindowsOf(b).Count > 0 && uia.Tabs(WindowsOf(b)[0].Hwnd).Count >= 3, Slow), "three tabs did not appear");
        using var agent = AgentProcess.Start(dir.Path, f.Profile);

        Assert.True(Poll(() => Saved(dir.Path, 1) is not null && Saved(dir.Path, 2) is not null, TimeSpan.FromSeconds(90), uia, () => WindowsOf(b)[0].Hwnd),
            "pages 1 and 2 were not saved");
        ReportSaved("background tabs", b, dir.Path);
        foreach (var page in new[] { 1, 2 })
        {
            var item = Saved(dir.Path, page)!;
            Assert.Equal($"Fixture {page}", item.Title);
            Assert.True(item.Kind is Kind.Tools or Kind.Other, $"kind {item.Kind}");
            Assert.Equal(b, item.Browser);
        }
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
        using var agent = AgentProcess.Start(dir.Path, f.Profile);
        Func<nint> window = () => WindowsOf(b)[0].Hwnd;
        Assert.True(Poll(() => Saved(dir.Path, 1) is not null && Saved(dir.Path, 2) is not null, TimeSpan.FromSeconds(90), uia, window), "pages 1 and 2 were not saved");

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
        using var agent = AgentProcess.Start(dir.Path, f.Profile);
        Func<nint> window = () => WindowsOf(b)[0].Hwnd;
        Assert.True(Poll(() => Saved(dir.Path, 1) is not null && Saved(dir.Path, 2) is not null, TimeSpan.FromSeconds(90), uia, window), "pages 1 and 2 were not saved");

        int page = expectCrash ? 5 : 6;
        using var holder = AgentProcess.Start(dir.Path, f.Profile, flag: flag);   // takes the engine, then ends (badly, or after three seconds)
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
    public void NoWindowIsCreated()
    {
        using var dir = new TempDir();
        using var agent = AgentProcess.Start(dir.Path, dir.At("no-profile"));
        var pid = (uint)agent.Process.Id;
        List<(string Class, bool Visible)> Windows()
        {
            var found = new List<(string, bool)>();
            User32.EnumWindows((hwnd, _) =>
            {
                _ = User32.GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == pid) found.Add((User32.Class(hwnd), User32.IsWindowVisible(hwnd) != 0));
                return 1;
            }, 0);
            return found;
        }
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
