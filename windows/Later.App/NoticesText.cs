namespace Later.App;

/// The open-source notices that ship beside the program, for the About section of Settings.
internal static class NoticesText
{
    public const string FileName = "THIRD-PARTY-NOTICES.txt";
    public const string Missing = "The full notices are in THIRD-PARTY-NOTICES.txt, next to Later.exe.";
    const int MaxChars = 200_000;   // a notices file is tens of kilobytes; a damaged or odd one never fills the window

    /// The file's text, trimmed; a line saying where to find it when it is not there or cannot be read.
    public static string Read(string? folder = null)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(folder ?? AppContext.BaseDirectory, FileName)).Trim();
            return text.Length == 0 ? Missing : text.Length > MaxChars ? text[..MaxChars] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Missing; }
    }
}
