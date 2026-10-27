using System.Text;

namespace Later.Core;

public static class LinkCleaner
{
    /// The cleaned link, or null when the page must never be tracked or saved.
    public static ParsedUrl? Clean(string raw)
    {
        var parts = ParsedUrl.Parse(raw);
        if (parts is null) return null;
        var scheme = parts.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https") return null;
        var host = parts.Host?.ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return null;
        if (parts.User is not null || parts.Password is not null) return null;
        int? port = parts.Port;
        if ((scheme == "https" && port == 443) || (scheme == "http" && port == 80)) port = null;
        parts = parts with { Scheme = scheme, Host = host, Port = port, EncodedPath = parts.DecodedPath.Length == 0 ? "/" : parts.EncodedPath };

        var items = parts.EncodedQueryItems.ToList();   // encoded, so kept parameters keep their exact encoding (C%2B%2B must not become C++)
        if (items.Any(i => IsSecret(Key(i.Name)))) return null;
        items.RemoveAll(i => IsTracking(Key(i.Name), host));
        parts = parts.WithQueryItems(items);

        if (parts.EncodedFragment is { } fragment)
        {
            var route = parts.DecodedFragment ?? fragment;   // decoded, so #%2F… and #%21… count as routes too
            if (!(route.StartsWith('/') || route.StartsWith('!'))) parts = parts with { EncodedFragment = null };
            else
            {
                // Route fragments like #/login/callback?token=… carry their own query; check it encoded and decoded once,
                // since a hash router reads #/login%3Ftoken=x as /login?token=x.
                foreach (var text in new[] { fragment, route })
                {
                    int q = text.IndexOf('?', StringComparison.Ordinal);
                    if (q < 0) continue;
                    var names = text[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => Key(p.Split('=', 2)[0]));
                    if (names.Any(IsSecret)) return null;
                }
            }
        }

        var asin = AmazonAsin(host, parts.DecodedPath);
        if (asin is not null) return new ParsedUrl("https", null, null, host, null, "/dp/" + asin, null, null);
        return parts;
    }

    /// One string for one page, from a cleaned link: the same page opened as www or bare, over http or https, with or
    /// without a trailing or doubled slash, with its query in another order, or with noise keys (a YouTube timestamp, a
    /// playlist position) is one page. Pages that differ in path, in an identity key's value, or in a product variant
    /// (color, size) stay apart. Not a link: it is only ever compared and hashed.
    public static string PageKey(ParsedUrl url)
    {
        if (url.Host is null) return url.Absolute;
        var rawHost = ToUnicodeHost(url.Host).ToLowerInvariant();   // URLComponents.host gives the Unicode form of a punycode host
        var host = rawHost.StartsWith("www.", StringComparison.Ordinal) ? rawHost[4..] : rawHost;
        var path = Text.SplitOmitEmpty(url.EncodedPath, '/');
        var items = url.EncodedQueryItems.ToList();
        if (host == "youtu.be" && path.Count == 1)   // youtu.be/<id> is youtube.com/watch?v=<id>
        {
            host = "youtube.com";
            items.Add(new QueryItem("v", path[0]));
            path = ["watch"];
        }
        // A Google document is its id, whether it is opened to edit, view or preview.
        if (host == "docs.google.com" && DocumentKinds.Contains(path.Count > 0 ? path[0] : "")
            && path.Count > 0 && path[^1] is "edit" or "view" or "preview")
        {
            path.RemoveAt(path.Count - 1);
        }
        var identity = SiteRules.IdentityKey(host, path);
        if (identity is not null)
        {
            items.RemoveAll(i => Key(i.Name) != identity);
        }
        else
        {
            bool idPath = path.Any(Structure.IsIDLike);
            bool youtube = Matches(host, YoutubeHosts);
            items.RemoveAll(i =>
            {
                var name = Key(i.Name);
                return IsTracking(name, host) || (youtube && YoutubeNoiseKeys.Contains(name)) || (idPath && ViewStateKeys.Contains(name));
            });
        }
        items = Text.StableSort(items, (a, b) =>
        {
            int c = string.CompareOrdinal(a.Name, b.Name);
            return c != 0 ? c : string.CompareOrdinal(a.Value ?? "", b.Value ?? "");
        });
        var result = new StringBuilder(host);
        if (url.Port is { } port) result.Append(':').Append(port);
        result.Append('/').Append(string.Join('/', path));
        if (items.Count > 0) result.Append('?').Append(ParsedUrl.JoinItems(items));
        if (url.EncodedFragment is { } route) result.Append('#').Append(route);
        return result.ToString();
    }

    static string ToUnicodeHost(string host)
    {
        if (!host.Contains("xn--", StringComparison.OrdinalIgnoreCase)) return host;
        try { return new System.Globalization.IdnMapping().GetUnicode(host); } catch (ArgumentException) { return host; }
    }

