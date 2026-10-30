using System.Globalization;
using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Later.App.Panes;

/// The right pane. With nothing selected it is the overview of the list: how many pages across how many sites, and the count of
/// each kind. Task 8 fills in the details of one page and the summary of several.
public sealed partial class InspectorPane : UserControl
{
    readonly Dictionary<Kind, (Grid Row, TextBlock Count)> kinds = [];
    LibraryViewModel? model;

    public InspectorPane()
    {
        InitializeComponent();
        foreach (var kind in KindInfo.All) kinds[kind] = ((Grid)FindName(kind.Title() + "Row"), (TextBlock)FindName(kind.Title() + "Count"));
    }

    public void Bind(LibraryViewModel viewModel)
    {
        model = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.Current) or nameof(LibraryViewModel.Counts) or nameof(LibraryViewModel.Search)) Update();
        };
        Update();
    }

    void Update()
    {
        if (model is null) return;
        var listing = model.Current;
        int pages = listing.Items.Count;
        Caption.Text = model.Searching
            ? (pages == 1 ? "1 result" : pages.ToString(CultureInfo.InvariantCulture) + " results") + $" for “{model.Query}”"
            : $"{pages.ToString(CultureInfo.InvariantCulture)} {(pages == 1 ? "page" : "pages")} across {listing.Sites.ToString(CultureInfo.InvariantCulture)} {(listing.Sites == 1 ? "site" : "sites")}";
        // The kinds of what the list shows now, so a search or a section narrows them too.
        var byKind = listing.Items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (kind, (row, count)) in kinds)
        {
            int n = byKind.GetValueOrDefault(kind);
            count.Text = n.ToString(CultureInfo.InvariantCulture);
            AutomationProperties.SetName(row, $"{kind.Title()}, {n}");
        }
    }
}
