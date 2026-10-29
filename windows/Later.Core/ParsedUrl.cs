using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Later.Core;

public readonly record struct QueryItem(string Name, string? Value);

/// A link split into its still-encoded parts, like Foundation's URLComponents. Parsing normalizes nothing but encodes
/// characters the component may not hold. Characterized against macOS; the vectors are the arbiter.
public sealed partial record ParsedUrl(string Scheme, string? User, string? Password, string? Host, int? Port,
                                       string EncodedPath, string? EncodedQuery, string? EncodedFragment)
{
    [GeneratedRegex(@"^(?:([^:/?#]+):)?(?://([^/?#]*))?([^?#]*)(?:\?([^#]*))?(?:#(.*))?$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3986();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeShape();

    public static ParsedUrl? Parse(string text)
    {
        var m = Rfc3986().Match(text);
        if (!m.Success || !m.Groups[1].Success || !SchemeShape().IsMatch(m.Groups[1].Value)) return null;
        string? user = null, password = null, host = null; int? port = null;
        if (m.Groups[2].Success)
        {
            var authority = m.Groups[2].Value;
            int at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                var info = authority[..at]; authority = authority[(at + 1)..];
                int colon = info.IndexOf(':', StringComparison.Ordinal);
                user = UrlText.EncodeInvalid(colon < 0 ? info : info[..colon], UrlText.UserChar);
                if (colon >= 0) password = UrlText.EncodeInvalid(info[(colon + 1)..], UrlText.UserChar);
            }
            string hostText = authority;
            if (authority.StartsWith('['))
            {
                int close = authority.IndexOf(']', StringComparison.Ordinal);
                if (close < 0) return null;
                hostText = authority[..(close + 1)];
                var rest = authority[(close + 1)..];
                if (rest.Length > 0) { if (rest[0] != ':' || !TryPort(rest[1..], out port)) return null; }
            }
            else
            {
                int colon = authority.LastIndexOf(':');
                if (colon >= 0) { hostText = authority[..colon]; if (!TryPort(authority[(colon + 1)..], out port)) return null; }
            }
            host = EncodeHost(hostText);
            if (host is null) return null;
        }
        var path = UrlText.EncodeInvalid(m.Groups[3].Value, UrlText.PathChar);
        var query = m.Groups[4].Success ? UrlText.EncodeInvalid(m.Groups[4].Value, UrlText.QueryChar) : null;
        var fragment = m.Groups[5].Success ? UrlText.EncodeInvalid(m.Groups[5].Value, UrlText.QueryChar) : null;
        return new ParsedUrl(m.Groups[1].Value, user, password, host, port, path, query, fragment);
    }

    static bool TryPort(string s, out int? port)
    {
        port = null;
        if (s.Length == 0) return true;   // "host:" has an empty port
        if (!s.All(Text.IsAsciiDigit) || !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p)) return false;
        port = p; return true;
    }

    /// Non-ASCII hosts become punycode (Foundation's host is punycode too).
    static string? EncodeHost(string host)
    {
        if (host.All(c => c < 128)) return host;
        try { return new IdnMapping().GetAscii(host); } catch (ArgumentException) { return null; }
    }

    public string Absolute
    {
        get
        {
            var sb = new StringBuilder(Scheme).Append(':');
            if (Host is not null)
            {
                sb.Append("//");
                if (User is not null) { sb.Append(User); if (Password is not null) sb.Append(':').Append(Password); sb.Append('@'); }
                sb.Append(Host);
                if (Port is not null) sb.Append(':').Append(Port.Value.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(EncodedPath);
            if (EncodedQuery is not null) sb.Append('?').Append(EncodedQuery);
            if (EncodedFragment is not null) sb.Append('#').Append(EncodedFragment);
            return sb.ToString();
        }
    }

    public override string ToString() => Absolute;

    public string DecodedPath => UrlText.PercentDecode(EncodedPath) ?? EncodedPath;
    /// `URLComponents.fragment`: decoded.
    public string? DecodedFragment => EncodedFragment is null ? null : UrlText.PercentDecode(EncodedFragment) ?? EncodedFragment;

    /// `percentEncodedQueryItems`: name and value still encoded; a missing `=` gives a null value.
    public IReadOnlyList<QueryItem> EncodedQueryItems => EncodedQuery is null ? [] : ParseItems(EncodedQuery);

    public static IReadOnlyList<QueryItem> ParseItems(string query)
    {
        var items = new List<QueryItem>();
        if (query.Length == 0) return items;   // "a?" has no items at all
        foreach (var piece in query.Split('&'))   // Foundation keeps empty pieces: "a=1&&b=2" has an item with an empty name and no value
        {
            int eq = piece.IndexOf('=', StringComparison.Ordinal);
            items.Add(eq < 0 ? new QueryItem(piece, null) : new QueryItem(piece[..eq], piece[(eq + 1)..]));
        }
        return items;
    }

    public static string JoinItems(IEnumerable<QueryItem> items) =>
        string.Join('&', items.Select(i => i.Value is null ? i.Name : i.Name + "=" + i.Value));

    public ParsedUrl WithQueryItems(IReadOnlyList<QueryItem> items) => this with { EncodedQuery = items.Count == 0 ? null : JoinItems(items) };

    /// `url.host` for display and host matching.
    public string HostOrEmpty => Host ?? "";
}
