using Later.App.ViewModels;
using Later.Core;
using Later.Windows.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Later.App.Panes;

/// The library by kind, then Older and Done, then the Regulars, each with its count; and what Later is doing at the foot.
public sealed partial class SidebarPane : UserControl
{
    readonly Dictionary<LibrarySection, (NavigationViewItem Item, InfoBadge Badge)> items = [];
    LibraryViewModel? model;
    bool selecting;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ResumeRequested;

    public SidebarPane()
    {
        InitializeComponent();
        foreach (var section in LibrarySection.Ordered)
            items[section] = ((NavigationViewItem)FindName(section.Title + "Item"), (InfoBadge)FindName(section.Title + "Badge"));
    }

    public void Bind(LibraryViewModel viewModel)
    {
        model = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LibraryViewModel.Counts): UpdateCounts(); break;
                case nameof(LibraryViewModel.Section): UpdateSelection(); break;
                case nameof(LibraryViewModel.Engine): UpdateStatus(); break;
            }
        };
        UpdateCounts();
        UpdateSelection();
        UpdateStatus();
    }

    void UpdateCounts()
    {
        if (model is null) return;
        foreach (var (section, (item, badge)) in items)
        {
            int count = model.Counts.GetValueOrDefault(section);
            badge.Value = count;
            badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(item, $"{section.Title}, {count}");
        }
    }

    void UpdateSelection()
    {
        if (model is null) return;
        selecting = true;
        Nav.SelectedItem = items[model.Section].Item;
        selecting = false;
    }

    void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (selecting || model is null) return;
        if (args.SelectedItemContainer?.Tag is string tag) model.Section = LibrarySection.Ordered.First(s => s.Title == tag);
    }

    /// The Mac's `StatusLine`: problems with the button that fixes them, or Paused with Resume, or what is being watched.
    void UpdateStatus()
    {
        if (model is null) return;
        var state = model.Engine;
        var watching = BrowserInfo.All.Where(b => state.Statuses.GetValueOrDefault(b) == BrowserStatus.Watching).ToList();
        var problems = BrowserInfo.All.Where(b => state.Statuses.TryGetValue(b, out var s) && BrowserStatusText.IsProblem(s))
            .Select(b => BrowserStatusText.Text(state.Statuses[b], b)).ToList();

        ProblemLines.Children.Clear();
        foreach (var text in problems)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = "\uE7BA", Style = (Style)Resources["WarningGlyph"], VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            line.Children.Add(icon);
            var words = new TextBlock { Text = text, TextWrapping = TextWrapping.WrapWholeWords };
            Grid.SetColumn(words, 1);
            line.Children.Add(words);
            ProblemLines.Children.Add(line);
        }
        ProblemsPanel.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PausedPanel.Visibility = state.Onboarded && state.Paused ? Visibility.Visible : Visibility.Collapsed;

        string? status = !state.Onboarded ? "Starts watching after setup"
            : state.Paused || watching.Count == 0 ? null
            : "Watching " + BrowserStatusText.Names(watching);
        StatusText.Text = status ?? "";
        GoodDot.Visibility = state.Onboarded ? Visibility.Visible : Visibility.Collapsed;
        WarningDot.Visibility = state.Onboarded ? Visibility.Collapsed : Visibility.Visible;
        StatusPanel.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    void OnResume(object sender, RoutedEventArgs e) => ResumeRequested?.Invoke(this, EventArgs.Empty);
}
