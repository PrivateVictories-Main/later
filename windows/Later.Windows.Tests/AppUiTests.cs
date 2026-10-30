using System.Diagnostics;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Native;
using Later.Windows.Platform;
using Later.Windows.Tests.Integration;
using Xunit;

namespace Later.Windows.Tests;

/// The real `Later.exe` (a Debug build, LATER_EXE) window, read through UI Automation as assistive technology reads it. Every run has a
/// data folder of its own (LATER_DATA_DIR), so no real list, setting or lock is touched.
[Trait("Category", "Ui")]
public class AppUiTests
{
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(40);

    static string Exe => Environment.GetEnvironmentVariable("LATER_EXE") is { Length: > 0 } exe && File.Exists(exe)
        ? exe : throw new Xunit.Sdk.XunitException("LATER_EXE does not name the Debug Later.exe");

    /// The app's process and its window, with the UI Automation view of it.
    sealed class RunningApp : IDisposable
    {
        readonly UiaTest uia = new();
        public Process Process { get; }
        public nint Window { get; private set; }

        RunningApp(Process process) => Process = process;

        public static RunningApp Start(string dataDir, params string[] args) => StartWith(dataDir, null, args);

        /// `trace`: a file the app writes its own counts-and-timings events to (what it logs, never a link or a title).
        public static RunningApp StartWith(string dataDir, string? trace, params string[] args)
        {
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LATER_DATA_DIR"] = dataDir;
            if (trace is not null) psi.Environment["LATER_TEST_TRACE"] = trace;
            var app = new RunningApp(Process.Start(psi)!);
            Wait(() => { app.Process.Refresh(); return app.Process.HasExited || app.Process.MainWindowHandle != 0; }, Slow, "the window to appear");
            Assert.False(app.Process.HasExited, "Later.exe ended before showing a window");
            app.Window = app.Process.MainWindowHandle;
            return app;
        }

        public List<string> Names() => uia.Names(Window);

        public string? Title => uia.RootName(Window);

        /// Waits until a name that satisfies `wanted` is in the window, and returns every name then.
        public List<string> WaitForName(Func<string, bool> wanted, string what)
        {
            var names = new List<string>();
            Wait(() => { names = Names(); return names.Any(wanted); }, Slow, what);
            return names;
        }

