using System.Diagnostics;
using Later.App.ViewModels;
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

        /// The names of the list's items (rows and headings), in the list's order.
        public List<string> ListItems() => uia.NamesOf(Window, UiaTest.ListItemType);

        public string? NameOfId(string automationId) => uia.NameOfId(Window, automationId);

        public string FocusedElement() => uia.FocusedElement();

        public bool Invoke(string name, int controlType = UiaTest.ButtonType) => uia.Invoke(Window, name, controlType);

        public bool Choose(string name) => uia.ChooseInProcess(Process.Id, name);

        public bool SelectRow(Func<string, bool> which, bool add = false) => uia.SelectRow(Window, which, add);

        /// Keys go to the foreground window.
        public void Focus() => _ = NativeTest.SetForegroundWindow(Window);

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

    // MARK: The list, the inspector and the actions (a sample run: its own list, edited in the window, nothing on disk)

    static readonly DateTime SampleClock = SampleLibrary.NoonUtc(new DateTime(2026, 10, 15));

    /// A sample window as tall as the whole list, so every row is laid out and so is in the UI Automation tree (a list shows only the
    /// rows it draws).
    static RunningApp SampleApp(TempDir dir, params string[] more) =>
        RunningApp.Start(dir.Path, ["--sample", "--sample-now", "2026-10-15T12:00:00", "--shot-size", "1100x2400", .. more]);

    /// The headings and rows a section, search and grouping show, as the list names them: worked out with the same clock, zone and culture.
    static List<object> Expected(LibrarySection section, string search = "", LibraryGrouping grouping = LibraryGrouping.Date)
    {
        var library = SampleLibrary.Build(SampleClock);
        var listing = LibraryListing.Build(library.Data, SampleLibrary.OpenCopies(library), library.PageKey, section, search, SampleClock, grouping, LibrarySort.LastSeen, false,
                                           new HashSet<string>(), TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        bool searching = search.Trim().Length > 0;
        return ListRows.Build(listing, section, searching, LibraryListing.EffectiveGrouping(section, grouping), !searching, LibrarySort.LastSeen, SampleClock, _ => false);
    }

    static Dictionary<LibrarySection, int> SampleCounts() => LibraryQuery.Counts(SampleLibrary.Data(SampleClock), SampleClock);

    /// Waits for the list to name exactly these entries, in this order.
    static void WaitForList(RunningApp app, List<object> expected, string what)
    {
        var names = expected.Select(e => e.ToString()!).ToList();
        var wanted = names.ToHashSet();
        var shown = new List<string>();
        Wait(() => { shown = app.ListItems().Where(wanted.Contains).ToList(); return shown.Count == names.Count; }, Slow, what);
        Assert.Equal(names, shown);
    }

    static void WaitForCounts(RunningApp app, params (LibrarySection Section, int Count)[] counts) =>
        Wait(() => { var names = app.Names(); return counts.All(c => names.Contains($"{c.Section.Title}, {c.Count}")); }, Slow, "the sidebar to say " + string.Join(", ", counts.Select(c => $"{c.Section.Title} {c.Count}")));

    /// Tries until the element is there and takes the action: the window may still be laying out.
    static void Do(Func<bool> action, string what, RunningApp? app = null)
    {
        try { Wait(() => { try { return action(); } catch (System.Runtime.InteropServices.COMException) { return false; } }, Slow, what); }
        catch (Xunit.Sdk.XunitException e) when (app is not null) { throw new Xunit.Sdk.XunitException(e.Message + " | the window names: " + string.Join("; ", app.Names().Take(60))); }
    }

    static void Undo(RunningApp app)
    {
        app.Focus();
        Thread.Sleep(200);   // the window has the keyboard before it is pressed
        bool offered = app.Names().Contains("Undo");   // the bar that offers it, for a few seconds after a change
        NativeTest.Press((byte)'Z', control: true);
        if (offered)
            Wait(() => !app.Names().Contains("Undo"), TimeSpan.FromSeconds(5),
                 $"Ctrl+Z to reach the window (it is {(NativeTest.GetForegroundWindow() == app.Window ? "" : "not ")}the foreground window; the keyboard is on: {app.FocusedElement()})");
    }

    [Fact]
    public void TheListShowsEveryPageUnderItsHeadingsInOrder()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        var expected = Expected(LibrarySection.All);
        WaitForList(app, expected, "every heading and row");
        Assert.Equal(SampleCounts()[LibrarySection.All], expected.OfType<RowModel>().Count());   // as many rows as the sidebar says pages
        Assert.Equal(["Today", "Yesterday", "Previous 7 Days", "Previous 30 Days"], expected.OfType<GroupHeaderRow>().Select(h => h.Title).ToArray());
    }

    [Fact]
    public void SelectingARowShowsItsTitleItsWhyLineAndTheActionsByName()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        const string title = "Patagonia Nano Puff Jacket - Men's | REI Co-op";
        app.WaitForName(n => n.StartsWith(title, StringComparison.Ordinal), "the first row");
        Do(() => app.SelectRow(n => n.StartsWith(title, StringComparison.Ordinal)), "the row to be selectable");
        Wait(() => app.NameOfId("InspectorTitle") == title, Slow, "the inspector to show the title");
        var names = app.WaitForName(n => n == "Copy Link", "the inspector's actions");
        Assert.Contains("New site", names);   // why the page is kept
        foreach (var action in new[] { "Copy Link", "Mark Done", "Move to", "Not an Idea", "Delete" }) Assert.Contains(action, names);
        Assert.Contains(names, n => n == "Open" || n.StartsWith("Open in ", StringComparison.Ordinal));
        Assert.DoesNotContain("Keep Pages Like This", names);   // a page that is a thing has no regular choice
    }

    [Fact]
    public void MarkDoneMovesAPageFromAllToDoneAndCtrlZBringsItBack()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        var counts = SampleCounts();
        int all = counts[LibrarySection.All], done = counts[LibrarySection.Done];
        WaitForCounts(app, (LibrarySection.All, all), (LibrarySection.Done, done));
        var first = Expected(LibrarySection.All).OfType<RowModel>().First();
        Do(() => app.SelectRow(n => n == first.Name), "the first row");
        Do(() => app.Invoke("Mark Done"), "the Mark Done button");
        WaitForCounts(app, (LibrarySection.All, all - 1), (LibrarySection.Done, done + 1));
        Undo(app);
        WaitForCounts(app, (LibrarySection.All, all), (LibrarySection.Done, done));
    }

    [Fact]
    public void DeletingAPageSelectsTheNextOneAndUndoPutsItBackWhereItWas()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        var rows = Expected(LibrarySection.All).OfType<RowModel>().ToList();
        WaitForList(app, Expected(LibrarySection.All), "every heading and row");
        var target = rows[1];
        Do(() => app.SelectRow(n => n == target.Name), "the second row");
        Do(() => app.Invoke("Delete"), "the Delete button");
        Wait(() => !app.ListItems().Contains(target.Name), Slow, "the row to go");
        Assert.Equal(rows.Where(r => r != target).Select(r => r.Name).ToList(), app.ListItems().Where(n => n.Contains(", saved ", StringComparison.Ordinal)).ToList());
        Wait(() => app.NameOfId("InspectorTitle") == rows[2].Title, Slow, "the selection to move on to the next page");
        Undo(app);
        Wait(() => app.ListItems().Contains(target.Name), Slow, "Undo to bring the row back");
        Assert.Equal(rows.Select(r => r.Name).ToList(), app.ListItems().Where(n => n.Contains(", saved ", StringComparison.Ordinal)).ToList());   // at its old position
    }

    [Fact]
    public void TypingIntoTheSearchBoxFiltersTheRowsAndTheHeadingsBecomeSavedAndDone()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        app.WaitForName(n => n == "Today, 4 pages", "the list");
        app.Focus();
        NativeTest.Press((byte)'F', control: true);   // Ctrl+F: the search box
        Thread.Sleep(300);
        NativeTest.Type("youtube");
        WaitForList(app, Expected(LibrarySection.All, "youtube"), "the search results");
        var names = app.Names();
        Assert.Contains("Saved, 1 page", names);
        Assert.Contains("Done, 1 page", names);
        Assert.DoesNotContain("Today, 4 pages", names);
    }

    [Fact]
    public void TypingALetterInTheListStartsASearchWithIt()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        app.WaitForName(n => n == "Today, 4 pages", "the list");
        app.Focus();
        NativeTest.Type("youtube");   // the keyboard starts in the list: the first letter starts the search and the rest follow it
        WaitForList(app, Expected(LibrarySection.All, "youtube"), "the search results");
    }

    [Fact]
    public void GroupBySiteGivesEachSiteItsOwnHeading()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        app.WaitForName(n => n == "Today, 4 pages", "the list");
        Do(() => app.Invoke("Group By"), "the Group By button");
        Do(() => app.Choose("Site"), "the Site choice", app);
        var expected = Expected(LibrarySection.All, grouping: LibraryGrouping.Site);
        WaitForList(app, expected, "a heading for every site");
        Assert.Equal(expected.OfType<RowModel>().Select(r => r.Site).Distinct().Count(), expected.OfType<GroupHeaderRow>().Count());   // one heading per host
    }

    [Fact]
    public void ChangingSeveralPagesAtOnceIsOneUndo()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir);
        var counts = SampleCounts();
        int all = counts[LibrarySection.All], done = counts[LibrarySection.Done];
        var rows = Expected(LibrarySection.All).OfType<RowModel>().Take(3).ToList();
        WaitForCounts(app, (LibrarySection.All, all));
        Do(() => app.SelectRow(n => n == rows[0].Name), "the first row");
        Do(() => app.SelectRow(n => n == rows[1].Name, add: true), "the second row");
        Do(() => app.SelectRow(n => n == rows[2].Name, add: true), "the third row");
        Wait(() => app.NameOfId("InspectorTitle") == "3 Pages", Slow, "the summary of three pages");
        Do(() => app.Invoke("Mark Done"), "the Mark Done button");
        WaitForCounts(app, (LibrarySection.All, all - 3), (LibrarySection.Done, done + 3));
        Undo(app);
        WaitForCounts(app, (LibrarySection.All, all), (LibrarySection.Done, done));   // one Undo, not three
    }

    [Fact]
    public void ARegularPageOffersKeepPagesLikeThisWithItsWhyLineAndUndoBringsItBack()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir, "--sample-section", "regulars", "--sample-select", "1");
        var counts = SampleCounts();
        int all = counts[LibrarySection.All], regulars = counts[LibrarySection.Regulars];
        var names = app.WaitForName(n => n == "Keep Pages Like This", "the regular's choice");
        Assert.Contains(names, n => n.StartsWith("Regular · visited ", StringComparison.Ordinal));   // the Mac 1.4 why line
        Assert.DoesNotContain("Mark Done", names);   // a regular still in the list has its own choice instead
        Do(() => app.Invoke("Keep Pages Like This"), "the Keep Pages Like This button");
        WaitForCounts(app, (LibrarySection.All, all + 1), (LibrarySection.Regulars, regulars - 1));
        Undo(app);
        WaitForCounts(app, (LibrarySection.All, all), (LibrarySection.Regulars, regulars));
    }

    [Fact]
    public void AKeptPlaceOffersThisIsARegularAndUndoTakesItBack()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir, "--sample-section", "tools");
        var counts = SampleCounts();
        int all = counts[LibrarySection.All], regulars = counts[LibrarySection.Regulars], tools = counts[LibrarySection.OfKind(Kind.Tools)];
        app.WaitForName(n => n.StartsWith("Tiny Tools", StringComparison.Ordinal), "the place's row");
        Do(() => app.SelectRow(n => n.StartsWith("Tiny Tools", StringComparison.Ordinal)), "the place's row");
        Do(() => app.Invoke("This Is a Regular"), "the This Is a Regular button");
        WaitForCounts(app, (LibrarySection.All, all - 1), (LibrarySection.Regulars, regulars + 1), (LibrarySection.OfKind(Kind.Tools), tools - 1));
        Undo(app);
        WaitForCounts(app, (LibrarySection.All, all), (LibrarySection.Regulars, regulars), (LibrarySection.OfKind(Kind.Tools), tools));
    }

    [Fact]
    public void EmptyDoneAsksFirstDeletesWhatDoneShowsAsOneUndo()
    {
        using var dir = new TempDir();
        using var app = SampleApp(dir, "--sample-section", "done");
        int done = SampleCounts()[LibrarySection.Done];
        WaitForCounts(app, (LibrarySection.Done, done));
        Do(() => app.Invoke("Empty Done…"), "the Empty Done button");
        Do(() => app.Invoke("Cancel"), "the question's Cancel");   // asked first: Cancel changes nothing
        Thread.Sleep(500);
        Assert.Contains($"Done, {done}", app.Names());
        Do(() => app.Invoke("Empty Done…"), "the Empty Done button");
        Do(() => app.Invoke("Empty Done"), "the question's Empty Done");
        WaitForCounts(app, (LibrarySection.Done, 0));
        Undo(app);
        WaitForCounts(app, (LibrarySection.Done, done));
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
