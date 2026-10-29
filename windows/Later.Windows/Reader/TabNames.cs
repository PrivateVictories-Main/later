using System.Text.RegularExpressions;

namespace Later.Windows.Reader;

public static partial class TabNames
{
    [GeneratedRegex(@"(?<=.)\s[-‐-―−]\s[^-‐-―−]+\s[-‐-―−]\s\d[\d.,]*\s?\p{L}{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryUsage();

    /// A tab's UIA name without the browser's " - Memory usage - N MB" tooltip suffix, in any language
    /// (the words are localized): the two-dash shape is what tells it from a title like "Download size - 17 MB".
    public static string Clean(string uiaName) => MemoryUsage().Replace(uiaName, "").Trim();
}
