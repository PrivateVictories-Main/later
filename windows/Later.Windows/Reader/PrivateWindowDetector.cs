using Later.Core;

namespace Later.Windows.Reader;

/// What the reader can see of a window before deciding whether it is private: the window text and the top-level UI Automation name.
public sealed record RawWindowInfo(Browser Browser, string WindowTitle, string UiaName);

public static class PrivateWindowDetector
{
    /// Placeholder until the probe run shows the real marker on every browser and architecture.
    public static bool IsPrivate(RawWindowInfo w) =>
        w.WindowTitle.Contains("Incognito", StringComparison.OrdinalIgnoreCase) || w.UiaName.Contains("Incognito", StringComparison.OrdinalIgnoreCase);
}
