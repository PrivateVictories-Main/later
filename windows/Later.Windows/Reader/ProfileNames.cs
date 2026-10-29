using System.Text.Json;

namespace Later.Windows.Reader;

/// The display names of a Chromium data folder's profiles, from its `Local State`: a second profile's windows carry
/// " - <name>" in their accessible title. Re-read only when the file changes. Names stay in memory.
sealed class ProfileNames(string dataFolder)
{
    static readonly string[] Keys = ["name", "gaia_name", "gaia_given_name", "shortcut_name"];
    IReadOnlyCollection<string> names = [];
    DateTime stamp; long length = -1;

    public IReadOnlyCollection<string> Current()
    {
        try
        {
            var file = new FileInfo(Path.Combine(dataFolder, "Local State"));
            if (!file.Exists) return names = [];
            if (file.LastWriteTimeUtc == stamp && file.Length == length) return names;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            names = Parse(stream); stamp = file.LastWriteTimeUtc; length = file.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { names = []; stamp = default; length = -1; }
        return names;
    }

    public static IReadOnlyCollection<string> Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object
            && profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            foreach (var entry in cache.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
                foreach (var key in Keys)
                    if (entry.Value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } name) found.Add(name);
        return found;
    }
}
