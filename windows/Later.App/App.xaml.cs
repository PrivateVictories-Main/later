using Later.App.Icons;
using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Platform;
using Later.Windows.Reader;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Later.App;

public partial class App : Application
{
    ShellWindow? window;
    OnboardingWindow? welcome;

    // Until the sign-in and firewall work lands these keep their state in memory only: the settings show and change them, Windows is not asked.
    readonly InMemoryStartAtSignIn start = new();
    readonly InMemoryFirewallBlock firewall = new();

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        if (Sample.Enabled) { LaunchSample(); return; }
#endif
        var paths = AppPaths.Default;
        if (SettingsStore.Load(paths.Settings).Onboarded) { OpenLibrary(paths); return; }
        // The first run: only the welcome. No engine, no list and no background process until the user presses its button.
        var browsers = OnboardingModel.Discover(BrowserDiscovery.ExePath, BrowserDiscovery.IsBrowserRunning);
        var model = new OnboardingModel(browsers, start, paths.Settings);
        welcome = new OnboardingWindow(model, () => { OpenLibrary(paths); welcome?.Close(); });
        welcome.Activate();
    }

    /// The library window and, behind it, the engine: asked for, and watched through, from here on.
    void OpenLibrary(AppPaths paths)
    {
        var model = new LibraryViewModel(Sample.Clock);
        var settings = SettingsStore.Load(paths.Settings);
        model.Grouping = settings.Grouping;
        model.Sort = settings.Sort;
        model.SortReversed = settings.SortReversed;
        var queue = DispatcherQueue.GetForCurrentThread();
        // The sites' icons come from the browsers' own icon caches, kept as PNGs in Later's folder.
        var icons = new SiteIconStore(Path.Combine(paths.DataDir, "icons"), () => BrowserInfo.All.SelectMany(b => FaviconStore.Databases(BrowserDiscovery.DataFolder(b))).ToList(), IconDecoder.Accept);
        SiteIconSources.Use(icons, queue);
        var engine = new AppEngine(paths, model, work => queue.TryEnqueue(() => work()), Exit, icons: icons);
        window = new ShellWindow(model, engine, engine, start, firewall);
        window.Activate();
        engine.Start();
    }

#if DEBUG
    /// A sample run: made-up lists and statuses; no engine, no hooks, nothing read from or written to the real data, no setting written.
    void LaunchSample()
    {
        var model = new LibraryViewModel(Sample.Clock);
        var editor = PrepareSample(model);
        var sampleStart = new InMemoryStartAtSignIn(Sample.Startup switch { "off" => StartState.Off, "disabled" => StartState.DisabledByUser, _ => StartState.On });
        var sampleFirewall = new InMemoryFirewallBlock();
        string? ExePathOf(Browser b) => Sample.NoBrowsers ? null : BrowserDiscovery.ExePath(b);
        if (Sample.Onboarding)
        {
            // Helium open, Chrome not: both listed, whatever this machine has (a browser it lacks gets a globe).
            OnboardingBrowser[] browsers = Sample.NoBrowsers ? [] : [new OnboardingBrowser(Browser.Helium, true, ExePathOf(Browser.Helium)), new OnboardingBrowser(Browser.Chrome, false, ExePathOf(Browser.Chrome))];
            welcome = new OnboardingWindow(new OnboardingModel(browsers, sampleStart, null), Exit);
            welcome.Activate();
        }
        else if (Sample.Settings)
        {
            var settings = new SettingsModel(model, null, sampleStart, sampleFirewall, ExePathOf, null, "1.4.0");
            if (Sample.Startup == "failed")
            {
                sampleStart.FailWith = new UnauthorizedAccessException("Access to the registry key is denied.");
                settings.SetStart(false);
            }
            new SettingsWindow(settings, Exit).Activate();
        }
        else
        {
            window = new ShellWindow(model, null, editor, sampleStart, sampleFirewall);
            window.Activate();
        }
    }

    static SampleEditor PrepareSample(LibraryViewModel model)
    {
        var library = SampleLibrary.Build(Sample.Clock);
        var editor = new SampleEditor(model, library);
        if (Sample.Section is { } section) model.Section = section;
        if (Sample.Group is { } grouping) model.Grouping = grouping;
        if (Sample.Sort is { } sort) model.Sort = sort;
        model.SortReversed = Sample.Reversed;
        if (Sample.Search is { } search) model.SetSearch(search);
        editor.Send(0);
        model.Wake(Sample.Clock);   // worked out now, so the first rows can be selected below
        if (Sample.SelectCount > 0) model.SetSelection(model.Current.RowIds.Take(Sample.SelectCount));
        var statuses = BrowserInfo.All.ToDictionary(b => b, _ => Sample.NoBrowsers ? Later.Windows.Engine.BrowserStatus.NotInstalled : Later.Windows.Engine.BrowserStatus.Watching);
        switch (Sample.Status)
        {
            case "problem": statuses[Browser.Chrome] = Later.Windows.Engine.BrowserStatus.ReadFailed; break;
            case "idle": foreach (var b in BrowserInfo.All) statuses[b] = Later.Windows.Engine.BrowserStatus.NotRunning; break;
            case "missing": statuses[Browser.Helium] = Later.Windows.Engine.BrowserStatus.NotInstalled; break;
        }
        model.SetEngineState(new EngineState(statuses, Sample.Status == "paused", null, true, true));
        return editor;
    }
#endif
}
