using Later.Core;

namespace Later.Windows.Reader;

/// What the reader sees of a browser window's chrome (never its tabs): the accessible title of the window's root view and
/// the name of the toolbar's profile button. A normal window reads "<page> - Chrome" and "You"; a private one reads
/// "<page> - Chrome (Incognito)" and "Incognito" (verified on Chrome and Helium, x64 and Arm64). The window text and the
/// top-level UIA name carry no marker, so they are not used.
public sealed record RawWindowInfo(Browser Browser, string RootViewName, string? ProfileButtonName);

public static class PrivateWindowDetector
{
    /// English interface text; other languages are not verified (see the design notes).
    public static bool IsPrivate(RawWindowInfo w) =>
        w.RootViewName.Contains("Incognito", StringComparison.OrdinalIgnoreCase) ||
        w.ProfileButtonName?.Contains("Incognito", StringComparison.OrdinalIgnoreCase) == true;
}
