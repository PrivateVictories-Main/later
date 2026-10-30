using Later.App.Actions;
using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Later.App.Panes;

/// The right pane: with nothing selected an overview of the list (how many pages across how many sites, by kind, top sites), with one
/// page its details and what can be done with it, with several a summary and the same actions for all. What it says comes from
/// `InspectorModel`; this only draws it and sends the clicks to `ItemActions`.
public sealed partial class InspectorPane : UserControl
{
    LibraryViewModel? model;
    ItemActions? actions;
    InspectorModel? shown;
    Guid? copiedFor;   // "Copied" until another page is picked

    public InspectorPane() => InitializeComponent();

    internal void Bind(LibraryViewModel viewModel, ItemActions itemActions)
    {
        model = viewModel;
        actions = itemActions;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.Current) or nameof(LibraryViewModel.Counts) or nameof(LibraryViewModel.Selected) or nameof(LibraryViewModel.Engine))
                Update();
        };
        Update();
    }

    void Update()
    {
        if (model is null || actions is null) return;
        var openIn = model.Engine.OpenIn?.DisplayName();   // from the engine's snapshot: never a look at the machine's processes on this thread
        var view = InspectorModel.For(model, openIn);
        shown = view;
        OverviewPanel.Visibility = view.Mode == InspectorMode.Overview ? Visibility.Visible : Visibility.Collapsed;
        PagePanel.Visibility = view.Mode == InspectorMode.Page ? Visibility.Visible : Visibility.Collapsed;
        PagesPanel.Visibility = view.Mode == InspectorMode.Pages ? Visibility.Visible : Visibility.Collapsed;
        switch (view.Mode)
        {
            case InspectorMode.Overview: ShowOverview(view); break;
            case InspectorMode.Page: ShowPage(view); break;
            default: ShowPages(view); break;
        }
    }

    // MARK: Overview

    void ShowOverview(InspectorModel view)
    {
        Caption.Text = view.Caption;
        KindList.ItemsSource = view.Kinds;
        ByKind.Visibility = view.Kinds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SiteList.ItemsSource = view.Sites;
        TopSites.Visibility = view.Sites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnKindClick(object sender, RoutedEventArgs e)
    {
        if (model is { Searching: false } && sender is FrameworkElement { Tag: Kind kind }) model.Section = LibrarySection.OfKind(kind);
    }

    void OnSiteClick(object sender, RoutedEventArgs e)
    {
        if (model is { Searching: false } && sender is FrameworkElement { Tag: string host }) SearchFor?.Invoke(this, host);
    }

    /// An overview's site was clicked: the window puts it in the search field.
    public event EventHandler<string>? SearchFor;

    // MARK: One page

    void ShowPage(InspectorModel view)
    {
        var item = view.Item!;
        if (copiedFor != item.Id) copiedFor = null;
        PageTile.Host = item.Host;
        PageTitle.Text = view.Title;
        WhyGlyph.Glyph = item.Regular == true ? "" : "";   // repeat for a regular, magnifier for the rest
        WhyText.Text = view.Why;
        ToolTipService.SetToolTip(LinkText, item.Url);
        LinkText.Text = view.Link;
        FactList.ItemsSource = view.Facts;
        SetOpen(OpenButton, OpenText, view);
        CopyText.Text = copiedFor == item.Id ? "Copied" : "Copy Link";
        CopyGlyph.Glyph = copiedFor == item.Id ? "" : "";
        DoneButton.Visibility = view.ShowsDone ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(CopyButton, view.ShowsDone ? 1 : 2);   // Copy Link takes the whole row when Done is not offered
        SetDone(DoneButton, DoneText, DoneGlyph, view);
        CorrectionButton.Visibility = view.CorrectionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        if (view.CorrectionLabel is { } label)
        {
            CorrectionText.Text = label;
            CorrectionGlyph.Glyph = view.CorrectionKeeps ? "" : "";
            AutomationProperties.SetName(CorrectionButton, label);
            ToolTipService.SetToolTip(CorrectionButton, view.CorrectionKeeps
                ? "Move this page, and pages like it, back to your list (Ctrl+Z undoes it)"
                : "Move this page, and pages like it, out of your list (Ctrl+Z brings them back)");
        }
        FillMoveMenu(MoveButton, view);
        foreach (var b in new Control[] { DoneButton, CorrectionButton, MoveButton, NotAnIdeaButton, DeleteButton }) b.IsEnabled = view.CanEdit;
    }

    // MARK: Several pages

    void ShowPages(InspectorModel view)
    {
        PagesHeading.Text = view.Heading;
        PagesSites.Text = view.SiteSummary;
        SetOpen(PagesOpenButton, PagesOpenText, view);
        SetDone(PagesDoneButton, PagesDoneText, PagesDoneGlyph, view);
        PagesDeleteText.Text = view.DeleteLabel;
        AutomationProperties.SetName(PagesDeleteButton, view.DeleteLabel);
        FillMoveMenu(PagesMoveButton, view);
        foreach (var b in new Control[] { PagesDoneButton, PagesMoveButton, PagesNotIdeasButton, PagesDeleteButton }) b.IsEnabled = view.CanEdit;
    }

    // MARK: Shared parts

    static void SetOpen(SplitButton button, TextBlock text, InspectorModel view)
    {
        text.Text = view.OpenLabel;
        AutomationProperties.SetName(button, view.OpenLabel);
    }

    static void SetDone(Button button, TextBlock text, FontIcon glyph, InspectorModel view)
    {
        text.Text = view.DoneLabel;
        glyph.Glyph = view.AllDone ? "" : "";
        AutomationProperties.SetName(button, view.DoneLabel);
    }

    /// The seven kinds with their glyphs; the one every selected page already has is dimmed.
    void FillMoveMenu(DropDownButton button, InspectorModel view)
    {
        var menu = new MenuFlyout();
        foreach (var kind in KindInfo.All)
        {
            var entry = new MenuFlyoutItem { Text = kind.Title(), Icon = new FontIcon { Glyph = kind.Glyph() }, IsEnabled = !view.Items.All(i => i.Kind == kind) };
            entry.Click += (_, _) => actions?.Move(view.Items, kind);
            menu.Items.Add(entry);
        }
        button.Flyout = menu;
    }

    /// The Open button's other choices: each installed browser.
    void OnOpenMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu || shown is null) return;
        var view = shown;
        menu.Items.Clear();
        foreach (var browser in Opener.Installed())
        {
            var entry = new MenuFlyoutItem { Text = $"Open in {browser.DisplayName()}" };
            entry.Click += (_, _) => actions?.Open(view.Items, browser);
            menu.Items.Add(entry);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No browser found", IsEnabled = false });
    }

    void OnOpenClick(SplitButton sender, SplitButtonClickEventArgs args) { if (shown is not null) actions?.Open(shown.Items); }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (shown is null) return;
        ItemActions.CopyLinks(shown.Items);
        if (shown.Item is { } item) { copiedFor = item.Id; CopyText.Text = "Copied"; CopyGlyph.Glyph = ""; }
    }

    void OnDone(object sender, RoutedEventArgs e) { if (shown is not null) actions?.SetDone(shown.Items, !shown.AllDone); }

    void OnCorrection(object sender, RoutedEventArgs e)
    {
        if (shown is null) return;
        if (shown.CorrectionKeeps) actions?.KeepInList(shown.Items); else actions?.MarkRegular(shown.Items);
    }

    void OnNotAnIdea(object sender, RoutedEventArgs e) { if (shown is not null) actions?.NotAnIdea(shown.Items); }

    /// A Delete is about to be made from here: the list takes the keyboard to the next page afterwards (`how` the user reached the button).
    public event EventHandler<FocusState>? DeleteStarting;

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (shown is null || actions is not { CanEdit: true }) return;
        DeleteStarting?.Invoke(this, (sender as Control)?.FocusState ?? FocusState.Programmatic);
        actions.Delete(shown.Items);
    }
}
