using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Later.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        Debug.Assert(Later.Core.LinkCleaner.Clean("https://example.com/") is not null);   // keeps the Core reference real, so it is scanned
    }
}
