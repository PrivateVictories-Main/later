using Later.App.Icons;
using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Later.App;

/// The first-run welcome, 600 wide and as tall as its content (it scrolls on a shorter screen, with the button always in view). It says
/// what Later does and does not do, and has one button; nothing is asked of the user and nothing is changed until it is pressed.
public sealed partial class OnboardingWindow : Window
{
    const double WidthDips = 600;
    const int SettlingResizes = 3;   // the first sizes are the window finding its width and its text wrapping; they are centered

    readonly OnboardingModel model;
    readonly Action finished;
    readonly Dictionary<Browser, ImageSource> icons = [];
    int resizes;

    /// `finished` runs when setup is done: the app shows the library, then this window closes.
    internal OnboardingWindow(OnboardingModel model, Action finished)
    {
        this.model = model;
        this.finished = finished;
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = false; presenter.IsMaximizable = false; }

        ShowBrowsers();
        Root.Loaded += (_, _) => StartButton.Focus(FocusState.Programmatic);   // Enter presses it, and a screen reader starts on it
        PageStack.SizeChanged += (_, _) => SizeToContent();
        _ = LoadIconsAsync();
#if DEBUG
        SampleShot.Attach(this, Root);
#endif
    }

    void ShowBrowsers()
    {
        BrowserList.Visibility = model.Browsers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoBrowser.Visibility = model.Browsers.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        BrowserList.ItemsSource = model.Browsers
            .Select(b => new BrowserRowView(new BrowserLine(b.Browser, b.Name, b.Status, b.IsOpen ? Tone.Good : Tone.Quiet, b.ExePath), icons.GetValueOrDefault(b.Browser)))
            .ToList();
    }

    async Task LoadIconsAsync()
    {
        foreach (var browser in model.Browsers)
        {
            if (browser.ExePath is not { } exe || await BrowserIcon.Load(exe) is not { } image) continue;
            icons[browser.Browser] = image;
        }
        ShowBrowsers();
    }

    void OnStart(object sender, RoutedEventArgs e)
    {
        if (model.Start())
        {
            FailureText.Visibility = Visibility.Collapsed;
            finished();
            return;
        }
        FailureText.Text = model.Failure ?? "";
        FailureText.Visibility = Visibility.Visible;
    }

    /// 600 wide and as tall as the page, its title row and the button's footer; on a shorter screen the page scrolls and the footer stays.
    void SizeToContent()
    {
        if (Root.XamlRoot is not { } xaml || PageStack.ActualHeight <= 0) return;
        double scale = xaml.RasterizationScale;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = (int)Math.Round(WidthDips * scale);
        int wanted = (int)Math.Ceiling((TitleBar.ActualHeight + PageStack.ActualHeight + FooterHeight) * scale);
        int height = Math.Min(wanted, area.Height - (int)Math.Round(64 * scale));   // the frame and the taskbar's edge stay clear
        var now = AppWindow.ClientSize;
        if (now.Width != width || now.Height != height) AppWindow.ResizeClient(new SizeInt32(width, height));
        if (resizes++ >= SettlingResizes) return;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    double FooterHeight => Root.RowDefinitions[2].ActualHeight;
}
