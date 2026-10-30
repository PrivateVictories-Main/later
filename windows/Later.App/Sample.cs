namespace Later.App;

#if DEBUG
using System.Globalization;
using Later.Core;

/// Development only: `--sample` shows a made-up list as if already set up; a sample run starts no engine and no hooks, touches no
/// library file and writes no setting, and no browser is watched or read. Every `--sample…` flag means a sample run. Compiled out of
/// Release builds, where `Enabled` is a constant false (so the Release binary carries none of this, like the Mac's).
///   --sample-section <name>   --sample-group date|site|kind   --sample-sort <rawValue>   --sample-reversed
///   --sample-search <text>    --sample-select <n>             --sample-dark              --sample-onboarding   --sample-settings
///   --shot <png>  --shot-screen <png>  --shot-size WxH  --shot-delay <ms>   save the window (PrintWindow, a screen copy) and quit
internal static class Sample
{
    static readonly string[] Args = Environment.GetCommandLineArgs();

    public static readonly bool Enabled = Args.Any(a => a.StartsWith("--sample", StringComparison.Ordinal));
    public static bool Dark => Has("--sample-dark");
    public static bool Reversed => Has("--sample-reversed");
    public static bool Onboarding => Has("--sample-onboarding");
    public static bool Settings => Has("--sample-settings");
    public static string? Search => Value("--sample-search");

    /// The sample's clock, so every machine, hour and time zone draws the same counts: today at 12:00 local, or `--sample-now <ISO>`
    /// (a time without a zone is local). Not the real clock outside a sample run.
    public static DateTime Clock => Enabled ? Pinned : DateTime.UtcNow;

    static readonly DateTime Pinned =
        DateTime.TryParse(Value("--sample-now"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var pinned) ? pinned : SampleLibrary.NoonUtc(DateTime.Now);
    public static string? ShotPath => Value("--shot");
    public static string? ShotScreenPath => Value("--shot-screen");

    public static LibrarySection? Section
    {
        get
        {
            var name = Value("--sample-section");
            foreach (var section in LibrarySection.Ordered) if (string.Equals(section.Title, name, StringComparison.OrdinalIgnoreCase)) return section;
            return null;
        }
    }

    public static LibraryGrouping? Group => Enum.TryParse<LibraryGrouping>(Value("--sample-group"), ignoreCase: true, out var g) ? g : null;
    public static LibrarySort? Sort => Enum.TryParse<LibrarySort>(Value("--sample-sort"), ignoreCase: true, out var s) ? s : null;

    /// `--sample-select N` selects the first N pages (for the inspector's screenshots).
    public static int SelectCount => int.TryParse(Value("--sample-select"), out var n) ? n : 0;

    public static (int Width, int Height) ShotSize
    {
        get
        {
            var parts = (Value("--shot-size") ?? "").Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (1100, 720);
        }
    }

    public static int ShotDelayMs => int.TryParse(Value("--shot-delay"), out var ms) ? ms : 1500;

    /// A scene whose window does not exist yet: the app exits with 3 ("not built") before anything opens (Tasks 8 and 9 build them).
    public static bool NotBuilt => Enabled && (Onboarding || Settings || SelectCount > 0);

    static bool Has(string flag) => Args.Contains(flag, StringComparer.Ordinal);

    static string? Value(string flag)
    {
        int i = Array.IndexOf(Args, flag);
        return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }
}
#else
internal static class Sample
{
    public const bool Enabled = false;
    public static DateTime Clock => DateTime.UtcNow;
}
#endif
