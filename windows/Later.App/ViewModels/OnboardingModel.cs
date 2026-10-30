using System.ComponentModel;
using Later.Core;
using Later.Windows.Platform;

namespace Later.App.ViewModels;

/// A browser the welcome lists: installed, and open right now or not.
public sealed record OnboardingBrowser(Browser Browser, bool IsOpen, string? ExePath)
{
    public string Name => Browser.DisplayName();
    public string Status => IsOpen ? "Open" : "Not open";
}

/// The first-run welcome: which browsers Later can read, and what "Start Using Later" does. A sample run has no settings path, so it
/// writes nothing.
internal sealed class OnboardingModel(IReadOnlyList<OnboardingBrowser> browsers, IStartAtSignIn start, string? settingsPath)
{
    public const string NoBrowserText = "Later works with Helium and Chrome. Install one, then open Later again.";

    /// The browsers that are installed. `exePath` is null for one that is not; `isOpen` asks whether it is running.
    public static List<OnboardingBrowser> Discover(Func<Browser, string?> exePath, Func<Browser, bool> isOpen) =>
        BrowserInfo.All.Select(b => (Browser: b, Exe: exePath(b))).Where(x => x.Exe is not null).Select(x => new OnboardingBrowser(x.Browser, isOpen(x.Browser), x.Exe)).ToList();

    public IReadOnlyList<OnboardingBrowser> Browsers => browsers;

    /// Set when setup could not be finished; the window says so and stays.
    public string? Failure { get; private set; }

    /// Marks setup done (a settings file, written whole or not at all) and turns on starting at sign-in. True when the window can go on to the
    /// library. Starting at sign-in is a courtesy: if Windows refuses, setup still finishes, and Settings shows how it stands.
    public bool Start()
    {
        Failure = null;
        if (settingsPath is null) return true;
        try { SettingsStore.Save(settingsPath, SettingsStore.Load(settingsPath) with { Onboarded = true }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failure = "Later couldn't save its settings, so setup isn't finished. Check that your user folder can be written to, then try again.";
            return false;
        }
        try { start.Set(true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException) { }
        return true;
    }
}
