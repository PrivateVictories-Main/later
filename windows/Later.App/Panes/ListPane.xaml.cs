using System.Collections.ObjectModel;
using System.Globalization;
using Later.App.Actions;
using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Later.App.Panes;

/// Headings and rows have templates of their own.
public sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate Row { get; set; } = default!;
    public DataTemplate Header { get; set; } = default!;

    protected override DataTemplate SelectTemplateCore(object item) => item is GroupHeaderRow ? Header : Row;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public sealed class ContainerStyleSelector : StyleSelector
{
    public Style Row { get; set; } = default!;
    public Style Header { get; set; } = default!;

    protected override Style SelectStyleCore(object item, DependencyObject container) => item is GroupHeaderRow ? Header : Row;
}

/// The list of the section or search: its heading, its pages under their headings (one list control over a flat sequence of headings
/// and rows), an empty state that says where the user is, the notice bar, and the Undo bar. The model owns the selection; the list
/// control shows it and reports the user's changes to it.
public sealed partial class ListPane : UserControl
{
    readonly ObservableCollection<object> entries = [];
    LibraryViewModel? model;
    ItemActions? actions;
    string? dismissed;
    bool syncing, fromList;
    int lastIndex;

    public ListPane() => InitializeComponent();

    /// Esc in the list with a search going: the window clears the search field.
    public event EventHandler? ClearSearchRequested;

    /// A letter typed in the list starts a search with it: the window puts it in the search field.
    public event EventHandler<string>? TypeToSearch;

    /// Where the keyboard starts, so the search box does not open with a caret in it.
    public void FocusList() => Rows.Focus(FocusState.Programmatic);

