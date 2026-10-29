using System.Diagnostics;
using Later.Core;

namespace Later.Windows.Tests.Integration;

/// One headed browser instance on its own throwaway profile, pointed at the local fixture pages.
internal sealed class BrowserFixture : IDisposable
{
    public Process Process { get; }
    public string Profile { get; }
    BrowserFixture(Process process, string profile) { Process = process; Profile = profile; }

    public static BrowserFixture Start(string exe, FixtureServer server, string[] urls, bool incognito = false, string? profile = null, bool restore = false)
    {
        profile ??= Path.Combine(Path.GetTempPath(), "LaterIT-" + Guid.NewGuid().ToString("N"));
        var args = new List<string>
        {
            $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--disable-sync",
            "--disable-features=Translate", $"--host-resolver-rules=MAP fixture.example.com 127.0.0.1:{server.Port}", "--new-window",
        };
        if (incognito) args.Add("--incognito");
        if (restore) args.Add("--restore-last-session");
        args.AddRange(urls);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return new BrowserFixture(Process.Start(psi)!, profile);
    }

    /// Graceful close (needed for the session file), then wait; kill only as a last resort.
    public void Close()
    {
        if (Process.HasExited) return;
        Process.CloseMainWindow();
        if (!Process.WaitForExit(10_000)) Process.Kill(true);
    }

    public void Dispose()
    {
        Close();
        for (int i = 0; i < 40; i++)   // the browser lets go of its files a moment after exit
        {
            try { if (Directory.Exists(Profile)) Directory.Delete(Profile, true); return; }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
        throw new IOException($"could not delete {Profile}");
    }

    public static string? Exe(Browser b) => Environment.GetEnvironmentVariable(b == Browser.Chrome ? "LATER_CHROME" : "LATER_HELIUM");
}
