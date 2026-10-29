using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Later.Windows.Native;

namespace Later.Windows.Engine;

/// Lock, unlock, sleep, wake, display on or off and shutdown, from one hidden top-level window. It is never shown, and it is not message-only:
/// message-only windows are not sent broadcasts such as `WM_POWERBROADCAST`. Create it on a thread that pumps messages (the
/// engine thread) and dispose it there.
public sealed unsafe class SessionNotifier : IDisposable
{
    const string ClassName = "Later.Hidden";
    const uint WmEndSession = 0x0016, WmPowerBroadcast = 0x0218, WmSessionChange = 0x02B1;
    const int WtsSessionLock = 7, WtsSessionUnlock = 8;
    const int PbtSuspend = 4, PbtResumeSuspend = 7, PbtResumeAutomatic = 0x12, PbtPowerSettingChange = 0x8013;
    /// GUID_CONSOLE_DISPLAY_STATE: the data is 0 (display off), 1 (on) or 2 (dimmed). Modern Standby starts when the display goes
    /// off, so this arrives where a suspend message may not.
    static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    static readonly NativeApi.WindowProc Proc = OnMessage;   // a static field: the class keeps the delegate alive for the process
    static readonly ConcurrentDictionary<nint, SessionNotifier> Instances = new();
    static readonly object RegisterGate = new();
    static bool classRegistered;

    public event Action? Locked, Unlocked, Suspending, Resumed, SessionEnding;
    /// The display went off (false) or is on or dimmed (true).
    public event Action<bool>? DisplayChanged;

    /// The hidden window; the tests send it the messages the system would.
    public nint Hwnd { get; }

    /// Whether Windows accepted the session-change registration (lock and unlock arrive only when it did).
    public bool RegisteredForSessionChanges { get; }

    /// Whether Windows accepted the display-state registration.
    public bool RegisteredForDisplayChanges => displayRegistration != 0;
    readonly nint displayRegistration;

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
        displayRegistration = NativeApi.RegisterPowerSettingNotification(Hwnd, in ConsoleDisplayState, 0 /*DEVICE_NOTIFY_WINDOW_HANDLE*/);
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
                case WmPowerBroadcast when (int)wParam == PbtPowerSettingChange:
                    notifier.OnPowerSetting((PowerBroadcastSetting*)lParam);
                    return 1;
                case WmEndSession when wParam != 0: notifier.SessionEnding?.Invoke(); return 0;
            }
        }
        return NativeApi.DefWindowProc(hwnd, message, wParam, lParam);
    }

    void OnPowerSetting(PowerBroadcastSetting* setting)
    {
        if (setting is not null && setting->PowerSetting == ConsoleDisplayState && setting->DataLength >= 4) DisplayChanged?.Invoke(setting->Data != 0);
    }

    public void Dispose()
    {
        if (Hwnd == 0 || !Instances.TryRemove(Hwnd, out _)) return;
        if (RegisteredForSessionChanges) _ = NativeApi.WTSUnRegisterSessionNotification(Hwnd);
        if (displayRegistration != 0) _ = NativeApi.UnregisterPowerSettingNotification(displayRegistration);
        _ = NativeApi.DestroyWindow(Hwnd);
    }
}
