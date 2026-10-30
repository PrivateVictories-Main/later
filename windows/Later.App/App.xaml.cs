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

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var model = new LibraryViewModel(Sample.Clock);
        AppEngine? engine = null;
        IListEditor? editor = null;
#if DEBUG
        if (Sample.Enabled) editor = PrepareSample(model);
        else
#endif
        {
            // Before setup (Task 9 adds it) the list is only shown; afterwards the engine is asked for and the window follows it.
            var paths = AppPaths.Default;
            var settings = SettingsStore.Load(paths.Settings);
            model.Grouping = settings.Grouping;
            model.Sort = settings.Sort;
            model.SortReversed = settings.SortReversed;
            var queue = DispatcherQueue.GetForCurrentThread();
            // The sites' icons come from the browsers' own icon caches, kept as PNGs in Later's folder.
            var icons = new SiteIconStore(Path.Combine(paths.DataDir, "icons"), () => BrowserInfo.All.SelectMany(b => FaviconStore.Databases(BrowserDiscovery.DataFolder(b))).ToList(), IconDecoder.Accept);
            SiteIconSources.Use(icons, queue);
            engine = new AppEngine(paths, model, work => queue.TryEnqueue(() => work()), Exit, icons: icons);
            editor = engine;
        }
        window = new ShellWindow(model, engine, editor);
        window.Activate();
        engine?.Start();
    }

#if DEBUG
    /// A made-up list and statuses; no engine, no hooks, nothing read from or written to the real data.
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
        var watching = BrowserInfo.All.ToDictionary(b => b, _ => Later.Windows.Engine.BrowserStatus.Watching);
        model.SetEngineState(new EngineState(watching, false, null, true, true));
        return editor;
    }
#endif
}
