using System.Runtime.InteropServices;

namespace Later.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMsg { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WndClassEx
{
    public uint Size, Style;
    public nint WndProc;
    public int ClassExtra, WindowExtra;
    public nint Instance, Icon, Cursor, Background;
    public char* MenuName, ClassName;
    public nint SmallIcon;
}

/// Kernel objects, the thread's message queue and the hidden session window: the calls the engine thread, the lock and
/// the session notifier make. (The reader's window and hook calls are in `User32`.)
internal static unsafe partial class NativeApi
{
    internal const uint WaitObject0 = 0, WaitAbandoned0 = 0x80, WaitTimeout = 0x102, WaitFailed = 0xFFFFFFFF, Infinite = 0xFFFFFFFF;
    internal const uint PmRemove = 1, QsAllInput = 0x04FF, MwmoInputAvailable = 0x0004;
    internal const uint WmQuit = 0x0012, WmApp = 0x8000;
    internal const uint WsPopup = 0x80000000;

    internal delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateMutex(nint attributes, int initialOwner, string name);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateEvent(nint attributes, int manualReset, int initialState, string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial int ReleaseMutex(nint mutex);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial int SetEvent(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial int ResetEvent(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] internal static partial nint GetModuleHandle(nint name);
    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] internal static partial int GetProcessMemoryInfo(nint process, out ProcessMemory counters, uint size);
    [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentProcess();

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] internal static partial int PeekMessage(out NativeMsg msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] internal static partial int TranslateMessage(in NativeMsg msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static partial nint DispatchMessage(in NativeMsg msg);
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")] internal static partial int PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)] internal static partial ushort RegisterClassEx(in WndClassEx wndClass);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    internal static partial nint CreateWindowEx(uint exStyle, char* className, char* title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static partial nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] internal static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);
    [LibraryImport("user32.dll")] internal static partial int UnregisterPowerSettingNotification(nint handle);

    [LibraryImport("wtsapi32.dll", SetLastError = true)] internal static partial int WTSRegisterSessionNotification(nint hwnd, uint flags);
    [LibraryImport("wtsapi32.dll")] internal static partial int WTSUnRegisterSessionNotification(nint hwnd);
}

/// POWERBROADCAST_SETTING for a setting whose data is one DWORD (the console display state is).
[StructLayout(LayoutKind.Sequential)]
internal struct PowerBroadcastSetting { public Guid PowerSetting; public uint DataLength; public int Data; }

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemory
{
    public uint Size, PageFaultCount;
    public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage,
        PagefileUsage, PeakPagefileUsage, PrivateUsage;
}
