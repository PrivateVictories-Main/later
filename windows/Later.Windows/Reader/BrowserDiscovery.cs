using System.Diagnostics;
using Later.Core;
using Later.Windows.Native;
using Microsoft.Win32;

namespace Later.Windows.Reader;

public sealed record BrowserWindow(nint Hwnd, uint Pid, Browser Browser, string Title, int ZIndex, bool IsMinimized);

public static class BrowserDiscovery
{
    const string BrowserWindowClass = "Chrome_WidgetWin_1";
    static readonly Dictionary<string, Browser?> BrowserOfPath = new(StringComparer.OrdinalIgnoreCase);   // by executable, so a reused pid can't borrow another program's answer
    static readonly object Gate = new();

    /// Top-level, visible, titled `Chrome_WidgetWin_1` windows of Chrome and Helium, z-order top first.
    public static IReadOnlyList<BrowserWindow> Enumerate()
    {
        var found = new List<BrowserWindow>();
        User32.EnumWindows((hwnd, lParam) =>
        {
            if (User32.IsWindowVisible(hwnd) == 0 || User32.Class(hwnd) != BrowserWindowClass) return 1;
            var title = User32.Title(hwnd);
            if (title.Length == 0) return 1;
            if ((User32.GetWindowLong(hwnd, User32.GwlExStyle) & User32.WsExToolWindow) != 0 || User32.GetWindow(hwnd, User32.GwOwner) != 0) return 1;
            _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
            if (BrowserOfProcess(pid) is { } browser)
                found.Add(new BrowserWindow(hwnd, pid, browser, title, found.Count, User32.IsIconic(hwnd) != 0));
            return 1;
        }, 0);
        return found;
    }

    /// The browser a window belongs to; null for other programs (and for windows that are not browser windows).
    public static Browser? BrowserOfWindow(nint hwnd)
    {
        if (hwnd == 0 || User32.Class(hwnd) != BrowserWindowClass) return null;
        _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
        return BrowserOfProcess(pid);
    }

    public static Browser? FrontBrowser() => BrowserOfWindow(User32.GetForegroundWindow());

    public static bool IsBrowserRunning(Browser b)
    {
        foreach (var p in Process.GetProcessesByName("chrome"))
            using (p)
                if (BrowserOfProcess((uint)p.Id) == b) return true;
        return false;
    }

    /// The running process's path, else the installed one, else null.
    public static string? ExePath(Browser b)
    {
        foreach (var p in Process.GetProcessesByName("chrome"))
            using (p)
                if (User32.ImagePath((uint)p.Id) is { } path && BrowserOfProcess((uint)p.Id) == b) return path;
        if (b == Browser.Chrome)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                if (hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe")?.GetValue(null) is string exe && File.Exists(exe)) return exe;
            return null;
        }
        var helium = Path.Combine(LocalAppData, "imput", "Helium", "Application", "chrome.exe");
        return File.Exists(helium) ? helium : null;
    }

    public static string DataFolder(Browser b) => b == Browser.Chrome
        ? Path.Combine(LocalAppData, "Google", "Chrome", "User Data")
        : Path.Combine(LocalAppData, "imput", "Helium", "User Data");

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    static Browser? BrowserOfProcess(uint pid)
    {
        if (User32.ImagePath(pid) is not { } path) return null;
        lock (Gate)
        {
            if (BrowserOfPath.TryGetValue(path, out var known)) return known;
            Browser? browser = null;
            try { browser = BrowserInfo.FromProductName(FileVersionInfo.GetVersionInfo(path).ProductName); }
            catch (FileNotFoundException) { }
            return BrowserOfPath[path] = browser;
        }
    }
}
