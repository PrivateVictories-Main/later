using System.ComponentModel;
using Later.Windows.Native;

namespace Later.Windows.Reader;

public enum WatchEventKind { NameChanged, Foreground, WindowShown, WindowDestroyed }

/// Wake-ups from the system's event hooks: name change (a tab switch retitles the window), foreground, show and destroy.
/// Nothing runs while nothing happens. The hooks are out-of-context, so events arrive on the thread that called `Start()`,
/// which must pump messages, and `Dispose()` belongs on that thread too.
public sealed class WinEventWatcher : IDisposable
{
    const string BrowserWindowClass = "Chrome_WidgetWin_1";
    readonly Action<WatchEventKind, nint> onEvent;
    readonly User32.WinEventProc callback;   // a field, so the delegate outlives the hooks
    readonly List<nint> hooks = [];
    volatile HashSet<nint> known = [];       // replaced whole, so the callback reads it without a lock

    public WinEventWatcher(Action<WatchEventKind, nint> onEvent) { this.onEvent = onEvent; callback = OnWinEvent; }

    public void Start()
    {
        if (hooks.Count > 0) return;
        foreach (var e in new[] { User32.EventObjectNameChange, User32.EventSystemForeground, User32.EventObjectDestroy, User32.EventObjectShow })
        {
            nint hook = User32.SetWinEventHook(e, e, 0, callback, 0, 0, User32.WinEventOutOfContext | User32.WinEventSkipOwnProcess);
            if (hook == 0) { Dispose(); throw new Win32Exception(); }
            hooks.Add(hook);
        }
    }

    /// DESTROY arrives when the window's class can no longer be read, so only windows seen earlier pass.
    public void SetKnownBrowserWindows(IReadOnlyCollection<nint> hwnds) => known = [.. hwnds];

    void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (idObject != User32.ObjIdWindow || hwnd == 0) return;
        switch (eventType)
        {
            case User32.EventSystemForeground: onEvent(WatchEventKind.Foreground, hwnd); break;
            case User32.EventObjectNameChange when User32.Class(hwnd) == BrowserWindowClass: onEvent(WatchEventKind.NameChanged, hwnd); break;
            case User32.EventObjectShow when User32.Class(hwnd) == BrowserWindowClass: onEvent(WatchEventKind.WindowShown, hwnd); break;
            case User32.EventObjectDestroy when known.Contains(hwnd): onEvent(WatchEventKind.WindowDestroyed, hwnd); break;
        }
    }

    public void Dispose()
    {
        foreach (var h in hooks) _ = User32.UnhookWinEvent(h);
        hooks.Clear();
    }
}