    public void Bind(LibraryViewModel viewModel, ItemActions itemActions)
    {
        model = viewModel;
        actions = itemActions;
        Rows.ItemsSource = entries;
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LibraryViewModel.Current): ShowList(); break;   // every section or search change comes with a new listing
                case nameof(LibraryViewModel.Counts): ShowEmptyState(); break;
                case nameof(LibraryViewModel.Engine): ShowNotice(); UpdateEmptyDone(); break;
                case nameof(LibraryViewModel.Selected): ApplySelection(scroll: !fromList); break;
                case nameof(LibraryViewModel.UndoName): ShowUndo(); break;
            }
        };
        ShowList();
        ShowNotice();
    }

    // MARK: The list

    void ShowList()
    {
        if (model is null) return;
        var listing = model.Current;
        Heading.Text = model.Searching ? "Search" : model.Section.Title;
        Summary.Text = SummaryText(listing);
        UpdateEmptyDone();
        Sync(ListRows.Build(listing, model.Section, model.Searching, model.EffectiveGrouping, model.GroupingActive, model.Sort, model.Now, model.IsFolded));
        ShowEmptyState();
        ApplySelection(scroll: false);
    }

    /// Brings the list control's items to `next` with the fewest changes, so a change somewhere in the list keeps the scroll position
    /// and the rows around it as they are.
    void Sync(List<object> next)
    {
        syncing = true;
        try
        {
            var nextKeys = next.Select(Key).ToHashSet(StringComparer.Ordinal);
            for (int i = entries.Count - 1; i >= 0; i--) if (!nextKeys.Contains(Key(entries[i]))) entries.RemoveAt(i);
            for (int i = 0; i < next.Count; i++)
            {
                var key = Key(next[i]);
                if (i < entries.Count && Key(entries[i]) == key)
                {
                    if (!entries[i].Equals(next[i])) entries[i] = next[i];
                    continue;
                }
                int at = -1;
                for (int j = i + 1; j < entries.Count; j++) if (Key(entries[j]) == key) { at = j; break; }
                if (at >= 0)
                {
                    entries.Move(at, i);
                    if (!entries[i].Equals(next[i])) entries[i] = next[i];
                }
                else entries.Insert(i, next[i]);
            }
            while (entries.Count > next.Count) entries.RemoveAt(entries.Count - 1);
        }
        finally { syncing = false; }
    }

    static string Key(object entry) => entry switch { RowModel r => r.Id.ToString(), GroupHeaderRow h => "header|" + h.Title, _ => "" };

    static string PageCount(int n) => n == 1 ? "1 page" : n.ToString(CultureInfo.InvariantCulture) + " pages";

    string SummaryText(Listing listing)
    {
        var summary = listing.Summary;
        if (model!.Searching)
            return (summary.Pages == 1 ? "1 result" : summary.Pages.ToString(CultureInfo.InvariantCulture) + " results") + $" for “{model.Query}”";
        if (summary.Pages == 0) return "Nothing here yet";
        var parts = new List<string> { PageCount(summary.Pages) };
        if (summary.NewToday > 0 && model.Section != LibrarySection.Done) parts.Add(summary.NewToday.ToString(CultureInfo.InvariantCulture) + " new today");
        parts.Add(summary.Sites == 1 ? "1 site" : summary.Sites.ToString(CultureInfo.InvariantCulture) + " sites");
        return string.Join(" · ", parts);
    }

    void UpdateEmptyDone()
    {
        if (model is null) return;
        EmptyDone.Visibility = model.Section == LibrarySection.Done && !model.Searching && model.Current.Summary.Pages > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyDone.IsEnabled = actions?.CanEdit ?? false;
    }

    // MARK: Selection

    /// The list control shows what the model says is selected. `scroll`: bring the keyboard's page into view (when the model moved it, not the user).
    void ApplySelection(bool scroll)
    {
        if (model is null) return;
        syncing = true;
        try
        {
            var wanted = entries.OfType<RowModel>().Where(r => model.Selected.Contains(r.Id)).Cast<object>().ToList();
            foreach (var item in Rows.SelectedItems.ToList()) if (!wanted.Contains(item)) Rows.SelectedItems.Remove(item);
            foreach (var item in wanted) if (!Rows.SelectedItems.Contains(item)) Rows.SelectedItems.Add(item);
            if (scroll && model.Cursor is { } cursor && entries.OfType<RowModel>().FirstOrDefault(r => r.Id == cursor) is { } row) Rows.ScrollIntoView(row);
        }
        finally { syncing = false; }
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || model is null) return;
        // Arrow keys step onto headings too; the selection goes on to the next page that way.
        if (e.AddedItems.OfType<GroupHeaderRow>().FirstOrDefault() is { } header)
        {
            int at = entries.IndexOf(header);
            int step = at >= lastIndex ? 1 : -1;
            syncing = true;
            try { Rows.SelectedItems.Remove(header); }
            finally { syncing = false; }
            object? target = NextRow(at, step) ?? NextRow(at, -step);
            if (target is null) return;
            Rows.SelectedItems.Add(target);   // raises this event again for the page
            Rows.ScrollIntoView(target);
            DispatcherQueue.TryEnqueue(() => (Rows.ContainerFromItem(target) as Control)?.Focus(FocusState.Keyboard));
            return;
        }
        var added = e.AddedItems.OfType<RowModel>().LastOrDefault();
        if (added is not null) lastIndex = entries.IndexOf(added);
        fromList = true;
        try { model.SetSelection(Rows.SelectedItems.OfType<RowModel>().Select(r => r.Id), added?.Id); }
        finally { fromList = false; }
    }

    object? NextRow(int from, int step)
    {
        for (int i = from + step; i >= 0 && i < entries.Count; i += step) if (entries[i] is RowModel) return entries[i];
        return null;
    }

    // MARK: Rows

    void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        AutomationProperties.SetName(args.ItemContainer, args.Item.ToString());
        if (args.Item is RowModel { Visits.Length: > 0 } row) AutomationProperties.SetItemStatus(args.ItemContainer, row.Visits);
        else AutomationProperties.SetItemStatus(args.ItemContainer, "");
    }

    /// Hovering a row swaps its date for Open and Done, in the same space.
    void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => SetHover((Grid)sender, true);

    void OnRowPointerExited(object sender, PointerRoutedEventArgs e) => SetHover((Grid)sender, false);

    void SetHover(Grid row, bool on)
    {
        var right = (Grid)row.Children[2];
        var buttons = (StackPanel)right.Children[1];
        if (on) foreach (var b in buttons.Children.OfType<Button>().Skip(1)) b.IsEnabled = actions?.CanEdit ?? false;
        right.Children[0].Opacity = on ? 0 : 1;
        buttons.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    Item? ItemOf(object sender) => sender is FrameworkElement { Tag: Guid id } ? model?.Current.Items.FirstOrDefault(i => i.Id == id) : null;

    void OnRowOpen(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) actions?.Open([item]); }

    void OnRowDone(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) actions?.SetDone([item], item.Status != ItemStatus.Done); }

    void OnRowKeep(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) actions?.KeepInList([item]); }

    void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string title }) model?.ToggleFold(title);
    }

    /// A double-click opens the page (or the selection, when it is one of them).
    void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (model is null || actions is null || RowAt(e.OriginalSource) is not { } row) return;
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift)) return;
        actions.Open(Targets(row));
        e.Handled = true;
    }

    RowModel? RowAt(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ListViewItem container) return Rows.ItemFromContainer(container) as RowModel;
        return null;
    }

    /// A page that is selected acts with the whole selection, any other page alone.
    IReadOnlyList<Item> Targets(RowModel row)
    {
        if (model!.Selected.Contains(row.Id)) return model.SelectedItems;
        return model.Current.Items.Where(i => i.Id == row.Id).ToList();
    }

    // MARK: Context menu

    void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (model is null || actions is null || RowAt(e.OriginalSource) is not { } row) return;
        var picked = Targets(row);
        if (picked.Count == 0) return;
        var flyout = BuildMenu(picked);
        if (e.TryGetPosition(Rows, out var point)) flyout.ShowAt(Rows, new FlyoutShowOptions { Position = point });
        else flyout.ShowAt(e.OriginalSource as FrameworkElement ?? Rows);
        e.Handled = true;
    }

    MenuFlyout BuildMenu(IReadOnlyList<Item> picked)
    {
        var a = actions!;
        var menu = new MenuFlyout();
        int n = picked.Count;
        bool allDone = picked.All(i => i.Status == ItemStatus.Done);
        MenuFlyoutItem Entry(string text, string glyph, Action click, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => click();
            return item;
        }
        menu.Items.Add(Entry(n > 1 ? $"Open {n} Pages" : "Open", "", () => a.Open(picked)));
        foreach (var browser in Opener.Installed())
            menu.Items.Add(Entry($"Open in {browser.DisplayName()}", "", () => a.Open(picked, browser)));
        menu.Items.Add(Entry(n > 1 ? $"Copy {n} Links" : "Copy Link", "", () => ItemActions.CopyLinks(picked)));
        menu.Items.Add(new MenuFlyoutSeparator());
        bool edit = a.CanEdit;
        var move = new MenuFlyoutSubItem { Text = "Move To", Icon = new FontIcon { Glyph = "" }, IsEnabled = edit };
        foreach (var kind in KindInfo.All)
            move.Items.Add(new MenuFlyoutItem { Text = kind.Title(), Icon = new FontIcon { Glyph = kind.Glyph() }, IsEnabled = !picked.All(i => i.Kind == kind) }
                .With(item => item.Click += (_, _) => a.Move(picked, kind)));
        menu.Items.Add(move);
        menu.Items.Add(Entry("Not an Idea", "", () => a.NotAnIdea(picked), edit));
        if (picked.All(i => i.IsActiveRegular)) menu.Items.Add(Entry("Keep Pages Like This", "", () => a.KeepInList(picked), edit));
        else menu.Items.Add(Entry(allDone ? "Move Back to Later" : "Mark Done", allDone ? "" : "", () => a.SetDone(picked, !allDone), edit));
        if (picked.All(i => i.Regular == false)) menu.Items.Add(Entry("This Is a Regular", "", () => a.MarkRegular(picked), edit));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Entry("Delete", "", () => a.Delete(picked), edit));
        return menu;
    }

    // MARK: Keys

    static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// Enter opens the selection, Delete deletes it, Space toggles Done, Esc clears the search, then the selection. (The arrows, Home, End,
    /// Shift and Ctrl+A are the list control's own.)
    void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (model is null || actions is null || e.OriginalSource is Button || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)) return;   // a button has its own Enter and Space
        var selected = model.SelectedItems;
        switch (e.Key)
        {
            case VirtualKey.Enter when selected.Count > 0:
                actions.Open(selected);
                e.Handled = true;
                break;
            case VirtualKey.Delete when selected.Count > 0:
                actions.Delete(selected);
                e.Handled = true;
                break;
            case VirtualKey.Space when selected.Count > 0:
                // A regular still in the list has its own choice instead of Done.
                if (!(selected.Count == 1 && selected[0].IsActiveRegular)) actions.SetDone(selected, !selected.All(i => i.Status == ItemStatus.Done));
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                if (model.Searching) ClearSearchRequested?.Invoke(this, EventArgs.Empty);
                else if (selected.Count > 0) model.ClearSelection();
                else break;
                e.Handled = true;
                break;
        }
    }

    /// Typing a letter in the list starts a search with it.
    void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        char c = e.Character;
        if (char.IsControl(c) || char.IsWhiteSpace(c) || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)) return;
        TypeToSearch?.Invoke(this, new string(c, 1));
        e.Handled = true;
    }

    // MARK: Empty state, notice, Undo

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
        else if (model.IsLoaded && section == LibrarySection.All && model.Counts.Count == 0)   // nothing saved at all (not: not loaded yet)
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

    void OnEmptyDone(object sender, RoutedEventArgs e) => actions?.EmptyDone();

    void ShowNotice()
    {
        var text = model?.Engine.Notice;
        if (text is null) dismissed = null;
        Notice.Message = text ?? "";
        Notice.IsOpen = text is not null && text != dismissed;
    }

    void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => dismissed = model?.Engine.Notice;

    /// The change just made, for a few seconds, with its Undo.
    void ShowUndo()
    {
        if (model?.UndoName is { } name && actions is not null) UndoToast.Show(name, actions.Undo);
        else UndoToast.Hide();
    }
}

internal static class MenuExtensions
{
    /// Runs `setup` on the item and returns it, so an item can be built and wired in one expression.
    public static T With<T>(this T item, Action<T> setup)
    {
        setup(item);
        return item;
    }
}
