using Later.App;
using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Platform;
using Xunit;

namespace Later.Windows.Tests;

/// Settings and the first-run welcome through their view models (not the window): what each control changes, what is read back, and that a
/// sample run (no engine, no settings path) writes nothing. The engine's side of it, the settings file, is in `AppEngineTests`.
public sealed class SettingsRoundTripTests : IDisposable
{
    sealed class Recorder : IWatchControls
    {
        public List<string> Calls { get; } = [];
        public void SetPaused(bool value) => Calls.Add($"paused {value}");
        public void SetSavesFeedHomes(bool value) => Calls.Add($"feed {value}");
        public void SetView(LibraryGrouping grouping, LibrarySort sort, bool reversed) => Calls.Add($"view {grouping} {sort} {reversed}");
    }

    const BrowserStatus Up = BrowserStatus.Watching, Idle = BrowserStatus.NotRunning, Gone = BrowserStatus.NotInstalled, Broken = BrowserStatus.ReadFailed;

    readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    static LibraryViewModel Library(BrowserStatus helium = Up, BrowserStatus chrome = Up, bool paused = false, bool onboarded = true)
    {
        var library = new LibraryViewModel();
        library.SetEngineState(new EngineState(new Dictionary<Browser, BrowserStatus> { [Browser.Helium] = helium, [Browser.Chrome] = chrome }, paused, null, true, onboarded));
        return library;
    }

    static SettingsModel Model(LibraryViewModel? library = null, IWatchControls? engine = null, IStartAtSignIn? start = null, IFirewallBlock? firewall = null,
                               string? path = null, Func<Browser, string?>? exe = null) =>
        new(library ?? Library(), engine, start ?? new InMemoryStartAtSignIn(), firewall ?? new InMemoryFirewallBlock(), exe ?? (_ => null), path, "1.4.0");

    // MARK: The three settings that change what is watched or shown

    [Fact]
    public void EachControlGoesToTheEnginesSideAndTheModelShowsItAtOnce()
    {
        var engine = new Recorder();
        var library = Library();
        var model = Model(library, engine);
        int changes = 0;
        model.Changed += () => changes++;

        model.SetPaused(true);
        model.SetSavesFeedHomes(true);
        model.SetSort(LibrarySort.Title);

        Assert.True(model.Paused);
        Assert.True(model.SavesFeedHomes);
        Assert.Equal(LibrarySort.Title, model.Sort);
        Assert.Equal(LibrarySort.Title, library.Sort);   // the toolbar's own order is the same one
        Assert.Equal(["paused True", "feed True", "view Date Title False"], engine.Calls);
        Assert.True(changes >= 2);
    }

