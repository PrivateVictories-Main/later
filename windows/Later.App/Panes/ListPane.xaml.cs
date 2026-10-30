using System.Globalization;
using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Later.App.Panes;

/// A flat row. Task 8 replaces it with the real row (site tile, kind chip, visits, open dot) and the grouped list.
public sealed record RowText(string Title, string Site, string When, string Name)
{
    /// What assistive technology reads for the item.
    public override string ToString() => Name;
}

/// The list of the section or search, with its heading, an empty state that says where the user is, and the notice bar.
public sealed partial class ListPane : UserControl
{
    LibraryViewModel? model;
    string? dismissed;

    public ListPane() => InitializeComponent();

    /// Where the keyboard starts, so the search box does not open with a caret in it.
    public void FocusList() => Rows.Focus(FocusState.Programmatic);

    public void Bind(LibraryViewModel viewModel)
    {
        model = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LibraryViewModel.Current): case nameof(LibraryViewModel.Section): case nameof(LibraryViewModel.Search): ShowList(); break;
                case nameof(LibraryViewModel.Counts): ShowEmptyState(); break;
                case nameof(LibraryViewModel.Engine): ShowNotice(); break;
            }
        };
        ShowList();
        ShowNotice();
    }

    void ShowList()
    {
        if (model is null) return;
        var listing = model.Current;
        Heading.Text = model.Searching ? "Search" : model.Section.Title;
        Summary.Text = SummaryText(listing);
        Rows.ItemsSource = listing.Items.Select(i => Row(i, model.Now)).ToList();
        ShowEmptyState();
    }

    static RowText Row(Item item, DateTime now)
    {
        var title = item.Title.Length == 0 ? item.Url : item.Title;
        var saved = LibraryQuery.ShortDate(item.FirstSaved, now);
        var name = $"{title}, {item.Host}, {item.Kind.Title()}, saved {saved}" + (item.Status == ItemStatus.Done ? ", done" : "");
        return new RowText(title, item.Host, LibraryQuery.ShortDate(LibraryQuery.SortDate(item), now), name);
    }

    string SummaryText(Listing listing)
    {
        int pages = listing.Items.Count;
        if (model!.Searching)
            return (pages == 1 ? "1 result" : pages.ToString(CultureInfo.InvariantCulture) + " results") + $" for “{model.Query}”";
        if (pages == 0) return "Nothing here yet";
        var parts = new List<string> { pages == 1 ? "1 page" : pages.ToString(CultureInfo.InvariantCulture) + " pages" };
        if (listing.NewToday > 0 && model.Section != LibrarySection.Done) parts.Add(listing.NewToday.ToString(CultureInfo.InvariantCulture) + " new today");
        parts.Add(listing.Sites == 1 ? "1 site" : listing.Sites.ToString(CultureInfo.InvariantCulture) + " sites");
        return string.Join(" · ", parts);
    }

    /// What an empty list says, by where the user is.
    void ShowEmptyState()
    {
        if (model is null) return;
        bool empty = model.Current.Items.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Rows.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;
        var section = model.Section;
        bool older = model.Counts.GetValueOrDefault(LibrarySection.Older) > 0;
        if (model.Searching)
        {
            EmptyGlyph.Glyph = "";
            EmptyTitle.Text = $"No Results for “{model.Query}”";
            EmptyMessage.Text = "Check the spelling or try a new search.";
        }
        else if (section == LibrarySection.All && model.Counts.Count == 0)   // nothing saved at all
        {
            EmptyGlyph.Glyph = section.Glyph();
            EmptyTitle.Text = "Close tabs freely.";
            EmptyMessage.Text = "Later keeps the ones worth coming back to. When a tab is on one specific thing and you've had it open a while, it shows up here, sorted by kind.";
        }
        else
        {
            EmptyGlyph.Glyph = section.Glyph();
            EmptyTitle.Text = section.EmptyTitle();
            EmptyMessage.Text = section.EmptyMessage(older);
        }
        ShowOlder.Visibility = !model.Searching && section == LibrarySection.All && older ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnShowOlder(object sender, RoutedEventArgs e) { if (model is not null) model.Section = LibrarySection.Older; }

    void ShowNotice()
    {
        var text = model?.Engine.Notice;
        if (text is null) dismissed = null;
        Notice.Message = text ?? "";
        Notice.IsOpen = text is not null && text != dismissed;
    }

    void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => dismissed = model?.Engine.Notice;
}
