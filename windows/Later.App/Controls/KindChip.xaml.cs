using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Later.App.Controls;

/// A kind's symbol and name in the kind's color.
public sealed partial class KindChip : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(Kind), typeof(KindChip), new PropertyMetadata(Kind.Other, (d, _) => ((KindChip)d).Apply()));

    public KindChip()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply();
        Apply();
    }

    public Kind Kind
    {
        get => (Kind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    void Apply()
    {
        Symbol.Glyph = Kind.Glyph();
        KindText.Text = Kind.Title();
        VisualStateManager.GoToState(this, Kind.Title(), false);
    }
}
