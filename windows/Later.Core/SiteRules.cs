namespace Later.Core;

/// Rules for one site. General patterns (searches, inboxes) are checked first, then feed homes (kept only when the user turns
/// that on), then specific ones; no match falls through to later rules and then to the structure rules, which keep a
/// page unless it is a hard skip. An empty `Hosts` list applies to every host. `Subdomains` false keeps the rule off other
/// subdomains (only the bare host and its www./m./mobile. forms), so a rule whose "/*" means a profile can't swallow
/// pages on, say, education.github.com.
public sealed record SiteRule(string[] Hosts, Kind Kind, PathPattern[] General, PathPattern[] Feeds, PathPattern[] Specific, bool Subdomains = true)
{
    static readonly string[] BarePrefixes = ["", "www.", "m.", "mobile."];

    internal bool AppliesTo(string host) =>
        Hosts.Length == 0 || Hosts.Any(site =>
            Subdomains ? SiteRules.HostMatches(host, site) : BarePrefixes.Any(p => host == p + site));
}

public static class SiteRules
{
    /// "amazon.*" means any Amazon storefront; anything else is a domain suffix.
    internal static bool HostMatches(string host, string pattern)
    {
        if (pattern.EndsWith(".*", StringComparison.Ordinal)) return LinkCleaner.IsStorefront(host, pattern[..^2]);
        return host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal);
    }

    static SiteRule Rule(string[] hosts, Kind kind, PathPattern[] general, PathPattern[] specific, PathPattern[]? feeds = null, bool subdomains = true) =>
        new(hosts, kind, general, feeds ?? [], specific, subdomains);

    /// General: pages that are never kept (search results, inboxes). Feeds: a site's home feed, kept only when the user asks
    /// for feed home pages. Specific: pages with a shape worth a page type of its own. Everything else is kept anyway.
    internal static readonly SiteRule[] All =
    [
        Rule(["amazon.*"], Kind.Shopping, general: ["/s", "/s/**"], feeds: ["/", "/ref=*"],
             specific: ["/dp/*", "/dp/*/**", "/gp/product/*", "/gp/aw/d/*", "/*/dp/*", "/*/dp/*/**"]),
        Rule(["ebay.*"], Kind.Shopping, general: ["/sch/**"], specific: ["/itm/**"]),
        Rule(["etsy.com"], Kind.Shopping, general: ["/search"], specific: ["/listing/**"]),
        Rule(["walmart.com"], Kind.Shopping, general: ["/search"], specific: ["/ip/**"]),
        Rule(["target.com"], Kind.Shopping, general: ["/s", "/s/**"], specific: ["/p/**"]),
        Rule(["bestbuy.com"], Kind.Shopping, general: ["/site/searchpage.jsp"], specific: ["/site/*/*"]),
        Rule(["x.com", "twitter.com"], Kind.Posts, general: ["/search", "/notifications/**", "/messages/**"],
             feeds: ["/", "/home"], specific: ["/*/status/*", "/*/status/*/**", "/i/web/status/*", "/*"], subdomains: false),
        Rule(["reddit.com"], Kind.Posts, general: ["/search"], feeds: ["/"],
             specific: ["/r/*/comments/**", "/comments/*", "/comments/*/**", "/r/*/s/*", "/r/*", "/r/*/hot", "/r/*/new",
                        "/r/*/top", "/r/*/rising", "/user/*"]),
        Rule(["bsky.app"], Kind.Posts, general: ["/search", "/notifications"],
             specific: ["/profile/*/post/*", "/profile/*"]),
        Rule(["threads.net", "threads.com"], Kind.Posts, general: ["/search", "/activity"],
             specific: ["/*/post/*", "/*"], subdomains: false),
        Rule(["instagram.com"], Kind.Posts, general: ["/direct/**"], feeds: ["/"],
             specific: ["/p/*", "/reel/*", "/*/p/*", "/*/reel/*", "/*"], subdomains: false),
        Rule(["facebook.com", "fb.com"], Kind.Posts, general: ["/messages/**", "/notifications/**", "/search/**"],
             feeds: ["/", "/home.php"], specific: []),
        Rule(["linkedin.com"], Kind.Posts, general: ["/messaging/**", "/notifications/**", "/search/**"],
             feeds: ["/feed"], specific: ["/posts/*", "/feed/update/*", "/jobs/view/*", "/pulse/*"]),
        Rule(["news.ycombinator.com"], Kind.Posts, general: [], specific: ["/item?id"]),
        Rule(["youtube.com"], Kind.Videos, general: ["/results"], feeds: ["/", "/feed/**"],
             specific: ["/watch?v", "/shorts/*", "/live/*", "/playlist?list"]),
        Rule(["youtu.be"], Kind.Videos, general: [], specific: ["/*"]),
        Rule(["vimeo.com"], Kind.Videos, general: ["/search"], specific: []),
        Rule(["twitch.tv"], Kind.Videos, general: [], specific: ["/videos/*", "/*/clip/*"]),
        Rule(["tiktok.com"], Kind.Videos, general: ["/search"], feeds: ["/", "/foryou", "/following"],
             specific: ["/*/video/*", "/*"], subdomains: false),
        // A user, org or project page is an idea; GitHub's own pages (pricing, features, explore) are kept too.
        Rule(["github.com"], Kind.Tools, general: ["/search", "/notifications"],
             specific: ["/*/*/issues/*", "/*/*/pull/*", "/*/*/pull/*/**", "/*/*/discussions/*", "/*/*/blob/**",
                        "/*/*/tree/**", "/*/*/releases/**", "/*/*/commit/*", "/*/*", "/*"], subdomains: false),
        Rule(["gist.github.com"], Kind.Other, general: ["/search"],
             specific: ["/*/*", "/*/*/**", "/*"], subdomains: false),
        Rule(["wikipedia.org"], Kind.Reading, general: ["/wiki/Special:Search"], specific: ["/wiki/*"]),
        Rule(["medium.com"], Kind.Reading, general: ["/search"], specific: ["/p/*", "/*/*"]),
        Rule(["substack.com"], Kind.Reading, general: ["/inbox", "/search"], specific: ["/p/*", "/home/post/*", "/*/p/*"]),
        // A framework's overview is a table of contents; a symbol or article under it is a page to read.
        Rule(["developer.apple.com"], Kind.Reading, general: [], specific: ["/documentation/*/**"]),
        Rule(["news.google.com"], Kind.Reading, general: ["/search"], feeds: ["/", "/home", "/topstories"],
             specific: []),
        Rule(["docs.google.com"], Kind.Other, general: [],
             specific: ["/document/d/**", "/spreadsheets/d/**", "/presentation/d/**", "/forms/d/**"]),
        Rule(["drive.google.com"], Kind.Other, general: [], specific: ["/file/d/**"]),
        Rule(["classroom.google.com"], Kind.School, general: [],
             specific: ["/c/*", "/u/*/c/*", "/c/*/a/*/**", "/u/*/c/*/a/*/**", "/c/*/m/*/**", "/u/*/c/*/m/*/**",
                        "/c/*/p/*/**", "/u/*/c/*/p/*/**", "/c/*/sa/*/**"]),
        // Learning platforms on a school's own host: Canvas, Blackboard, Moodle, Brightspace, Gradescope. A course's
        // home is a class, so it is kept like any assignment under it.
        Rule([], Kind.School,
             general: ["/ultra/messages", "/conversations"],
             specific: ["/courses", "/courses/*", "/ultra/stream", "/ultra/course", "/ultra/calendar",
                        "/ultra/institution-page", "/ultra/grades", "/ultra/courses/*/outline",
                        "/ultra/courses/*/cl/outline", "/course/view.php?id", "/d2l/home", "/d2l/home/*",
                        "/courses/*/assignments/*", "/courses/*/quizzes/*", "/courses/*/discussion_topics/*",
                        "/courses/*/pages/*", "/courses/*/files/*", "/courses/*/modules/items/*",
                        "/ultra/courses/*/outline/**", "/ultra/courses/*/cl/outline/**", "/webapps/assignment/**",
                        "/webapps/blackboard/content/**", "/mod/assign/view.php?id", "/mod/quiz/view.php?id",
                        "/mod/page/view.php?id", "/mod/resource/view.php?id", "/mod/forum/discuss.php?d",
                        "/d2l/le/content/*/viewContent/*/View", "/d2l/lms/dropbox/**"]),
    ];

    /// The query key that names a page ("v" for "/watch?v=…") on this host and path, if a site rule says so: the other
    /// keys (list, index, time) only describe how it was reached.
    internal static string? IdentityKey(string host, IReadOnlyList<string> path)
    {
        foreach (var rule in All.Where(r => r.AppliesTo(host)))
            foreach (var pattern in rule.Specific)
                if (pattern.IdentityKey(path) is { } key) return key;
        return null;
    }

    /// Which kind a host's pages are, when no site rule names one.
    static readonly (Kind Kind, string[] Hosts)[] HostKinds =
    [
        (Kind.Shopping, ["amazon.*", "ebay.*", "etsy.com", "walmart.com", "target.com", "bestbuy.com", "aliexpress.com",
                         "temu.com", "costco.com", "newegg.com", "bhphotovideo.com", "homedepot.com", "lowes.com",
                         "ikea.com", "wayfair.com", "shein.com", "nike.com", "zara.com"]),
        (Kind.Posts, ["x.com", "twitter.com", "reddit.com", "bsky.app", "threads.net", "threads.com", "instagram.com",
                      "facebook.com", "linkedin.com", "news.ycombinator.com", "mastodon.social", "tumblr.com", "pinterest.com"]),
        (Kind.Videos, ["youtube.com", "youtu.be", "vimeo.com", "twitch.tv", "tiktok.com", "dailymotion.com", "netflix.com"]),
        (Kind.School, ["edu", "instructure.com", "blackboard.com", "classroom.google.com", "brightspace.com", "d2l.com",
                       "schoology.com", "moodlecloud.com", "quizlet.com", "khanacademy.org", "coursera.org", "edx.org",
                       "chegg.com", "gradescope.com", "piazza.com", "edstem.org"]),
        (Kind.Reading, ["wikipedia.org", "medium.com", "substack.com", "nytimes.com", "theverge.com", "arstechnica.com",
                        "bbc.com", "bbc.co.uk", "cnn.com", "theguardian.com", "wsj.com", "bloomberg.com", "techcrunch.com",
                        "wired.com", "washingtonpost.com", "reuters.com", "apnews.com", "npr.org", "theatlantic.com",
                        "economist.com", "dev.to", "stackoverflow.com", "developer.apple.com", "developer.mozilla.org"]),
        // Last, so the news and reference hosts above keep their own kinds.
        (Kind.Tools, ["github.com", "gitlab.com", "npmjs.com", "pypi.org", "huggingface.co", "producthunt.com",
                      "dev", "app", "io", "ai", "sh", "so"]),
    ];
    static readonly string[] ToolsPrefixes = ["docs.", "developer.", "api."];

    internal static Kind KindFor(string host)
    {
        // A school's learning platform on its own domain (blackboard.school.org, canvas.school.org).
        if (Text.SplitOmitEmpty(host, '.').Any(l => l.StartsWith("blackboard", StringComparison.Ordinal) || l.StartsWith("canvas", StringComparison.Ordinal)))
            return Kind.School;
        foreach (var (kind, hosts) in HostKinds)
            if (hosts.Any(h => HostMatches(host, h))) return kind;
        return ToolsPrefixes.Any(p => host.StartsWith(p, StringComparison.Ordinal)) ? Kind.Tools : Kind.Other;
    }
}
