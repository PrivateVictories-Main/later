using Later.Core;

namespace Later.Windows.Reader;

static class ProfileFiles
{
    /// The `file` (`Favicons`, or `History`) of each of a Chromium data folder's profiles: Default, then Profile 1, Profile 2, ...
    public static IReadOnlyList<string> In(string dataFolder, string file)
    {
        if (!Directory.Exists(dataFolder)) return [];
        var names = Directory.EnumerateDirectories(dataFolder).Select(d => Path.GetFileName(d))
            .Where(n => n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal)).ToList();
        names.Sort((a, b) => a == "Default" ? (b == "Default" ? 0 : -1) : b == "Default" ? 1 : NaturalComparer.Compare(a, b));
        return names.Select(n => Path.Combine(dataFolder, n, file)).Where(File.Exists).ToList();
    }
}
