using System.Runtime.InteropServices;

namespace Later.Windows.Native;

/// The handful of window, process and event-hook calls the reader needs.
internal static unsafe partial class User32
{
    internal delegate int EnumWindowsProc(nint hwnd, nint lParam);
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    internal const uint EventSystemForeground = 0x0003, EventObjectDestroy = 0x8001, EventObjectShow = 0x8002, EventObjectNameChange = 0x800C;
    internal const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    internal const int ObjIdWindow = 0;

    [LibraryImport("user32.dll")] internal static partial int EnumWindows(EnumWindowsProc callback, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] internal static partial int GetClassName(nint hwnd, char* buffer, int max);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] internal static partial int GetWindowText(nint hwnd, char* buffer, int max);
    [LibraryImport("user32.dll")] internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] internal static partial int IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial int IsIconic(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial nint GetWindow(nint hwnd, uint command);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static partial int GetWindowLong(nint hwnd, int index);
    [LibraryImport("user32.dll")] internal static partial nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint pid, uint thread, uint flags);
    [LibraryImport("user32.dll")] internal static partial int UnhookWinEvent(nint hook);
    [LibraryImport("kernel32.dll")] internal static partial nint OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")] internal static partial int QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);
    [LibraryImport("kernel32.dll")] internal static partial int CloseHandle(nint handle);

    internal const uint GwOwner = 4, ProcessQueryLimitedInformation = 0x1000;
    internal const int GwlExStyle = -20, WsExToolWindow = 0x80;

    internal static string Class(nint hwnd)
    {
        char* buffer = stackalloc char[64];
        int n = GetClassName(hwnd, buffer, 64);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    internal static string Title(nint hwnd)
    {
        char* buffer = stackalloc char[512];
        int n = GetWindowText(hwnd, buffer, 512);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    /// The path of a process's executable, or null when it can't be read (gone, or not ours to open).
    internal static string? ImagePath(uint pid)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, 0, pid);
        if (process == 0) return null;
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageName(process, 0, buffer, ref size) != 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally { _ = CloseHandle(process); }
    }
}
