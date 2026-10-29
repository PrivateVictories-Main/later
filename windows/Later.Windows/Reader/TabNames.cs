using System.Text.RegularExpressions;

namespace Later.Windows.Reader;

public static partial class TabNames
{
    [GeneratedRegex(@"(?<=.) - Memory usage - [\d.,]+\s?(?:KB|MB|GB)$", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryUsage();

    /// A tab's UIA name without the browser's " - Memory usage - N MB" tooltip suffix.
    public static string Clean(string uiaName) => MemoryUsage().Replace(uiaName, "").Trim();
}
