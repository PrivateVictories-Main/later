using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Later.App.Controls;

/// "{ActionName}" and an Undo button for 6 seconds after a change, then gone. A one-shot timer started by the change and stopped by the
/// next one: nothing runs while it is hidden.
public sealed partial class UndoBar : UserControl
{
    static readonly TimeSpan Shown = TimeSpan.FromSeconds(6);

    readonly DispatcherQueueTimer timer;
    Action? undo;

    public UndoBar()
    {
        InitializeComponent();
        timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Shown;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Hide();
    }

    public void Show(string name, Action onUndo)
    {
        undo = onUndo;
        Message.Text = name;
        Visibility = Visibility.Visible;
        // The text was set before the bar showed, which a live region does not announce reliably: say it outright.
        (FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this))
            .RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, $"{name}. Press Control Z to undo.", "UndoOffer");
        timer.Stop();
        timer.Start();
    }

    public void Hide()
    {
        timer.Stop();
        Visibility = Visibility.Collapsed;
        undo = null;
    }

    void OnUndo(object sender, RoutedEventArgs e)
    {
        var action = undo;
        Hide();
        action?.Invoke();
    }
}
