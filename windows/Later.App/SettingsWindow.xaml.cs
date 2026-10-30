using System.Runtime.InteropServices;
using Later.App.Icons;
using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace Later.App;

/// One browser's row in Settings: its name, how it is doing, and its icon (a globe when the browser is not installed).
public sealed class BrowserRowView(BrowserLine line, ImageSource? icon)
{
    public string Name => line.Name;
    public string Status => line.Status;
    public ImageSource? Icon => icon;
    public Visibility HasIcon => icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoIcon => icon is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsGood => line.Tone == Tone.Good ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsWarning => line.Tone == Tone.Warning ? Visibility.Visible : Visibility.Collapsed;
}

/// Settings, as the Mac's: 520 wide, as tall as its content (the page scrolls when the screen is shorter or the text is large), not
/// resizable. All it says and does comes from `SettingsModel`; this draws it and sends the clicks.
public sealed partial class SettingsWindow : Window
{
    const double WidthDips = 520;
    const int SettlingResizes = 3;   // the first sizes are the window finding its width and its text wrapping; they are centered

    readonly SettingsModel model;
    readonly Action quit;
    readonly Dictionary<Browser, ImageSource> icons = [];
    bool syncing;
    int resizes;

    internal SettingsWindow(SettingsModel model, Action quit)
    {
        this.model = model;
        this.quit = quit;
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = false; presenter.IsMaximizable = false; }

        SortBox.ItemsSource = SettingsModel.SortTitles;
        VersionText.Text = $"Later {model.Version}";
        model.Changed += Update;
        PageStack.SizeChanged += (_, _) => SizeToContent();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            model.Refresh();   // back from Startup apps, say: what Windows says is read again
            _ = model.LoadFirewallAsync();
        };
        Closed += (_, _) => model.Dispose();
        Update();
        _ = LoadIconsAsync();
#if DEBUG
        SampleShot.Attach(this, Root);
#endif
    }

    // MARK: Showing the model

    void Update()
    {
        syncing = true;   // setting a control from the model is not the user changing it
        try
        {
            var summary = model.Summary;
            SummaryText.Text = summary.Text;
            GoodDot.Visibility = summary.Tone == Tone.Good ? Visibility.Visible : Visibility.Collapsed;
            WarningDot.Visibility = summary.Tone == Tone.Warning ? Visibility.Visible : Visibility.Collapsed;
            QuietDot.Visibility = summary.Tone == Tone.Quiet ? Visibility.Visible : Visibility.Collapsed;

            StartSwitch.IsOn = model.StartIsOn;
            var note = model.StartNote;
            StartNotePanel.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
            StartNoteText.Text = note ?? "";
            PauseSwitch.IsOn = model.Paused;
            FeedSwitch.IsOn = model.SavesFeedHomes;
            SortBox.SelectedIndex = (int)model.Sort;

            bool onboarded = model.Onboarded;
            BrowsersLater.Visibility = onboarded ? Visibility.Collapsed : Visibility.Visible;
            BrowserList.Visibility = onboarded ? Visibility.Visible : Visibility.Collapsed;
            BrowserList.ItemsSource = model.Browsers.Select(b => new BrowserRowView(b, icons.GetValueOrDefault(b.Browser))).ToList();

            FirewallSwitch.IsOn = model.FirewallBlocked;
            FirewallSwitch.IsEnabled = model.FirewallAvailable && !model.FirewallBusy;
            var firewallNote = model.FirewallNote;
            FirewallNote.Visibility = firewallNote is null ? Visibility.Collapsed : Visibility.Visible;
            FirewallNote.Text = firewallNote ?? "";
        }
        finally { syncing = false; }
    }

    void OnStartToggled(object sender, RoutedEventArgs e) { if (!syncing) model.SetStart(StartSwitch.IsOn); }

    void OnPauseToggled(object sender, RoutedEventArgs e) { if (!syncing) model.SetPaused(PauseSwitch.IsOn); }

    void OnFeedToggled(object sender, RoutedEventArgs e) { if (!syncing) model.SetSavesFeedHomes(FeedSwitch.IsOn); }

    void OnFirewallToggled(object sender, RoutedEventArgs e) { if (!syncing) _ = model.SetFirewallAsync(FirewallSwitch.IsOn); }

    void OnSortChanged(object sender, SelectionChangedEventArgs e) { if (!syncing && SortBox.SelectedIndex >= 0) model.SetSort((LibrarySort)SortBox.SelectedIndex); }

    /// The Startup apps page of Windows' Settings, for the user to look at (the one address Later hands the system on its own account).
#pragma warning disable CA1822 // named in the XAML, so it cannot be static
    void OnOpenStartupApps(object sender, RoutedEventArgs e)
#pragma warning restore CA1822
    {
        try { _ = Launcher.LaunchUriAsync(new Uri(SettingsModel.StartupAppsUri)); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
    }

    void OnQuit(object sender, RoutedEventArgs e) => quit();

    /// The notices are read when first wanted, not when Settings opens.
    void OnNoticesExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (NoticesBody.Text.Length == 0) NoticesBody.Text = NoticesText.Read();
    }

    // MARK: Browser icons

    /// Each installed browser's own icon, from its executable (the shell's picture of the file); a globe stands in for the rest.
    async Task LoadIconsAsync()
    {
        foreach (var line in model.Browsers)
        {
            if (line.ExePath is not { } exe || await BrowserIcon.Load(exe) is not { } image) continue;
            icons[line.Browser] = image;
        }
        Update();
    }

    // MARK: Size

    /// 520 wide and as tall as the page (and its title row); on a shorter screen the page scrolls. The text wraps at the width it gets, so the
    /// height settles in a pass or two: the first ones also put the window in the middle of the screen it is on.
    void SizeToContent()
    {
        if (Root.XamlRoot is not { } xaml || PageStack.ActualHeight <= 0) return;
        double scale = xaml.RasterizationScale;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = (int)Math.Round(WidthDips * scale);
        int wanted = (int)Math.Ceiling((TitleBar.ActualHeight + PageStack.ActualHeight) * scale);
        int height = Math.Min(wanted, area.Height - (int)Math.Round(64 * scale));   // the frame and the taskbar's edge stay clear
#if DEBUG
        if (Sample.Enabled) height = wanted;   // a picture shows the whole page, whatever the screen it is taken on
#endif
        var now = AppWindow.ClientSize;
        if (now.Width != width || now.Height != height) AppWindow.ResizeClient(new SizeInt32(width, height));
        if (resizes++ >= SettlingResizes) return;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, Math.Max(area.Y, area.Y + (area.Height - size.Height) / 2)));
    }
}
