using Later.App.Icons;
using Later.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Later.App.Controls;

/// A site's tile: its icon, or its first letter, on the site's own soft color (the Mac's `SiteTile`).
public sealed partial class SiteTile : UserControl
{
    /// Apple's ten system colors (light), indexed by `SiteTint`.
    static readonly (byte R, byte G, byte B)[] Palette =
    [
        (0, 122, 255), (88, 86, 214), (175, 82, 222), (255, 45, 85), (255, 59, 48), (255, 149, 0), (52, 199, 89), (48, 176, 199), (50, 173, 230), (162, 132, 94),
    ];

    static readonly AccessibilitySettings Access = new();

    public static readonly DependencyProperty HostProperty =
        DependencyProperty.Register(nameof(Host), typeof(string), typeof(SiteTile), new PropertyMetadata("", (d, _) => ((SiteTile)d).Apply()));

    public static readonly DependencyProperty TileSizeProperty =
        DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(SiteTile), new PropertyMetadata(36.0, (d, _) => ((SiteTile)d).Apply()));

    public SiteTile()
    {
        InitializeComponent();
        Loaded += (_, _) => { SiteIconSources.Changed += Apply; Apply(); };
        Unloaded += (_, _) => SiteIconSources.Changed -= Apply;
        ActualThemeChanged += (_, _) => Apply();
        Apply();
    }

    public string Host
    {
        get => (string)GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    void Apply()
    {
        double size = TileSize;
        Tile.Width = Tile.Height = size;
        Tile.CornerRadius = new CornerRadius(size * 0.27);
        Letter.FontSize = size * 0.44;
        Letter.Text = LibraryListing.SiteLetter(Host);
        var (r, g, b) = Palette[SiteTint.Index(Host)];
        if (Access.HighContrast)
        {
            Tile.ClearValue(Panel.BackgroundProperty);
            Letter.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            bool dark = ActualTheme == ElementTheme.Dark;
            Tile.Background = new SolidColorBrush(Color.FromArgb((byte)(dark ? 66 : 36), r, g, b));
            // A shade deeper in light and lighter in dark, so the letter on its own soft tint stays easy to read.
            byte Shade(byte c) => dark ? (byte)(c + (255 - c) * 0.25) : (byte)(c * 0.65);
            Letter.Foreground = new SolidColorBrush(Color.FromArgb(255, Shade(r), Shade(g), Shade(b)));
        }
        ShowIcon();
    }

    /// The icon at half the tile, as large as it stays crisp; a smaller one than a third of the tile reads worse than the letter.
    void ShowIcon()
    {
        var image = SiteIconSources.For(Host);
        if (image is null) { Icon.Visibility = Visibility.Collapsed; Letter.Visibility = Visibility.Visible; return; }
        if (image.PixelWidth == 0)
        {
            // Still decoding: ask again when it has.
            Icon.Visibility = Visibility.Collapsed; Letter.Visibility = Visibility.Visible;
            void Opened(object s, RoutedEventArgs e) { image.ImageOpened -= Opened; Apply(); }
            image.ImageOpened += Opened;
            return;
        }
        double crisp = image.PixelWidth / (XamlRoot?.RasterizationScale ?? 1);
        double size = TileSize;
        if (crisp < size * 0.35) { Icon.Visibility = Visibility.Collapsed; Letter.Visibility = Visibility.Visible; return; }
        Icon.Source = image;
        Icon.Width = Icon.Height = Math.Min(crisp, size * 0.5);
        Icon.Visibility = Visibility.Visible;
        Letter.Visibility = Visibility.Collapsed;
    }
}
