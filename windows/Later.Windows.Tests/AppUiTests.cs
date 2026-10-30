using System.Diagnostics;
using Later.Core;
using Later.Windows.Engine;
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

        public static RunningApp Start(string dataDir, params string[] args)
        {
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LATER_DATA_DIR"] = dataDir;
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
    public void TheSampleWindowNamesEverySectionWithItsCountAndTheStatusLineAndSearch()
    {
        using var dir = new TempDir();
        var now = DateTime.UtcNow;
        var counts = LibraryQuery.Counts(SampleLibrary.Data(now), now);
        using var app = RunningApp.Start(dir.Path, "--sample");
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

    [Fact]
    public void ASetUpAppTakesTheEngineLockOnItsEngineThreadAndReleasesItWhenTheWindowCloses()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.At("settings.json"), "{\"onboarded\":true}");
        using var gate = EngineLock.Open(new AppPaths(dir.Path).InstanceSuffix);
        using var app = RunningApp.Start(dir.Path);
        Wait(() => !gate.TryAcquireAndRelease(), Slow, "the app to hold the engine lock");   // a lock this thread cannot take is the app's
        Assert.True(app.Process.CloseMainWindow());
        Assert.True(app.Process.WaitForExit(15_000), "the app ends when its window closes");
        Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(5)), "the lock is free once the app has ended");
        gate.Release();
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
            var names = app.WaitForName(n => n.StartsWith("Later is busy saving", StringComparison.Ordinal), "the read-only notice");
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
