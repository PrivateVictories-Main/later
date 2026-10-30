using Later.App.Actions;
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
    readonly ItemActions actions;
    readonly Dictionary<LibraryGrouping, RadioMenuFlyoutItem> groupItems = [];
    readonly Dictionary<LibrarySort, RadioMenuFlyoutItem> sortItems = [];
    readonly RadioMenuFlyoutItem newestItem = new() { GroupName = "Direction" }, oldestItem = new() { GroupName = "Direction" };

    internal ShellWindow(LibraryViewModel model, AppEngine? engine, IListEditor? editor)
    {
        this.model = model;
        this.engine = engine;
        InitializeComponent();
        actions = new ItemActions(model, editor, () => Root.XamlRoot);

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var bar = AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        Sidebar.Bind(model);
        List.Bind(model, actions);
        Inspector.Bind(model, actions);
        List.ClearSearchRequested += (_, _) => SearchBox.Text = "";
        List.TypeToSearch += (_, text) => StartSearch(model.Search + text);
        Inspector.SearchFor += (_, host) => SearchBox.Text = host;
        Sidebar.ResumeRequested += (_, _) => engine?.Resume();
        Sidebar.OpenSettingsRequested += (_, _) => { };   // Task 9 opens Settings
        SearchBox.Text = model.Search;
        SearchBox.TextChanged += (box, _) => model.SetSearch(box.Text);
        BuildMenus();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsLoaded)) ShowLoaded();
            if (e.PropertyName is nameof(LibraryViewModel.Grouping) or nameof(LibraryViewModel.Sort) or nameof(LibraryViewModel.SortReversed))
            {
                SyncMenus();
                engine?.SetView(model.Grouping, model.Sort, model.SortReversed);
            }
        };
        AddAccelerators();
        ShowLoaded();
        if (!model.IsLoaded)
        {
            // A ring only if the list takes a moment, so a quick start shows no flash of it.
            var wait = DispatcherQueue.CreateTimer();
            wait.Interval = TimeSpan.FromMilliseconds(600);
            wait.IsRepeating = false;
            wait.Tick += (_, _) => { if (!model.IsLoaded) Loading.Visibility = Visibility.Visible; };
            wait.Start();
        }

        Root.Loaded += (_, _) =>
        {
            StartSize(); KeepMinimumSize(); UpdateTitleBarRegions();
            List.FocusList();
            Root.XamlRoot.Changed += (_, _) => { KeepMinimumSize(); UpdateTitleBarRegions(); };   // another display's scale
        };
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        VisibilityChanged += (_, e) => UpdateShown(e.Visible);
        AppWindow.Changed += (_, _) => UpdateShown(AppWindow.IsVisible);   // a minimized window is still "visible" to the window, so its presenter says
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            UpdateShown(AppWindow.IsVisible);
            if (shown) model.Wake(Sample.Clock);   // a new day, or pages gone to Older, redraw; otherwise nothing does
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

    // MARK: Loading and showing

    bool shown;   // what the model and the engine were last told

    /// Until the first list arrives the panes are not shown: a library being loaded is not an empty one.
    void ShowLoaded()
    {
        bool loaded = model.IsLoaded;
        Panes.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        if (!loaded) return;
        Loading.Visibility = Visibility.Collapsed;
        // Only if the keyboard is not already somewhere in the toolbar (the user may have tabbed there during the load).
        if (new Control[] { SearchBox, GroupButton, SortButton }.All(c => c.FocusState == FocusState.Unfocused)) List.FocusList();
    }

    /// Shown means visible and not minimized (`VisibilityChanged` is not raised for a minimize). Only a change is acted on.
    void UpdateShown(bool visible)
    {
        bool now = visible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (now == shown) return;
        shown = now;
        if (now) { model.Wake(Sample.Clock); engine?.SetVisible(true); engine?.RetryLock(); }
        else { model.Hide(); engine?.SetVisible(false); }
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

    /// Ctrl+F finds, Ctrl+1 to Ctrl+9 go to the sidebar's first nine sections, Ctrl+A selects every page, Ctrl+Z and Ctrl+Y undo and redo.
    /// On the window so they work wherever the keyboard is; in a text field the field keeps its own Ctrl+Z, Ctrl+Y and Ctrl+A.
    void AddAccelerators()
    {
        void Add(VirtualKey key, Func<bool> action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, e) => e.Handled = action();
            Root.KeyboardAccelerators.Add(accelerator);
        }
        bool InText() => Root.XamlRoot is { } xaml && FocusManager.GetFocusedElement(xaml) is TextBox;
        Add(VirtualKey.F, () => { SearchBox.Focus(FocusState.Keyboard); return true; });
        Add(VirtualKey.Z, () => { if (InText()) return false; actions.Undo(); return true; });
        Add(VirtualKey.Y, () => { if (InText()) return false; actions.Redo(); return true; });
        Add(VirtualKey.A, () => { if (InText()) return false; model.SelectAll(); return true; });
        for (int i = 0; i < 9; i++)
        {
            var section = LibrarySection.Ordered[i];
            Add(VirtualKey.Number1 + i, () => { model.Section = section; return true; });
        }
    }

    /// A letter typed in the list: the search field takes it and the keyboard, with the caret after it.
    void StartSearch(string text)
    {
        SearchBox.Text = text;
        SearchBox.Focus(FocusState.Keyboard);
        // Focusing a text field selects what it holds; the next letter must add to the search, not replace it.
        if (FindTextBox(SearchBox) is { } box) { box.SelectionStart = box.Text.Length; box.SelectionLength = 0; }
    }

    static TextBox? FindTextBox(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox box) return box;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
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
