using System.Text.Json;
namespace Later.Core;

public sealed record Settings(bool Onboarded = false, bool Paused = false, bool SavesFeedHomes = false,
                              LibrarySort Sort = LibrarySort.LastSeen, bool SortReversed = false,
                              LibraryGrouping Grouping = LibraryGrouping.Date);

public static class SettingsStore
{
    // camelCase raw values, the same words the Mac keeps in UserDefaults; the order is the enums' order
    static readonly string[] SortNames = ["tabOrder", "tabOrderReversed", "dateOpened", "dateSaved", "lastSeen", "title", "site"];
    static readonly string[] GroupNames = ["date", "site", "kind"];

    /// Never throws: a missing, unreadable or damaged file gives the defaults.
    public static Settings Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            bool Flag(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            int Index(string n, string[] names, int fallback) =>
                r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && Array.IndexOf(names, v.GetString()) is var i and >= 0 ? i : fallback;
            return new Settings(Flag("onboarded"), Flag("paused"), Flag("savesFeedHomes"),
                (LibrarySort)Index("sort", SortNames, (int)LibrarySort.LastSeen), Flag("sortReversed"),
                (LibraryGrouping)Index("grouping", GroupNames, (int)LibraryGrouping.Date));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return new Settings(); }
    }

    public static void Save(string path, Settings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteBoolean("onboarded", s.Onboarded); w.WriteBoolean("paused", s.Paused); w.WriteBoolean("savesFeedHomes", s.SavesFeedHomes);
            w.WriteString("sort", SortNames[(int)s.Sort]); w.WriteBoolean("sortReversed", s.SortReversed); w.WriteString("grouping", GroupNames[(int)s.Grouping]);
            w.WriteEndObject();
        }
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, path, overwrite: true);
    }
}
