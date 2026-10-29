namespace Later.Core;

/// "/dp/*", "/*/status/*", "/r/*/comments/**", "/watch?v". `*` one segment, `**` the rest (zero or more),
/// `ref=*`-style trailing `*` matches a segment by prefix, `?name` requires that query key.
public sealed class PathPattern
{
    public string Text { get; }
    readonly string[] segments;
    readonly string? queryKey;

    public PathPattern(string text)
    {
        Text = text;
        var halves = text.Split('?', 2);
        segments = halves[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToLowerInvariant()).ToArray();
        queryKey = halves.Length > 1 ? halves[1].ToLowerInvariant() : null;
    }

    public static implicit operator PathPattern(string text) => new(text);

    internal bool Matches(IReadOnlyList<string> path, ISet<string> queryKeys)
    {
        if (queryKey is not null && !queryKeys.Contains(queryKey)) return false;
        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment == "**") return true;
            if (i >= path.Count) return false;
            var part = path[i].ToLowerInvariant();
            if (segment == "*" || segment == part || (segment.EndsWith('*') && part.StartsWith(segment[..^1], StringComparison.Ordinal))) continue;
            return false;
        }
        return segments.Length == path.Count;
    }

    /// The query key a pattern names ("/watch?v" → "v"), when the path alone matches.
    internal string? IdentityKey(IReadOnlyList<string> path) =>
        queryKey is not null && Matches(path, new HashSet<string> { queryKey }) ? queryKey : null;
}
