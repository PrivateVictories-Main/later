import Foundation

/// A path shape: "/dp/*", "/*/status/*", "/r/*/comments/**", "/watch?v".
/// `*` matches one segment, `**` matches the rest (zero or more), `ref=*` matches a segment by its prefix,
/// `?name` requires that query key.
public struct PathPattern: Sendable, ExpressibleByStringLiteral {
    public let text: String
    let segments: [String]
    let queryKey: String?

    public init(stringLiteral text: String) {
        self.text = text
        let halves = text.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        segments = halves[0].split(separator: "/").map { $0.lowercased() }
        queryKey = halves.count > 1 ? halves[1].lowercased() : nil
    }

    func matches(_ path: [String], queryKeys: Set<String>) -> Bool {
        if let queryKey, !queryKeys.contains(queryKey) { return false }
        for (i, segment) in segments.enumerated() {
            if segment == "**" { return true }
            guard i < path.count else { return false }
            let part = path[i].lowercased()
            if segment == "*" || segment == part || (segment.hasSuffix("*") && part.hasPrefix(segment.dropLast())) { continue }
            return false
        }
        return segments.count == path.count
    }

    /// The query key a pattern names ("/watch?v" → "v"), when the path alone matches.
    func identityKey(for path: [String]) -> String? {
        queryKey.flatMap { matches(path, queryKeys: [$0]) ? $0 : nil }
    }
}

/// Rules for one site. General patterns (searches, inboxes) are checked first, then feed homes (kept only when the user turns
/// that on), then specific ones; no match falls through to later rules and then to the structure rules, which keep a
/// page unless it is a hard skip. An empty `hosts` list applies to every host.
public struct SiteRule: Sendable {
    let hosts: [String]
    let kind: Kind
    let general: [PathPattern]
    var feeds: [PathPattern] = []
    let specific: [PathPattern]
    /// False keeps the rule off other subdomains (only the bare host and its www./m./mobile. forms),
    /// so a rule whose "/*" means a profile can't swallow pages on, say, education.github.com.
    var subdomains = true

    func applies(to host: String) -> Bool {
        hosts.isEmpty || hosts.contains { site in
            subdomains ? SiteRules.hostMatches(host, site) : ["", "www.", "m.", "mobile."].contains { host == $0 + site }
        }
    }
}

public enum SiteRules {
    /// "amazon.*" means any Amazon storefront; anything else is a domain suffix.
    static func hostMatches(_ host: String, _ pattern: String) -> Bool {
        if pattern.hasSuffix(".*") { return LinkCleaner.isStorefront(host, name: String(pattern.dropLast(2))) }
        return host == pattern || host.hasSuffix("." + pattern)
    }

