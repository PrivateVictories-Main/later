using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace Later.App;

/// The library window: Mica, the title bar as the toolbar (search, Group By, Sort By), and the three panes. Closing it ends the app
/// (the agent, if any, takes the engine back); the engine itself lives in `AppEngine`.
public sealed partial class ShellWindow : Window
{
    const double MinWidthDips = 900, MinHeightDips = 600, StartWidthDips = 1120, StartHeightDips = 720;

    readonly LibraryViewModel model;
    readonly AppEngine? engine;
    readonly Dictionary<LibraryGrouping, RadioMenuFlyoutItem> groupItems = [];
    readonly Dictionary<LibrarySort, RadioMenuFlyoutItem> sortItems = [];
    readonly RadioMenuFlyoutItem newestItem = new() { GroupName = "Direction" }, oldestItem = new() { GroupName = "Direction" };

    internal ShellWindow(LibraryViewModel model, AppEngine? engine)
    {
        this.model = model;
        this.engine = engine;
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var bar = AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        Sidebar.Bind(model);
        List.Bind(model);
        Inspector.Bind(model);
        Sidebar.ResumeRequested += (_, _) => engine?.Resume();
        Sidebar.OpenSettingsRequested += (_, _) => { };   // Task 9 opens Settings
        SearchBox.Text = model.Search;
        SearchBox.TextChanged += (box, _) => model.SetSearch(box.Text);
        BuildMenus();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.Grouping) or nameof(LibraryViewModel.Sort) or nameof(LibraryViewModel.SortReversed))
            {
                SyncMenus();
                engine?.SetView(model.Grouping, model.Sort, model.SortReversed);
            }
        };
        AddAccelerators();

        Root.Loaded += (_, _) =>
        {
            StartSize(); KeepMinimumSize(); UpdateTitleBarRegions();
            List.FocusList();
            Root.XamlRoot.Changed += (_, _) => { KeepMinimumSize(); UpdateTitleBarRegions(); };   // another display's scale
        };
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        VisibilityChanged += (_, e) =>
        {
            if (e.Visible) { model.Wake(DateTime.UtcNow); engine?.SetVisible(true); engine?.RetryLock(); }
            else { model.Hide(); engine?.SetVisible(false); }
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            model.Wake(DateTime.UtcNow);   // a new day, or pages gone to Older, redraw; otherwise nothing does
            engine?.RetryLock();
        };
        Closed += (_, _) =>
        {
            engine?.Shutdown();
            Application.Current.Exit();
        };
#if DEBUG
        ApplySample();
#endif
    }

    // MARK: Menus

    void BuildMenus()
    {
        var group = new MenuFlyout();
        foreach (var grouping in Enum.GetValues<LibraryGrouping>())
        {
            var item = new RadioMenuFlyoutItem { Text = grouping.Title(), GroupName = "Grouping" };
            item.Click += (_, _) => model.Grouping = grouping;
            groupItems[grouping] = item;
            group.Items.Add(item);
        }
        GroupButton.Flyout = group;

        var sort = new MenuFlyout();
        void Add(LibrarySort order)
        {
            var item = new RadioMenuFlyoutItem { Text = order.Title(), GroupName = "Sort" };
            item.Click += (_, _) => model.Sort = order;
            sortItems[order] = item;
            sort.Items.Add(item);
        }
        Add(LibrarySort.TabOrder); Add(LibrarySort.TabOrderReversed);
        sort.Items.Add(new MenuFlyoutSeparator());
        Add(LibrarySort.DateOpened); Add(LibrarySort.DateSaved); Add(LibrarySort.LastSeen);
        sort.Items.Add(new MenuFlyoutSeparator());
        Add(LibrarySort.Title); Add(LibrarySort.Site);
        sort.Items.Add(new MenuFlyoutSeparator());
        newestItem.Click += (_, _) => model.SortReversed = false;
        oldestItem.Click += (_, _) => model.SortReversed = true;
        sort.Items.Add(newestItem);
        sort.Items.Add(oldestItem);
        SortButton.Flyout = sort;
        SyncMenus();
    }

    /// Checks follow the model, and the two directions are named for the current order (the tab orders have none).
    void SyncMenus()
    {
        foreach (var (grouping, item) in groupItems) item.IsChecked = grouping == model.Grouping;
        foreach (var (order, item) in sortItems) item.IsChecked = order == model.Sort;
        var names = model.Sort.DirectionTitles();
        newestItem.Text = names?.Forward ?? "Newest First";
        oldestItem.Text = names?.Reversed ?? "Oldest First";
        newestItem.IsEnabled = oldestItem.IsEnabled = names is not null;
        newestItem.IsChecked = !model.SortReversed;
        oldestItem.IsChecked = model.SortReversed;
    }

    /// Ctrl+F finds, Ctrl+1 to Ctrl+9 go to the sidebar's first nine sections.
    void AddAccelerators()
    {
        void Add(VirtualKey key, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, e) => { action(); e.Handled = true; };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        Add(VirtualKey.F, () => SearchBox.Focus(FocusState.Keyboard));
        for (int i = 0; i < 9; i++)
        {
            var section = LibrarySection.Ordered[i];
            Add(VirtualKey.Number1 + i, () => model.Section = section);
        }
    }

    // MARK: Size and the title bar

    double Scale => Root.XamlRoot?.RasterizationScale ?? 1;

    void StartSize()
    {
#if DEBUG
        if (Sample.Enabled) return;   // a sample run is sized by its own flags
#endif
        AppWindow.Resize(new SizeInt32((int)Math.Round(StartWidthDips * Scale), (int)Math.Round(StartHeightDips * Scale)));
    }

    void KeepMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        presenter.PreferredMinimumWidth = (int)Math.Round(MinWidthDips * Scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(MinHeightDips * Scale);
    }

    /// The search box and the two buttons sit in the title bar's drag area; these regions give them their clicks back.
    void UpdateTitleBarRegions()
    {
        if (Root.XamlRoot is null) return;
        RectInt32 Region(FrameworkElement element)
        {
            var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new RectInt32((int)Math.Round(bounds.X * Scale), (int)Math.Round(bounds.Y * Scale), (int)Math.Round(bounds.Width * Scale), (int)Math.Round(bounds.Height * Scale));
        }
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [Region(SearchBox), Region(GroupButton), Region(SortButton)]);
    }

#if DEBUG
    // MARK: Sample mode and screenshots (Debug only; a Release build carries none of it)

    bool activatedOnce, loadedOnce, shotStarted;

    void ApplySample()
    {
        if (!Sample.Enabled) return;
        if (Sample.Dark)
        {
            Root.RequestedTheme = ElementTheme.Dark;
            AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Gray;
        }
        var (width, height) = Sample.ShotSize;
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(10, 10));
        if (Sample.ShotPath is null && Sample.ShotScreenPath is null) return;
        Activated += (_, _) => { activatedOnce = true; StartShot(); };
        Root.Loaded += (_, _) => { loadedOnce = true; StartShot(); };
    }

    /// Once the window is active and its content loaded: wait for the first frame to settle, take the picture, and quit.
    void StartShot()
    {
        if (!activatedOnce || !loadedOnce || shotStarted) return;
        shotStarted = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(Sample.ShotDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            try { ScreenCapture.Save(WinRT.Interop.WindowNative.GetWindowHandle(this), Sample.ShotPath, Sample.ShotScreenPath); }
            finally { Application.Current.Exit(); }
        };
        timer.Start();
    }
#endif
}