    [Fact]
    public void ASampleRunHasNoEngineAndNoPathAndWritesNothing()
    {
        var model = Model(path: null);
        model.SetPaused(true);
        model.SetSavesFeedHomes(true);
        model.SetSort(LibrarySort.Site);
        model.SetStart(true);
        Assert.True(model.Paused);
        Assert.Equal("Paused", model.Summary.Text);   // the summary follows the switch
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void WithNoEngineAskedTheSavedSettingsAreWhatTheModelShows()
    {
        var path = dir.At("settings.json");
        SettingsStore.Save(path, new Settings(Onboarded: true, Paused: true, SavesFeedHomes: true, Sort: LibrarySort.Title));
        var model = Model(new LibraryViewModel(), path: path);   // no statuses: nobody holds the engine
        Assert.True(model.Paused);
        Assert.True(model.SavesFeedHomes);
        Assert.Equal(["Not installed", "Not installed"], model.Browsers.Select(b => b.Status));   // nothing is known of the browsers either
    }

    [Fact]
    public void ResumeFromTheSidebarWhileSettingsIsOpenShowsOffButTheEnginesRepeatedWordDoesNotUndoTheUsersSwitch()
    {
        var library = Library(paused: true);
        var model = Model(library, new Recorder());
        Assert.True(model.Paused);
        library.SetEngineState(new EngineState(new Dictionary<Browser, BrowserStatus> { [Browser.Helium] = Up, [Browser.Chrome] = Up }, false, null, true, true));
        Assert.False(model.Paused);   // the sidebar's Resume

        model.SetPaused(true);
        library.SetEngineState(new EngineState(new Dictionary<Browser, BrowserStatus> { [Browser.Helium] = Up, [Browser.Chrome] = Idle }, false, null, true, true));
        Assert.True(model.Paused);   // the engine has not caught up yet and says what it said before
        library.SetEngineState(new EngineState(new Dictionary<Browser, BrowserStatus> { [Browser.Helium] = Up, [Browser.Chrome] = Idle }, true, null, true, true));
        Assert.True(model.Paused);
    }

    [Fact]
    public void TheDefaultOrderListsTheSevenOrdersByTheMacsTitles()
    {
        Assert.Equal(["Tab Order (Left to Right)", "Tab Order (Right to Left)", "Date Opened", "Date Saved", "Last Seen", "Title", "Site"], SettingsModel.SortTitles);
        Assert.Equal(Enum.GetValues<LibrarySort>().Length, SettingsModel.SortTitles.Count);
    }

    // MARK: Header and browsers

    [Fact]
    public void TheSummaryTellsWhatLaterIsDoingInTheMacsWords()
    {
        StatusSummary Of(LibraryViewModel library) => Model(library).Summary;
        Assert.Equal(new StatusSummary("Watching Helium and Chrome", Tone.Good), Of(Library()));
        Assert.Equal(new StatusSummary("Watching Chrome", Tone.Good), Of(Library(helium: Idle)));
        Assert.Equal(new StatusSummary("Paused", Tone.Warning), Of(Library(paused: true)));
        Assert.Equal(new StatusSummary("Needs your attention below", Tone.Warning), Of(Library(chrome: Broken)));
        Assert.Equal(new StatusSummary("Waiting for a browser to open", Tone.Quiet), Of(Library(Idle, Idle)));
        Assert.Equal(new StatusSummary("Waiting for a browser to open", Tone.Quiet), Of(Library(Gone, Gone)));
        Assert.Equal(new StatusSummary("Starts watching after setup", Tone.Warning), Of(Library(onboarded: false)));
    }

    [Fact]
    public void EachBrowserSaysWatchingNotOpenNotInstalledOrCantReadTabs()
    {
        var lines = Model(Library(Up, Broken), exe: b => b == Browser.Chrome ? @"C:\chrome.exe" : null).Browsers;
        Assert.Equal([("Helium", "Watching", Tone.Good, (string?)null), ("Chrome", "Can't read tabs", Tone.Warning, @"C:\chrome.exe")], lines.Select(l => (l.Name, l.Status, l.Tone, l.ExePath)));
        Assert.Equal(["Not open right now", "Not installed"], Model(Library(Idle, Gone)).Browsers.Select(l => l.Status));
    }

    [Fact]
    public void WithoutTheEnginesWordAnInstalledBrowserIsNotOpenAndAMissingOneNotInstalled()
    {
        var model = Model(new LibraryViewModel(), exe: b => b == Browser.Chrome ? @"C:\chrome.exe" : null);
        Assert.Equal(["Not installed", "Not open right now"], model.Browsers.Select(l => l.Status));
    }

    // MARK: Start at sign-in

    [Fact]
    public void StartAtSignInTurnsOnAndOffAndTheModelReadsWhatWindowsSays()
    {
        var start = new InMemoryStartAtSignIn();
        var model = Model(start: start);
        Assert.False(model.StartIsOn);
        model.SetStart(true);
        Assert.True(model.StartIsOn);
        Assert.Null(model.StartNote);
        model.SetStart(false);
        Assert.False(model.StartIsOn);
        Assert.Equal(2, start.Writes);
    }

    [Fact]
    public void AStartTheUserTurnedOffInStartupAppsReadsOffWithANoteAndStaysSo()
    {
        var start = new InMemoryStartAtSignIn(StartState.DisabledByUser);
        var model = Model(start: start);
        Assert.False(model.StartIsOn);
        Assert.Equal("Turned off in Startup apps.", model.StartNote);
        model.SetStart(true);   // Windows keeps the user's choice
        Assert.Equal(StartState.DisabledByUser, model.StartState);
        Assert.Equal("Turned off in Startup apps.", model.StartNote);
        Assert.Equal("ms-settings:startupapps", SettingsModel.StartupAppsUri);
    }

    [Fact]
    public void WhenWindowsDoesNotAcceptTheChangeTheNoteSaysWhyAndTheSwitchStaysWhereWindowsIs()
    {
        var start = new InMemoryStartAtSignIn { FailWith = new UnauthorizedAccessException("Access to the registry key is denied.") };
        var model = Model(start: start);
        model.SetStart(true);
        Assert.False(model.StartIsOn);
        Assert.Equal("Windows didn't accept the change: Access to the registry key is denied. Try again, or use Startup apps in Settings.", model.StartNote);

        start.FailWith = new IOException("");
        model.SetStart(true);
        Assert.Equal("Windows didn't accept the change. Try again, or use Startup apps in Settings.", model.StartNote);

        start.FailWith = null;
        model.SetStart(true);
        Assert.True(model.StartIsOn);
        Assert.Null(model.StartNote);
    }

    // MARK: Firewall

    [Fact]
    public async Task TheFirewallSwitchShowsTheRuleAfterTheChangeNeverTheHope()
    {
        var firewall = new InMemoryFirewallBlock();
        var model = Model(firewall: firewall);
        await model.LoadFirewallAsync();
        Assert.True(model.FirewallAvailable);
        Assert.False(model.FirewallBlocked);

        await model.SetFirewallAsync(true);
        Assert.True(model.FirewallBlocked);
        Assert.Null(model.FirewallNote);
        Assert.False(model.FirewallBusy);

        firewall.Answer = FirewallResult.Cancelled;   // the user said no to Windows' prompt
        await model.SetFirewallAsync(false);
        Assert.True(model.FirewallBlocked);   // nothing changed, and the switch says so
        Assert.Equal("Windows asked for your OK and it was declined.", model.FirewallNote);

        firewall.Answer = FirewallResult.Failed;
        await model.SetFirewallAsync(false);
        Assert.Equal("Windows didn't change the firewall. Try again.", model.FirewallNote);

        firewall.Answer = null;
        await model.SetFirewallAsync(false);
        Assert.False(model.FirewallBlocked);
        Assert.Null(model.FirewallNote);
    }

    [Fact]
    public async Task WithoutAFirewallTheSwitchIsOffAndSaysSo()
    {
        var model = Model(firewall: new InMemoryFirewallBlock(available: false));
        await model.LoadFirewallAsync();
        Assert.False(model.FirewallAvailable);
        Assert.False(model.FirewallBlocked);
        Assert.Equal("Windows Firewall isn't available on this PC.", model.FirewallNote);
    }

    // MARK: The first-run welcome

    static List<OnboardingBrowser> Browsers(bool helium = true, bool chrome = true) =>
        OnboardingModel.Discover(b => b == Browser.Helium ? (helium ? @"C:\helium.exe" : null) : (chrome ? @"C:\chrome.exe" : null), b => b == Browser.Helium);

    [Fact]
    public void TheWelcomeListsTheInstalledBrowsersAndWhetherEachIsOpen()
    {
        Assert.Equal([("Helium", "Open"), ("Chrome", "Not open")], Browsers().Select(b => (b.Name, b.Status)));
        Assert.Equal(["Chrome"], Browsers(helium: false).Select(b => b.Name));
        Assert.Empty(Browsers(false, false));
        Assert.Equal("Later works with Helium and Chrome. Install one, then open Later again.", OnboardingModel.NoBrowserText);
    }

    [Fact]
    public void StartUsingLaterMarksSetupDoneKeepsTheOtherSettingsAndTurnsOnStarting()
    {
        var path = dir.At("settings.json");
        SettingsStore.Save(path, new Settings(Sort: LibrarySort.Title, SavesFeedHomes: true));
        var start = new InMemoryStartAtSignIn();
        var welcome = new OnboardingModel(Browsers(), start, path);
        Assert.True(welcome.Start());
        var saved = SettingsStore.Load(path);
        Assert.Equal(new Settings(Onboarded: true, SavesFeedHomes: true, Sort: LibrarySort.Title), saved);
        Assert.Equal(StartState.On, start.State);
        Assert.Null(welcome.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path).Where(f => !f.EndsWith("settings.json", StringComparison.Ordinal)));   // no scratch file is left beside it
    }