        public void Dispose()
        {
            uia.Dispose();
            try { if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(10_000); } }
            catch (InvalidOperationException) { }
            Process.Dispose();
        }
    }

    static void Wait(Func<bool> condition, TimeSpan limit, string what)
    {
        var until = Stopwatch.StartNew();
        while (!condition())
        {
            if (until.Elapsed > limit) throw new Xunit.Sdk.XunitException($"timed out after {limit.TotalSeconds:0} s waiting for {what}");
            Thread.Sleep(100);
        }
    }

    [Fact]
    public void TheSampleWindowNamesEverySectionWithItsCountAndTheStatusLineAndSearch() =>
        AssertSampleWindow(SampleLibrary.NoonUtc(new DateTime(2026, 10, 15)), "--sample", "--sample-now", "2026-10-15T12:00:00");

    [Fact]
    public void TheSampleClockIsNoonTodayByDefaultSoTheFirstActivationMovesNothing() =>
        AssertSampleWindow(SampleLibrary.NoonUtc(DateTime.Now), "--sample");

    static void AssertSampleWindow(DateTime clock, params string[] args)
    {
        using var dir = new TempDir();
        var counts = LibraryQuery.Counts(SampleLibrary.Data(clock), clock);
        using var app = RunningApp.Start(dir.Path, args);
        var names = app.WaitForName(n => n == $"All, {counts[LibrarySection.All]}", "the sidebar");
        foreach (var section in LibrarySection.Ordered)
            Assert.Contains($"{section.Title}, {counts.GetValueOrDefault(section)}", names);
        Assert.Contains("Search saved pages", names);
        Assert.Contains("Group By", names);
        Assert.Contains("Sort By", names);
        Assert.Contains("Watching Helium and Chrome", names);
        Assert.Equal("Later", app.Title);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));   // a sample run touches no data at all
    }

    [Fact]
    public void TheSampleWindowListsTheSectionsPagesAndOneRowNamesItsSite()
    {
        using var dir = new TempDir();
        using var app = RunningApp.Start(dir.Path, "--sample", "--sample-section", "shopping");
        var names = app.WaitForName(n => n.StartsWith("Patagonia Nano Puff Jacket", StringComparison.Ordinal), "the first row");
        Assert.Contains(names, n => n.StartsWith("Patagonia Nano Puff Jacket - Men's | REI Co-op, rei.com, Shopping, saved ", StringComparison.Ordinal));
        Assert.Contains("Shopping", names);   // the list's heading
    }

    [Fact]
    public void ARealRunBeforeSetupWatchesNothingAndTouchesNothing()
    {
        using var dir = new TempDir();
        using var app = RunningApp.Start(dir.Path);
        app.WaitForName(n => n == "Starts watching after setup", "the status line");
        using var gate = EngineLock.Open(new AppPaths(dir.Path).InstanceSuffix);
        Assert.True(gate.TryAcquire(TimeSpan.Zero), "the app took the engine lock before setup");   // nothing watches, so nobody holds it
        gate.Release();
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    /// A list with one saved page and one whose address carries a one-time secret (which the app drops when it opens the list).
    static void WriteListWithASecret(TempDir dir)
    {
        var library = new Library();
        var verdict = new Verdict(true, Kind.Reading, "a.example.com/*");
        library.Save("https://a.example.com/reset-password/Xk9fA2bQ7zLm", "Reset", verdict, Browser.Helium, DateTime.UtcNow);
        library.Save("https://a.example.com/articles/slow-web", "Slow web", verdict, Browser.Helium, DateTime.UtcNow);
        new LibraryStore(dir.At("library.json")).Save(library.Data);
    }

    static string[] TitlesOnDisk(TempDir dir) => new LibraryStore(dir.At("library.json")).Peek("").Data.Items.Select(i => i.Title).ToArray();

    [Fact]
    public void ASetUpAppTakesTheEngineLockOnItsEngineThreadAndReleasesItCleanlyAfterSavingWhenTheWindowCloses()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");
        WriteListWithASecret(dir);
        using var gate = EngineLock.Open(new AppPaths(dir.Path).InstanceSuffix);
        using var app = RunningApp.Start(dir.Path);
        Wait(() => !gate.TryAcquireAndRelease(), Slow, "the app to hold the engine lock");   // a lock this thread cannot take is the app's
        Assert.True(app.Process.CloseMainWindow());
        Assert.True(app.Process.WaitForExit(15_000), "the app ends when its window closes");
        Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(5), out var abandoned), "the lock is free once the app has ended");
        Assert.False(abandoned, "the app ended holding the lock instead of releasing it");   // an abandoned mutex is also "free"
        gate.Release();
        Assert.Equal(["Slow web"], TitlesOnDisk(dir));   // what the app did to the list (it dropped the secret link) is on disk
    }

    [Fact]
    public void ClosingTheWindowWhileTheAgentStillHoldsTheLockStartsNoEngineAndEndsTheProcess()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");
        WriteListWithASecret(dir);
        using var gate = EngineLock.Open(new AppPaths(dir.Path).InstanceSuffix);
        Assert.True(gate.TryAcquire(TimeSpan.Zero));   // this thread plays the agent
        try
        {
            using var app = RunningApp.Start(dir.Path);
            Wait(() => NativeApi.WaitForSingleObject(gate.HandoffHandle, 0) == NativeApi.WaitObject0, Slow, "the app to ask for the lock");
            Assert.True(app.Process.CloseMainWindow());
            Thread.Sleep(100);
            gate.Release();   // the agent lets go while the window is closing: the app must not start watching now
            Assert.True(app.Process.WaitForExit(15_000), "the process does not end after its window closed");
            Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(5), out var abandoned));
            Assert.False(abandoned);
            gate.Release();
            Assert.Equal(["Reset", "Slow web"], TitlesOnDisk(dir).Order().ToArray());   // an engine would have dropped the secret link and saved
        }
        finally { try { gate.Release(); } catch (System.ComponentModel.Win32Exception) { } }
    }

    [Fact]
    public void TheProcessEndsWhenTheWindowIsClosedAtOnce()
    {
        for (int i = 0; i < 3; i++)
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");
            using var gate = EngineLock.Open(new AppPaths(dir.Path).InstanceSuffix);
            using var app = RunningApp.Start(dir.Path);
            Assert.True(app.Process.CloseMainWindow());
            // Under the 10 s watchdog, which would end a lost quit as a kill and so hide it; and a kill leaves the lock abandoned.
            Assert.True(app.Process.WaitForExit(8_000), $"the process outlived its window (run {i + 1})");
            Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(5), out var abandoned), "the lock is not free after the exit");
            Assert.False(abandoned, $"the exit was forced, not a clean quit (run {i + 1})");
            gate.Release();
        }
    }

    [Fact]
    public void AMinimizedWindowCountsAsHiddenSoNothingIsSentToItAndRestoringShowsItAgain()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");
        var trace = dir.At("trace.txt");
        using var app = RunningApp.StartWith(dir.Path, trace);
        string? LastShown()
        {
            string text;
            try { text = File.Exists(trace) ? File.ReadAllText(trace) : ""; } catch (IOException) { return null; }
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Contains(" Shown ", StringComparison.Ordinal))?.Split(' ').Last();
        }
        void Restore() { _ = NativeTest.ShowWindow(app.Window, NativeTest.SwRestore); _ = NativeTest.SetForegroundWindow(app.Window); }
        // The trace starts with the engine, so the first rounds may precede it; each round is minimize, look, restore.
        Wait(() =>
        {
            _ = NativeTest.ShowWindow(app.Window, NativeTest.SwMinimize);
            Thread.Sleep(700);
            if (LastShown() == "False") return true;
            Restore(); Thread.Sleep(700);
            return false;
        }, Slow, "a minimized window to be reported hidden");
        Wait(() => { Restore(); Thread.Sleep(500); return LastShown() == "True"; }, Slow, "the restored window to be reported shown");
    }

    [Fact]
    public void WhatTheAgentCouldNotDoIsSaidInTheWindowAndClearedOnceTaken()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");
        File.WriteAllText(dir.At("agent-status.json"), "{\"state\":\"cantSave\",\"count\":3}");
        using var app = RunningApp.Start(dir.Path);
        app.WaitForName(n => n.Contains("Later couldn't save while it was closed", StringComparison.Ordinal), "the agent's notice");
        Assert.False(File.Exists(dir.At("agent-status.json")), "the app that took over clears what the agent reported");
    }

    [Fact]
    public void AListThatStaysLockedIsShownReadOnlyWithTheNoticeAndTakenOverWhenTheLockIsFree()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");
        var library = new Library();
        library.Save("https://a.example.com/articles/slow-web", "Slow web", new Verdict(true, Kind.Reading, "a.example.com/*"), Browser.Helium, DateTime.UtcNow);
        new LibraryStore(dir.At("library.json")).Save(library.Data);
        using var gate = EngineLock.Open(new AppPaths(dir.Path).InstanceSuffix);
        Assert.True(gate.TryAcquire(TimeSpan.Zero));   // this thread plays an agent that never lets go
        try
        {
            using var app = RunningApp.Start(dir.Path);
            // The whole wait for the lock (5 s): the window must never say "nothing saved" (or "Nothing here yet") for a list that has pages.
            var names = new List<string>();
            Wait(() =>
            {
                names = app.Names();
                Assert.DoesNotContain("Close tabs freely.", names);
                Assert.DoesNotContain("Nothing here yet", names);
                return names.Any(n => n.StartsWith("Later is busy saving", StringComparison.Ordinal));
            }, Slow, "the read-only notice");
            Assert.Contains(names, n => n.StartsWith("Slow web, a.example.com, Reading, saved ", StringComparison.Ordinal));   // the list is there
            gate.Release();
            // Coming back to the window asks again, and the lock is free now. Restoring the window activates it.
            Wait(() =>
            {
                _ = NativeTest.ShowWindow(app.Window, NativeTest.SwMinimize);
                _ = NativeTest.ShowWindow(app.Window, NativeTest.SwRestore);
                _ = NativeTest.SetForegroundWindow(app.Window);
                Thread.Sleep(1000);
                return !gate.TryAcquireAndRelease();
            }, Slow, "the app to take the lock once it was free");
            Wait(() => !app.Names().Any(n => n.StartsWith("Later is busy saving", StringComparison.Ordinal)), Slow, "the read-only notice to go");
        }
        finally { try { gate.Release(); } catch (System.ComponentModel.Win32Exception) { } }
    }
}

static class EngineLockTestExtensions
{
    /// True when this thread could take the lock (and has given it back); false when another thread or process holds it.
    public static bool TryAcquireAndRelease(this EngineLock gate)
    {
        if (!gate.TryAcquire(TimeSpan.Zero)) return false;
        gate.Release();
        return true;
    }
}