    static readonly HashSet<string> DocumentKinds = ["document", "spreadsheets", "presentation"];
    static readonly string[] YoutubeHosts = ["youtube.com", "youtu.be"];
    static readonly HashSet<string> YoutubeNoiseKeys =
        ["autoplay", "embeds_referring_euri", "source_ve_path", "t", "start", "feature", "pp", "si"];
    /// How a page is shown, not which page it is, on pages named by an ID in their path.
    static readonly HashSet<string> ViewStateKeys = ["view", "state", "tab", "sort", "order", "mode"];

    /// True for `name.<storefront suffix>` and its subdomains, e.g. amazon.com, www.amazon.co.uk.
    public static bool IsStorefront(string host, string name) =>
        Matches(host.ToLowerInvariant(), StorefrontSuffixes.Select(s => name + "." + s));

    static readonly string[] StorefrontSuffixes =
    [
        "com", "co.uk", "de", "fr", "it", "es", "ca", "com.au", "co.jp", "in", "com.mx", "com.br", "nl", "se",
        "pl", "sg", "ae", "sa", "com.tr", "eg", "com.be", "ie", "at", "ch", "be", "cn",
    ];

    internal static string Key(string encodedName) => (UrlText.PercentDecode(encodedName) ?? encodedName).ToLowerInvariant();

    /// Exact secret keys, or keys with a secret word as a piece, e.g. auth_token, x-amz-signature.
    internal static bool IsSecret(string key) =>
        SecretKeys.Contains(key)
        || Text.SplitWhere(key, r => !Text.IsLetter(r) && !Text.IsNumber(r)).Any(SecretWords.Contains);

    static readonly HashSet<string> SecretWords =
    [
        "token", "code", "session", "sessionid", "auth", "authorization", "key", "apikey", "password", "passwd",
        "pwd", "signature", "sig", "otp", "secret", "credential", "credentials",
    ];

    static readonly HashSet<string> SecretKeys =
    [
        "token", "access_token", "id_token", "refresh_token", "code", "session", "sessionid", "session_id",
        "auth", "authorization", "key", "api_key", "apikey", "password", "passwd", "pwd",
        "signature", "sig", "otp", "magic_link", "reset_token",
    ];

    static readonly HashSet<string> TrackingKeys =
    [
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "mc_cid", "mc_eid", "igshid", "igsh",
        "ref", "ref_", "ref_src", "ref_url", "referrer", "spm", "_hsenc", "_hsmi", "yclid", "twclid",
        "li_fat_id", "srsltid",
    ];
    static readonly HashSet<string> AmazonKeys =
    [
        "tag", "psc", "th", "qid", "sr", "keywords", "crid", "sprefix", "content-id", "linkcode", "linkid",
        "camp", "creative", "creativeasin", "ascsubtag", "smid", "dib", "dib_tag",
    ];
    static readonly HashSet<string> YoutubeKeys = ["si", "feature", "pp"];
    static readonly string[] TwitterHosts = ["x.com", "twitter.com"];
    static readonly string[] NotionHosts = ["notion.so", "notion.site"];

    internal static bool IsTracking(string key, string host)
    {
        if (key.StartsWith("utm_", StringComparison.Ordinal) || key.StartsWith("pd_rd_", StringComparison.Ordinal)
            || key.StartsWith("pf_rd_", StringComparison.Ordinal)) return true;
        if (TrackingKeys.Contains(key)) return true;
        if (IsStorefront(host, "amazon") && AmazonKeys.Contains(key)) return true;
        if (Matches(host, YoutubeHosts) && YoutubeKeys.Contains(key)) return true;
        if (Matches(host, TwitterHosts) && (key == "s" || key == "t")) return true;
        if (Matches(host, NotionHosts) && key == "pvs") return true;
        if ((host == "docs.google.com" || host == "drive.google.com") && key == "usp") return true;
        return false;
    }

    internal static bool Matches(string host, IEnumerable<string> domains) =>
        domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));

    internal static string? AmazonAsin(string host, string path)
    {
        if (!IsStorefront(host, "amazon")) return null;
        var segments = Text.SplitOmitEmpty(path, '/');
        for (int i = 0; i + 1 < segments.Count; i++)
        {
            var s = segments[i];
            bool marker = s == "dp"
                || (s == "product" && i > 0 && segments[i - 1] == "gp")
                || (s == "d" && i > 1 && segments[i - 1] == "aw" && segments[i - 2] == "gp");
            var candidate = segments[i + 1];
            if (marker && Text.Count(candidate) == 10 && candidate.All(c => char.IsAscii(c) && Text.IsAsciiLetterOrDigit(c)))
                return candidate.ToUpperInvariant();
        }
        return null;
    }
}
