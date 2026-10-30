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
        Visibility = Visibility.Visible;
        // A live region announces a change of its text while it shows, not text that was set before it did: set it now, and say so.
        Message.Text = "";
        Message.Text = name;
        FrameworkElementAutomationPeer.FromElement(Message)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