    [Fact]
    public void ASampleWelcomeWritesNothingAndStartsNothing()
    {
        var start = new InMemoryStartAtSignIn();
        Assert.True(new OnboardingModel(Browsers(), start, null).Start());
        Assert.Equal(StartState.Off, start.State);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void IfTheSettingsCannotBeSavedTheWelcomeStaysAndSaysSo()
    {
        File.WriteAllText(dir.At("blocker"), "a file where the folder should be");
        var start = new InMemoryStartAtSignIn();
        var welcome = new OnboardingModel(Browsers(), start, dir.At("blocker/settings.json"));
        Assert.False(welcome.Start());
        Assert.StartsWith("Later couldn't save its settings", welcome.Failure);
        Assert.Equal(StartState.Off, start.State);   // setup is not done, so nothing starts
    }

    [Fact]
    public void WindowsRefusingStartAtSignInDoesNotStopSetup()
    {
        var path = dir.At("settings.json");
        var start = new InMemoryStartAtSignIn { FailWith = new UnauthorizedAccessException("denied") };
        Assert.True(new OnboardingModel(Browsers(), start, path).Start());
        Assert.True(SettingsStore.Load(path).Onboarded);
    }

    // MARK: Open-source notices

    [Fact]
    public void TheNoticesAreTheFilesTextOrALineSayingWhereToFindThem()
    {
        Assert.Equal(NoticesText.Missing, NoticesText.Read(dir.Path));
        File.WriteAllText(dir.At(NoticesText.FileName), "\n  Windows App SDK: MIT\n\n");
        Assert.Equal("Windows App SDK: MIT", NoticesText.Read(dir.Path));
        File.WriteAllText(dir.At(NoticesText.FileName), new string('x', 300_000));
        Assert.Equal(200_000, NoticesText.Read(dir.Path).Length);
    }
}
