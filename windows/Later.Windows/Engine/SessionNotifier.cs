using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Later.Windows.Native;

namespace Later.Windows.Engine;

/// Lock, unlock, sleep, wake and shutdown, from one hidden top-level window. It is never shown, and it is not message-only:
/// message-only windows are not sent broadcasts such as `WM_POWERBROADCAST`. Create it on a thread that pumps messages (the
/// engine thread) and dispose it there.
public sealed unsafe class SessionNotifier : IDisposable
{
    const string ClassName = "Later.Hidden";
    const uint WmEndSession = 0x0016, WmPowerBroadcast = 0x0218, WmSessionChange = 0x02B1;
    const int WtsSessionLock = 7, WtsSessionUnlock = 8;
    const int PbtSuspend = 4, PbtResumeSuspend = 7, PbtResumeAutomatic = 0x12;

    static readonly NativeApi.WindowProc Proc = OnMessage;   // a static field: the class keeps the delegate alive for the process
    static readonly ConcurrentDictionary<nint, SessionNotifier> Instances = new();
    static readonly object RegisterGate = new();
    static bool classRegistered;

    public event Action? Locked, Unlocked, Suspending, Resumed, SessionEnding;

    /// The hidden window; the tests send it the messages the system would.
    public nint Hwnd { get; }

    /// Whether Windows accepted the session-change registration (lock and unlock arrive only when it did).
    public bool RegisteredForSessionChanges { get; }

    public SessionNotifier()
    {
        var instance = NativeApi.GetModuleHandle(0);
        fixed (char* name = ClassName)
        {
            lock (RegisterGate)
            {
                if (!classRegistered)
                {
                    var wc = new WndClassEx { Size = (uint)sizeof(WndClassEx), WndProc = Marshal.GetFunctionPointerForDelegate(Proc), Instance = instance, ClassName = name };
                    if (NativeApi.RegisterClassEx(in wc) == 0) throw new Win32Exception();
                    classRegistered = true;
                }
            }
            Hwnd = NativeApi.CreateWindowEx(0, name, name, NativeApi.WsPopup, 0, 0, 0, 0, 0, 0, instance, 0);
        }
        if (Hwnd == 0) throw new Win32Exception();
        Instances[Hwnd] = this;
        RegisteredForSessionChanges = NativeApi.WTSRegisterSessionNotification(Hwnd, 0 /*NOTIFY_FOR_THIS_SESSION*/) != 0;
    }

    static nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hwnd, out var notifier))
        {
            switch (message)
            {
                case WmSessionChange when (int)wParam == WtsSessionLock: notifier.Locked?.Invoke(); return 0;
                case WmSessionChange when (int)wParam == WtsSessionUnlock: notifier.Unlocked?.Invoke(); return 0;
                case WmPowerBroadcast when (int)wParam == PbtSuspend: notifier.Suspending?.Invoke(); return 1;
                case WmPowerBroadcast when (int)wParam is PbtResumeAutomatic or PbtResumeSuspend: notifier.Resumed?.Invoke(); return 1;
                case WmEndSession when wParam != 0: notifier.SessionEnding?.Invoke(); return 0;
            }
        }
        return NativeApi.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Hwnd == 0 || !Instances.TryRemove(Hwnd, out _)) return;
        if (RegisteredForSessionChanges) _ = NativeApi.WTSUnRegisterSessionNotification(Hwnd);
        _ = NativeApi.DestroyWindow(Hwnd);
    }
}
