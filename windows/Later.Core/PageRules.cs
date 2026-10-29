namespace Later.Core;

public static class PageRules
{
    static readonly IReadOnlyDictionary<string, Correction> NoCorrections = new Dictionary<string, Correction>();

    /// General or specific, and which kind, by rules alone: corrections, then site rules, then structure. A page is kept
    /// unless it is a hard skip (see `Structure.IsHardSkip`) or a search; home feeds are kept only when `savesFeedHomes`.
    /// `url` must already be cleaned by `LinkCleaner`.
    public static Verdict Judge(ParsedUrl url, string title, IReadOnlyDictionary<string, Correction>? corrections = null,
                                bool savesFeedHomes = false)
    {
        var host = url.HostOrEmpty.ToLowerInvariant();
        var site = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        var urlPath = Text.SplitOmitEmpty(url.DecodedPath, '/');
        var path = urlPath;
        HashSet<string> queryKeys = url.EncodedQueryItems.Select(i => QueryKey(i.Name)).ToHashSet();
        // Page types start here; a hash route's types get a "#" so they never collide with real paths.
        var baseType = site;
        // Split the still-encoded route first, so an encoded "?" or "&" inside a value never becomes a delimiter;
        // the route marker is checked after decoding, so "#%2F" and "#%21/" count like "#/" and "#!/".
        if (url.EncodedFragment is { } fragment)
        {
            var halves = fragment.Split('?', 2);
            var route = UrlText.PercentDecode(halves[0]) ?? halves[0];
            if (route.StartsWith('/') || route.StartsWith('!'))
            {
                path = Text.SplitOmitEmpty(route.StartsWith('!') ? route[1..] : route, '/');
                queryKeys = halves.Skip(1).SelectMany(h => h.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    .Select(p => QueryKey(p.Split('=', 2)[0])).ToHashSet();
                var root = Structure.PageType(site, urlPath);
                baseType = (root.EndsWith('/') ? root : root + "/") + "#";
            }
        }

        var kind = SiteRules.KindFor(host);
        bool? decided = null;
        string? pageType = null;
        var isPlace = false;   // a site's home, dashboard, feed or plain section page: somewhere you go, not a thing
        foreach (var rule in SiteRules.All)
        {
            if (!rule.AppliesTo(host)) continue;
            if (rule.Hosts.Length > 0) kind = rule.Kind;
            if (rule.General.FirstOrDefault(p => p.Matches(path, queryKeys)) is { } general)
            {
                decided = false;
                isPlace = true;
                pageType = baseType + general.Text;
                kind = rule.Kind;
                break;
            }
            if (rule.Feeds.FirstOrDefault(p => p.Matches(path, queryKeys)) is { } feed)
            {
                decided = savesFeedHomes;
                isPlace = true;
                pageType = baseType + feed.Text;
                kind = rule.Kind;
                break;
            }
            if (rule.Specific.FirstOrDefault(p => p.Matches(path, queryKeys)) is { } specific)
            {
                decided = true;
                pageType = baseType + specific.Text;
                kind = rule.Kind;
                break;
            }
        }

        var type = pageType ?? Structure.PageType(baseType, path);
        var isSpecific = decided ?? true;
        if (decided is null)
        {
            bool looksLikeContent = path.Any(p => Structure.IsIDLike(p) || Structure.IsSlug(p));
            if (queryKeys.Overlaps(Structure.SearchKeys))
            {
                isSpecific = false;   // a page of search results, whichever site it is on
            }
            else if (looksLikeContent)
            {
                // An empty title means the page is still loading: it is judged again when the title arrives.
                if (string.IsNullOrWhiteSpace(title)) isSpecific = false;
                // A *.github.io page with a long slug is a post, whatever the ".io" says.
                if ((kind == Kind.Other || kind == Kind.Tools)
                    && (Structure.HasDate(path) || host.StartsWith("blog.", StringComparison.Ordinal)
                        || (host.EndsWith(".github.io", StringComparison.Ordinal) && path.Any(Structure.IsSlug))))
                    kind = Kind.Reading;
            }
            else if (path.Any(p => Structure.CartWords.Contains(p.ToLowerInvariant())))
            {
                if (kind == Kind.Other || kind == Kind.Tools) kind = Kind.Shopping;
            }
            else
            {
                // A site's home or a plain page like /careers or /login: one page type per host ("host/~"), so a single
                // correction covers them all. A host nobody has a rule for is a tool or a site the user follows.
                type = baseType + "/~";
                // Only a home or a single-section page is somewhere you go; a deeper page, a file or a page named by a
                // query value (/blog/hello-world, /article.php?id=7) is a thing, kept even on a site you visit all the time.
                isPlace = path.Count <= 1 && queryKeys.Count == 0 && !(path.Count > 0 && path[^1].Contains('.', StringComparison.Ordinal));
                if (kind == Kind.Other || kind == Kind.Tools) kind = Kind.Tools;
            }
        }
        // A PDF with a real name is something to read.
        if (isSpecific && path.Count > 0 && path[^1].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && (kind == Kind.Other || kind == Kind.Tools))
            kind = Kind.Reading;
        bool hardSkip = Structure.IsHardSkip(site, url.Port, urlPath, title, path.SequenceEqual(urlPath) ? null : path);
        if (hardSkip) isSpecific = false;
        return Apply(corrections ?? NoCorrections, new Verdict(isSpecific, kind, type, isPlace, hardSkip));
    }

    static string QueryKey(string encodedName) => (UrlText.PercentDecode(encodedName) ?? encodedName).ToLowerInvariant();

    /// The verdict with the user's correction for its page type, if they made one. A correction can turn a page off,
    /// never bring back a hard skip.
    public static Verdict Apply(IReadOnlyDictionary<string, Correction> corrections, Verdict verdict)
    {
        if (!corrections.TryGetValue(verdict.PageType, out var correction)) return verdict;
        return verdict with
        {
            IsSpecific = (correction.IsSpecific ?? verdict.IsSpecific) && !verdict.IsHardSkip,
            Kind = correction.Kind ?? verdict.Kind,
        };
    }
}
