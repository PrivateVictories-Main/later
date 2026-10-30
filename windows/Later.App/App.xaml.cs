using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Later.App;

public partial class App : Application
{
    ShellWindow? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var model = new LibraryViewModel();
        AppEngine? engine = null;
#if DEBUG
        if (Sample.Enabled) PrepareSample(model);
        else
#endif
        {
            // Before setup (Task 9 adds it) the list is only shown; afterwards the engine is asked for and the window follows it.
            var paths = AppPaths.Default;
            var settings = SettingsStore.Load(paths.Settings);
            model.Grouping = settings.Grouping;
            model.Sort = settings.Sort;
            model.SortReversed = settings.SortReversed;
            engine = new AppEngine(paths, model, DispatcherQueue.GetForCurrentThread(), Exit);
        }
        window = new ShellWindow(model, engine);
        window.Activate();
        engine?.Start();
    }

#if DEBUG
    /// A made-up list and statuses; no engine, no hooks, nothing read from or written to the real data.
    static void PrepareSample(LibraryViewModel model)
    {
        var data = SampleLibrary.Data(DateTime.UtcNow);
        var copies = SampleLibrary.OpenCopies(new Library(data));
        if (Sample.Section is { } section) model.Section = section;
        if (Sample.Group is { } grouping) model.Grouping = grouping;
        if (Sample.Sort is { } sort) model.Sort = sort;
        model.SortReversed = Sample.Reversed;
        if (Sample.Search is { } search) model.SetSearch(search);
        model.SetSnapshot(data, copies);
        var watching = BrowserInfo.All.ToDictionary(b => b, _ => Later.Windows.Engine.BrowserStatus.Watching);
        model.SetEngineState(new EngineState(watching, false, null, true, true));
    }
#endif
}
