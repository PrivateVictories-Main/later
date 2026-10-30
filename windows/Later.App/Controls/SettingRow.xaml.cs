using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Later.App.Controls;

/// A setting's card: a tile with its symbol, its name and what it does, and its control (the content). The control is named for the
/// setting and told its detail, so assistive technology reads the control once, with both.
[ContentProperty(Name = nameof(Accessory))]
public sealed partial class SettingRow : UserControl
{
    /// From this text size on, the control goes under the text.
    const double StackAt = 1.5;

    static readonly UISettings Ui = new();

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow), new PropertyMetadata("", (d, _) => ((SettingRow)d).Apply()));

    public static readonly DependencyProperty TileBrushProperty =
        DependencyProperty.Register(nameof(TileBrush), typeof(Brush), typeof(SettingRow), new PropertyMetadata(null, (d, _) => ((SettingRow)d).Apply()));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata("", (d, _) => ((SettingRow)d).Apply()));

    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(nameof(Detail), typeof(string), typeof(SettingRow), new PropertyMetadata("", (d, _) => ((SettingRow)d).Apply()));

    public static readonly DependencyProperty AccessoryProperty =
        DependencyProperty.Register(nameof(Accessory), typeof(UIElement), typeof(SettingRow), new PropertyMetadata(null, (d, _) => ((SettingRow)d).Apply()));

    public SettingRow()
    {
        InitializeComponent();
        Loaded += (_, _) => Arrange();
        Apply();
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush? TileBrush { get => (Brush?)GetValue(TileBrushProperty); set => SetValue(TileBrushProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public UIElement? Accessory { get => (UIElement?)GetValue(AccessoryProperty); set => SetValue(AccessoryProperty, value); }

    void Apply()
    {
        Symbol.Glyph = Glyph;
        if (TileBrush is { } tile) TileBorder.Background = tile;
        TitleText.Text = Title;
        DetailText.Text = Detail;
        DetailText.Visibility = Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccessoryHost.Content = Accessory;
        if (Accessory is { } accessory)
        {
            AutomationProperties.SetName(accessory, Title);
            AutomationProperties.SetHelpText(accessory, Detail);
        }
    }

    void Arrange()
    {
        bool below = Ui.TextScaleFactor >= StackAt;
        Grid.SetRow(AccessoryHost, below ? 1 : 0);
        Grid.SetColumn(AccessoryHost, below ? 1 : 2);
        AccessoryHost.HorizontalAlignment = below ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        AccessoryHost.Margin = below ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }
}
