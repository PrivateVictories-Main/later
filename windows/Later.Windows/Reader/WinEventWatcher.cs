using System.ComponentModel;
using Later.Windows.Native;

namespace Later.Windows.Reader;

public enum WatchEventKind { NameChanged, Foreground, WindowShown, WindowDestroyed }

/// Wake-ups from event hooks. Foreground changes are watched system-wide (rare, and how a browser that just started is
/// noticed). Name change, show and destroy are watched only inside browser processes: one registration per browser pid,
/// added when a browser is seen (`Retarget`, or its window coming to the front) and removed when it is gone, so other
/// programs' window traffic is never delivered here. Nothing runs while nothing happens.
/// The hooks are out-of-context, so events arrive on the thread that registered them, which must pump messages;
/// `Start`, `Retarget` and `Dispose` belong on that thread.
public sealed class WinEventWatcher : IDisposable
{
    const string BrowserWindowClass = "Chrome_WidgetWin_1";
    static readonly uint[] PerBrowserEvents = [User32.EventObjectNameChange, User32.EventObjectDestroy, User32.EventObjectShow];
    readonly Action<WatchEventKind, nint> onEvent;
    readonly User32.WinEventProc callback;   // a field, so the delegate outlives the hooks
    readonly Dictionary<uint, nint[]> perBrowser = [];
    nint foregroundHook;
    long eventsSeen;
    volatile HashSet<nint> known = [];       // replaced whole, so the callback reads it without a lock
    volatile uint[] watched = [];            // the keys of perBrowser, readable from any thread

    public WinEventWatcher(Action<WatchEventKind, nint> onEvent) { this.onEvent = onEvent; callback = OnWinEvent; }

    /// Every event the system delivered to this watcher, before filtering.
    public long EventsSeen => Interlocked.Read(ref eventsSeen);

    /// The browser processes whose windows are being watched.
    public IReadOnlyCollection<uint> WatchedPids => watched;

    public void Start()
    {
        if (foregroundHook != 0) return;
        foregroundHook = Hook(User32.EventSystemForeground, 0);
    }

    /// Watches exactly these browser processes: adds the new ones, drops the ones no longer listed.
    public void Retarget(IReadOnlyCollection<uint> pids)
    {
        foreach (var gone in perBrowser.Keys.Where(p => !pids.Contains(p)).ToList()) Unhook(gone);
        foreach (var pid in pids)
        {
            try { Watch(pid); }
            catch (Win32Exception) { }   // one process that can't be hooked (gone, or not ours) must not leave the others unwatched; tried again at the next Retarget
        }
    }

    /// DESTROY arrives when the window's class can no longer be read, so only windows seen earlier pass.
    public void SetKnownBrowserWindows(IReadOnlyCollection<nint> hwnds) => known = [.. hwnds];

    void Watch(uint pid)
    {
        if (pid == 0 || perBrowser.ContainsKey(pid)) return;
        var hooks = new List<nint>();
        try { foreach (var e in PerBrowserEvents) hooks.Add(Hook(e, pid)); }
        catch { foreach (var h in hooks) _ = User32.UnhookWinEvent(h); throw; }
        perBrowser[pid] = [.. hooks];
        watched = [.. perBrowser.Keys];
    }

    void Unhook(uint pid)
    {
        if (!perBrowser.Remove(pid, out var hooks)) return;
        foreach (var h in hooks) _ = User32.UnhookWinEvent(h);
        watched = [.. perBrowser.Keys];
    }

    nint Hook(uint e, uint pid)
    {
        nint hook = User32.SetWinEventHook(e, e, 0, callback, pid, 0, User32.WinEventOutOfContext | User32.WinEventSkipOwnProcess);
        return hook != 0 ? hook : throw new Win32Exception();
    }

    void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        Interlocked.Increment(ref eventsSeen);
        if (idObject != User32.ObjIdWindow || hwnd == 0) return;
        switch (eventType)
        {
            case User32.EventSystemForeground:
                if (BrowserDiscovery.BrowserOfWindow(hwnd) is not null)
                {
                    _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
                    try { Watch(pid); } catch (Win32Exception) { }   // a browser that started since the last look; tried again at the next switch
                }
                onEvent(WatchEventKind.Foreground, hwnd);
                break;
            case User32.EventObjectNameChange when User32.Class(hwnd) == BrowserWindowClass: onEvent(WatchEventKind.NameChanged, hwnd); break;
            case User32.EventObjectShow when User32.Class(hwnd) == BrowserWindowClass: onEvent(WatchEventKind.WindowShown, hwnd); break;
            case User32.EventObjectDestroy when known.Contains(hwnd): onEvent(WatchEventKind.WindowDestroyed, hwnd); break;
        }
    }

    public void Dispose()
    {
        foreach (var pid in perBrowser.Keys.ToList()) Unhook(pid);
        if (foregroundHook != 0) { _ = User32.UnhookWinEvent(foregroundHook); foregroundHook = 0; }
    }
}