    /// General: pages that are never kept (search results, inboxes). Feeds: a site's home feed, kept only when the user asks
    /// for feed home pages. Specific: pages with a shape worth a page type of its own. Everything else is kept anyway.
    static let all: [SiteRule] = [
        SiteRule(hosts: ["amazon.*"], kind: .shopping, general: ["/s", "/s/**"], feeds: ["/", "/ref=*"],
                 specific: ["/dp/*", "/dp/*/**", "/gp/product/*", "/gp/aw/d/*", "/*/dp/*", "/*/dp/*/**"]),
        SiteRule(hosts: ["ebay.*"], kind: .shopping, general: ["/sch/**"], specific: ["/itm/**"]),
        SiteRule(hosts: ["etsy.com"], kind: .shopping, general: ["/search"], specific: ["/listing/**"]),
        SiteRule(hosts: ["walmart.com"], kind: .shopping, general: ["/search"], specific: ["/ip/**"]),
        SiteRule(hosts: ["target.com"], kind: .shopping, general: ["/s", "/s/**"], specific: ["/p/**"]),
        SiteRule(hosts: ["bestbuy.com"], kind: .shopping, general: ["/site/searchpage.jsp"], specific: ["/site/*/*"]),
        SiteRule(hosts: ["x.com", "twitter.com"], kind: .posts, general: ["/search", "/notifications/**", "/messages/**"],
                 feeds: ["/", "/home"], specific: ["/*/status/*", "/*/status/*/**", "/i/web/status/*", "/*"], subdomains: false),
        SiteRule(hosts: ["reddit.com"], kind: .posts, general: ["/search"], feeds: ["/"],
                 specific: ["/r/*/comments/**", "/comments/*", "/comments/*/**", "/r/*/s/*", "/r/*", "/r/*/hot", "/r/*/new",
                            "/r/*/top", "/r/*/rising", "/user/*"]),
        SiteRule(hosts: ["bsky.app"], kind: .posts, general: ["/search", "/notifications"],
                 specific: ["/profile/*/post/*", "/profile/*"]),
        SiteRule(hosts: ["threads.net", "threads.com"], kind: .posts, general: ["/search", "/activity"],
                 specific: ["/*/post/*", "/*"], subdomains: false),
        SiteRule(hosts: ["instagram.com"], kind: .posts, general: ["/direct/**"], feeds: ["/"],
                 specific: ["/p/*", "/reel/*", "/*/p/*", "/*/reel/*", "/*"], subdomains: false),
        SiteRule(hosts: ["facebook.com", "fb.com"], kind: .posts, general: ["/messages/**", "/notifications/**", "/search/**"],
                 feeds: ["/", "/home.php"], specific: []),
        SiteRule(hosts: ["linkedin.com"], kind: .posts, general: ["/messaging/**", "/notifications/**", "/search/**"],
                 feeds: ["/feed"], specific: ["/posts/*", "/feed/update/*", "/jobs/view/*", "/pulse/*"]),
        SiteRule(hosts: ["news.ycombinator.com"], kind: .posts, general: [], specific: ["/item?id"]),
        SiteRule(hosts: ["youtube.com"], kind: .videos, general: ["/results"], feeds: ["/", "/feed/**"],
                 specific: ["/watch?v", "/shorts/*", "/live/*", "/playlist?list"]),
        SiteRule(hosts: ["youtu.be"], kind: .videos, general: [], specific: ["/*"]),
        SiteRule(hosts: ["vimeo.com"], kind: .videos, general: ["/search"], specific: []),
        SiteRule(hosts: ["twitch.tv"], kind: .videos, general: [], specific: ["/videos/*", "/*/clip/*"]),
        SiteRule(hosts: ["tiktok.com"], kind: .videos, general: ["/search"], feeds: ["/", "/foryou", "/following"],
                 specific: ["/*/video/*", "/*"], subdomains: false),
        // A user, org or project page is an idea; GitHub's own pages (pricing, features, explore) are kept too.
        SiteRule(hosts: ["github.com"], kind: .tools, general: ["/search", "/notifications"],
                 specific: ["/*/*/issues/*", "/*/*/pull/*", "/*/*/pull/*/**", "/*/*/discussions/*", "/*/*/blob/**",
                            "/*/*/tree/**", "/*/*/releases/**", "/*/*/commit/*", "/*/*", "/*"], subdomains: false),
        SiteRule(hosts: ["gist.github.com"], kind: .other, general: ["/search"],
                 specific: ["/*/*", "/*/*/**", "/*"], subdomains: false),
        SiteRule(hosts: ["wikipedia.org"], kind: .reading, general: ["/wiki/Special:Search"], specific: ["/wiki/*"]),
        SiteRule(hosts: ["medium.com"], kind: .reading, general: ["/search"], specific: ["/p/*", "/*/*"]),
        SiteRule(hosts: ["substack.com"], kind: .reading, general: ["/inbox", "/search"], specific: ["/p/*", "/home/post/*", "/*/p/*"]),
        // A framework's overview is a table of contents; a symbol or article under it is a page to read.
        SiteRule(hosts: ["developer.apple.com"], kind: .reading, general: [], specific: ["/documentation/*/**"]),
        SiteRule(hosts: ["news.google.com"], kind: .reading, general: ["/search"], feeds: ["/", "/home", "/topstories"],
                 specific: []),
        SiteRule(hosts: ["docs.google.com"], kind: .other, general: [],
                 specific: ["/document/d/**", "/spreadsheets/d/**", "/presentation/d/**", "/forms/d/**"]),
        SiteRule(hosts: ["drive.google.com"], kind: .other, general: [], specific: ["/file/d/**"]),
        SiteRule(hosts: ["classroom.google.com"], kind: .school, general: [],
                 specific: ["/c/*", "/u/*/c/*", "/c/*/a/*/**", "/u/*/c/*/a/*/**", "/c/*/m/*/**", "/u/*/c/*/m/*/**",
                            "/c/*/p/*/**", "/u/*/c/*/p/*/**", "/c/*/sa/*/**"]),
        // Learning platforms on a school's own host: Canvas, Blackboard, Moodle, Brightspace, Gradescope. A course's
        // home is a class, so it is kept like any assignment under it.
        SiteRule(hosts: [], kind: .school,
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
    ]

    /// The query key that names a page ("v" for "/watch?v=…") on this host and path, if a site rule says so: the other
    /// keys (list, index, time) only describe how it was reached.
    static func identityKey(host: String, path: [String]) -> String? {
        for rule in all where rule.applies(to: host) {
            if let key = rule.specific.lazy.compactMap({ $0.identityKey(for: path) }).first { return key }
        }
        return nil
    }

    /// Which kind a host's pages are, when no site rule names one.
    static let hostKinds: [(Kind, [String])] = [
        (.shopping, ["amazon.*", "ebay.*", "etsy.com", "walmart.com", "target.com", "bestbuy.com", "aliexpress.com",
                     "temu.com", "costco.com", "newegg.com", "bhphotovideo.com", "homedepot.com", "lowes.com",
                     "ikea.com", "wayfair.com", "shein.com", "nike.com", "zara.com"]),
        (.posts, ["x.com", "twitter.com", "reddit.com", "bsky.app", "threads.net", "threads.com", "instagram.com",
                  "facebook.com", "linkedin.com", "news.ycombinator.com", "mastodon.social", "tumblr.com", "pinterest.com"]),
        (.videos, ["youtube.com", "youtu.be", "vimeo.com", "twitch.tv", "tiktok.com", "dailymotion.com", "netflix.com"]),
        (.school, ["edu", "instructure.com", "blackboard.com", "classroom.google.com", "brightspace.com", "d2l.com",
                   "schoology.com", "moodlecloud.com", "quizlet.com", "khanacademy.org", "coursera.org", "edx.org",
                   "chegg.com", "gradescope.com", "piazza.com", "edstem.org"]),
        (.reading, ["wikipedia.org", "medium.com", "substack.com", "nytimes.com", "theverge.com", "arstechnica.com",
                    "bbc.com", "bbc.co.uk", "cnn.com", "theguardian.com", "wsj.com", "bloomberg.com", "techcrunch.com",
                    "wired.com", "washingtonpost.com", "reuters.com", "apnews.com", "npr.org", "theatlantic.com",
                    "economist.com", "dev.to", "stackoverflow.com", "developer.apple.com", "developer.mozilla.org"]),
        // Last, so the news and reference hosts above keep their own kinds.
        (.tools, ["github.com", "gitlab.com", "npmjs.com", "pypi.org", "huggingface.co", "producthunt.com",
                  "dev", "app", "io", "ai", "sh", "so"]),
    ]
    static let toolsPrefixes = ["docs.", "developer.", "api."]

    static func kind(for host: String) -> Kind {
        // A school's learning platform on its own domain (blackboard.school.org, canvas.school.org).
        if host.split(separator: ".").contains(where: { $0.hasPrefix("blackboard") || $0.hasPrefix("canvas") }) { return .school }
        return hostKinds.first { _, hosts in hosts.contains { hostMatches(host, $0) } }?.0
            ?? (toolsPrefixes.contains { host.hasPrefix($0) } ? .tools : .other)
    }
}
