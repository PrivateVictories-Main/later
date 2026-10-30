using System.Diagnostics;
using Later.Core;
using Later.Windows.Reader;
using Windows.System;

namespace Later.App.Actions;

/// Opens a saved link as a new tab. Only ever called from the user's click or key, never from the engine. Only web links open, and
/// the link is one argument of the browser's own process: no shell, so nothing in it can be taken for a command.
internal static class Opener
{
    /// Whether the link is a web page (`http` or `https` with a host): nothing else is ever opened.
    public static bool IsWebLink(string url) =>
        ParsedUrl.Parse(url) is { Host: { Length: > 0 } } parts
        && (parts.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || parts.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

    /// Where a link opens: the preferred browser if it is running, else any running one, else the first installed (Helium first).
    /// Null when none is installed.
    public static Browser? BrowserFor(Browser? preferred, Func<Browser, bool>? isRunning = null, Func<Browser, bool>? isInstalled = null)
    {
        isRunning ??= BrowserDiscovery.IsBrowserRunning;
        isInstalled ??= b => BrowserDiscovery.ExePath(b) is not null;
        var order = new List<Browser>();
        if (preferred is { } first) order.Add(first);
        order.AddRange(BrowserInfo.All);
        foreach (var b in order) if (isRunning(b)) return b;
        foreach (var b in order) if (isInstalled(b)) return b;
        return null;
    }

    /// The browsers that are installed, for the "Open in" choices.
    public static IReadOnlyList<Browser> Installed() => BrowserInfo.All.Where(b => BrowserDiscovery.ExePath(b) is not null).ToList();

    /// How the browser is started for the link: its own executable, the link its only argument, no shell. Null for anything but a web link.
    public static ProcessStartInfo? Plan(string url, string exePath)
    {
        if (!IsWebLink(url)) return null;
        var start = new ProcessStartInfo(exePath) { UseShellExecute = false };
        start.ArgumentList.Add(url);   // one argument, quoted by the runtime: a space or a quote in the link stays in it
        return start;
    }

    /// Opens in the browser that `BrowserFor` picks, else in the system's default browser. False when nothing was opened.
    public static bool Open(string url, Browser? preferred)
    {
        if (!IsWebLink(url)) return false;
        return BrowserFor(preferred) is { } browser ? Open(url, browser) : OpenWithDefault(url);
    }

    public static bool Open(string url, Browser browser)
    {
        if (BrowserDiscovery.ExePath(browser) is not { } exe) return false;
        if (Plan(url, exe) is not { } start) return false;
        try { using var _ = Process.Start(start); return true; }   // a new tab in the running browser, or the browser opening
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    static bool OpenWithDefault(string url)
    {
        try { _ = Launcher.LaunchUriAsync(new Uri(url)); return true; }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }
}
