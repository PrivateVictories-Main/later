using System.Globalization;

namespace Later.Windows.Reader;

public enum WindowKind { Normal, Private, Unrecognized }

/// Decides from the window's chrome alone (never its tabs or page) whether a browser window may be read.
///
/// The rule is positive evidence, with no language in it: a browser names a window's accessible title (the root view's name)
/// like the window text, plus a suffix only in the windows that are not plain ones. A private window adds "(Incognito)" in
/// the interface language ("(anonym)", "（シークレット モード）", "(Navigation privée)", ...), a guest window adds its own, a
/// second profile's window adds " - <profile name>". So a window is normal when the two are equal, or when the difference
/// is " - " and a profile name read from the browser's own `Local State`. Anything else (private, guest, an unknown
/// language's suffix, a title still changing) is not normal and is never read.
public static class PrivateWindowDetector
{
    public static bool IsNormal(string windowText, string rootViewName, Func<IReadOnlyCollection<string>> profileNames) =>
        Classify(windowText, rootViewName, profileNames) == WindowKind.Normal;

    /// Not normal is two things for the caller. `Private`: the browser appended something other than " - <name>" (the
    /// private and guest markers), so the window is deliberately left out. `Unrecognized`: it might be a normal window that
    /// is not understood yet (a second profile whose name `Local State` has not been flushed with, or a chrome still being built),
    /// so a read that skipped it is not a complete view and must be tried again.
    public static WindowKind Classify(string windowText, string rootViewName, Func<IReadOnlyCollection<string>> profileNames)
    {
        var text = Plain(windowText); var name = Plain(rootViewName);
        if (text.Length == 0 || name.Length == 0) return WindowKind.Unrecognized;
        if (name == text) return WindowKind.Normal;
        if (!name.StartsWith(text, StringComparison.Ordinal)) return WindowKind.Unrecognized;   // a different title: the window moved while it was read
        if (!name.StartsWith(text + " - ", StringComparison.Ordinal)) return WindowKind.Private;
        return profileNames().Any(p => Plain(p).Length > 0 && name == text + " - " + Plain(p)) ? WindowKind.Normal : WindowKind.Unrecognized;
    }

    /// Without invisible direction marks (right-to-left interfaces add them to one of the two) and edge spaces.
    static string Plain(string s) => new string([.. s.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();
}
