using Later.App.ViewModels;
using Later.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Later.App.Actions;

/// Everything the user can do to saved pages, shared by the context menu, the row buttons, the keyboard and the inspector. Every
/// change goes to the list's owner (`IListEditor`), is undoable, and several pages are one Undo. Opening only ever happens here,
/// on a click or a key.
internal sealed class ItemActions(LibraryViewModel model, IListEditor? editor, Func<XamlRoot?> root)
{
    /// Opening more pages than this at once asks first, wherever it is asked for.
    public const int OpenLimit = 10;

    bool asking;   // one dialog at a time

    public bool CanEdit => model.CanEdit && editor is not null;

    /// Where "Open" goes, from the same state the inspector's label reads (`EngineState.OpenIn`). Without an engine to ask (a sample, a
    /// list shown read-only) the label says just "Open" and the machine's own browsers are looked at, on the click.
    public Browser? DefaultBrowser => model.Engine.Target() ?? Opener.BrowserFor(model.Engine.LastBrowser);

    public void Open(IReadOnlyList<Item> items, Browser? inBrowser = null)
    {
        if (items.Count == 0) return;
        if (items.Count > OpenLimit) AskToOpen(items, inBrowser); else OpenNow(items, inBrowser);
    }

    void OpenNow(IReadOnlyList<Item> items, Browser? inBrowser)
    {
        var browser = model.Engine.Target(inBrowser);
        foreach (var item in items)
            _ = browser is { } b ? Opener.Open(item.Url, b) : Opener.Open(item.Url, model.Engine.LastBrowser);
    }

    async void AskToOpen(IReadOnlyList<Item> items, Browser? inBrowser)
    {
        if (asking || root() is not { } xaml) return;
        asking = true;
        try
        {
            var browser = inBrowser ?? DefaultBrowser;
            var dialog = new ContentDialog
            {
                XamlRoot = xaml,
                Title = $"Open {items.Count} pages" + (browser is { } b ? $" in {b.DisplayName()}?" : "?"),
                Content = "Each page opens in a new tab.",
                PrimaryButtonText = $"Open {items.Count} Pages",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) OpenNow(items, inBrowser);
        }
        finally { asking = false; }
    }

    public static void CopyLinks(IReadOnlyList<Item> items)
    {
        if (items.Count == 0) return;
        var data = new DataPackage();
        data.SetText(string.Join("\n", items.Select(i => i.Url)));
        Clipboard.SetContent(data);
    }

    public void SetDone(IReadOnlyList<Item> items, bool done) => Change(items, (library, undo) => ItemChanges.SetDone(library, undo, Ids(items), done, DateTime.UtcNow));
    public void Move(IReadOnlyList<Item> items, Kind kind) => Change(items, (library, undo) => ItemChanges.Move(library, undo, Ids(items), kind));
    public void KeepInList(IReadOnlyList<Item> items) => Change(items, (library, undo) => ItemChanges.KeepInList(library, undo, Ids(items)));
    public void MarkRegular(IReadOnlyList<Item> items) => Change(items, (library, undo) => ItemChanges.MarkRegular(library, undo, Ids(items)));
    public void NotAnIdea(IReadOnlyList<Item> items) => Change(items, (library, undo) => ItemChanges.NotAnIdea(library, undo, Ids(items)));
    public void Delete(IReadOnlyList<Item> items) => Change(items, (library, undo) => ItemChanges.Delete(library, undo, Ids(items), DateTime.UtcNow));

    /// Everything Done shows, as one Undo. Asks first: the whole list of what is finished goes at once.
    public async void EmptyDone()
    {
        if (!CanEdit || asking || root() is not { } xaml) return;
        var shown = model.Current.Items.Where(i => i.Status == ItemStatus.Done).ToList();
        if (shown.Count == 0) return;
        asking = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xaml,
                Title = "Delete everything in Done?",
                Content = "Ctrl+Z brings it all back.",
                PrimaryButtonText = "Empty Done",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                Change(shown, (library, undo) => ItemChanges.EmptyDone(library, undo, DateTime.UtcNow));
        }
        finally { asking = false; }
    }

    public void Undo()
    {
        if (!CanEdit) return;
        model.ClearUndoOffer();
        editor!.Undo();
    }

    public void Redo()
    {
        if (!CanEdit) return;
        model.ClearUndoOffer();
        editor!.Redo();
    }

    static List<Guid> Ids(IReadOnlyList<Item> items) => items.Select(i => i.Id).ToList();

    /// Sends a change for these pages; when it takes selected ones out of the list, the selection moves on to the next.
    void Change(IReadOnlyList<Item> items, Action<Library, UndoStack> work)
    {
        if (items.Count == 0 || !CanEdit) return;   // read-only: the buttons are disabled, and keys do nothing
        int token = model.BeginChange(items.Select(i => i.Id));
        editor!.Apply(token, work);
    }
}
