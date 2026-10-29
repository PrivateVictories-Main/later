import Foundation

public enum PageRules {
    /// General or specific, and which kind, by rules alone: corrections, then site rules, then structure. A page is kept
    /// unless it is a hard skip (see `Structure.isHardSkip`) or a search; home feeds are kept only when `savesFeedHomes`.
    /// `url` must already be cleaned by `LinkCleaner`.
    public static func judge(_ url: URL, title: String, corrections: [String: Correction] = [:],
                             savesFeedHomes: Bool = false) -> Verdict {
        let host = (url.host ?? "").lowercased()
        let site = host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
        let urlPath = url.path(percentEncoded: false).split(separator: "/").map(String.init)
        var path = urlPath
        var queryKeys = Set((URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []).map { $0.name.lowercased() })
        // Page types start here; a hash route's types get a "#" so they never collide with real paths.
        var base = site
        // Split the still-encoded route first, so an encoded "?" or "&" inside a value never becomes a delimiter;
        // the route marker is checked after decoding, so "#%2F" and "#%21/" count like "#/" and "#!/".
        if let fragment = url.fragment(percentEncoded: true),
           case let halves = fragment.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false),
           case let route = String(halves[0]).removingPercentEncoding ?? String(halves[0]),
           route.hasPrefix("/") || route.hasPrefix("!") {
            path = route.dropFirst(route.hasPrefix("!") ? 1 : 0).split(separator: "/").map(String.init)
            queryKeys = Set(halves.dropFirst().flatMap { $0.split(separator: "&") }.map {
                let key = String($0.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)[0])
                return (key.removingPercentEncoding ?? key).lowercased()
            })
            let root = Structure.pageType(site: site, path: urlPath)
            base = (root.hasSuffix("/") ? root : root + "/") + "#"
        }

        var kind = SiteRules.kind(for: host)
        var decided: Bool?
        var pageType: String?
        for rule in SiteRules.all where rule.applies(to: host) {
            if !rule.hosts.isEmpty { kind = rule.kind }
            if let p = rule.general.first(where: { $0.matches(path, queryKeys: queryKeys) }) {
                decided = false
                pageType = base + p.text
                kind = rule.kind
                break
            }
            if let p = rule.feeds.first(where: { $0.matches(path, queryKeys: queryKeys) }) {
                decided = savesFeedHomes
                pageType = base + p.text
                kind = rule.kind
                break
            }
            if let p = rule.specific.first(where: { $0.matches(path, queryKeys: queryKeys) }) {
                decided = true
                pageType = base + p.text
                kind = rule.kind
                break
            }
        }

        var type = pageType ?? Structure.pageType(site: base, path: path)
        var isSpecific = decided ?? true
        if decided == nil {
            let looksLikeContent = path.contains(where: { Structure.isIDLike($0) || Structure.isSlug($0) })
            if !queryKeys.isDisjoint(with: Structure.searchKeys) {
                isSpecific = false   // a page of search results, whichever site it is on
            } else if looksLikeContent {
                // An empty title means the page is still loading: it is judged again when the title arrives.
                if title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { isSpecific = false }
                // A *.github.io page with a long slug is a post, whatever the ".io" says.
                if kind == .other || kind == .tools,
                   Structure.hasDate(path) || host.hasPrefix("blog.") || (host.hasSuffix(".github.io") && path.contains(where: Structure.isSlug)) {
                    kind = .reading
                }
            } else if path.contains(where: { Structure.cartWords.contains($0.lowercased()) }) {
                if kind == .other || kind == .tools { kind = .shopping }
            } else {
                // A site's home or a plain page like /careers or /login: one page type per host ("host/~"), so a single
                // correction covers them all. A host nobody has a rule for is a tool or a site the user follows.
                type = base + "/~"
                if kind == .other || kind == .tools { kind = .tools }
            }
        }
        // A PDF with a real name is something to read.
        if isSpecific, path.last?.lowercased().hasSuffix(".pdf") == true, kind == .other || kind == .tools { kind = .reading }
        if Structure.isHardSkip(site: site, port: url.port, path: urlPath + (path == urlPath ? [] : path), title: title) {
            isSpecific = false
        }
        return apply(corrections, to: Verdict(isSpecific: isSpecific, kind: kind, pageType: type))
    }

    /// The verdict with the user's correction for its page type, if they made one.
    public static func apply(_ corrections: [String: Correction], to verdict: Verdict) -> Verdict {
        guard let correction = corrections[verdict.pageType] else { return verdict }
        var verdict = verdict
        if let s = correction.isSpecific { verdict.isSpecific = s }
        if let k = correction.kind { verdict.kind = k }
        return verdict
    }
}

/// What a page is made of, for sites without a rule of their own, and the pages never kept.
enum Structure {
    /// Pages that are in the way: sign-in redirects, one-time links, confirmations and errors. A sign-in or sign-up page
    /// itself is kept; these words only ever show up on the way through. Matched as whole path sections.
    static let oneTimeWords: Set<String> = [
        "oauth", "oauth2", "authorize", "callback", "logout", "signout", "log-out", "sign-out", "redirect", "unsubscribe",
        "error", "success", "thank-you", "thankyou", "verify", "verify-email", "consent",
    ]
    /// Words of a one-time flow whose link carries its secret in the path: /reset-password/<token>, /invite/<code>.
    /// Matched as whole pieces of a section split at "-" and "_". The flow page without a token (/reset) is kept.
    static let secretFlowWords: Set<String> = [
        "reset", "invite", "invites", "invitation", "magic", "accept", "token", "confirm", "confirmation", "activate",
        "activation", "claim", "redeem", "join",
    ]
    /// A path that starts with one of these is a mail, chat or notification inbox, or a page of search results.
    static let inboxWords: Set<String> = ["messages", "notifications", "inbox", "search"]
    static let cartWords: Set<String> = ["cart", "basket", "bag", "checkout"]
    /// Hosts that only exist to be passed through: captive-portal checks and link shorteners.
    static let passThroughHosts: [String] = [
        "captive.apple.com", "neverssl.com", "detectportal.firefox.com", "msftconnecttest.com", "connectivitycheck.gstatic.com",
        "bit.ly", "t.co", "tinyurl.com", "goo.gl", "ow.ly", "is.gd", "buff.ly", "lnkd.in", "rb.gy", "cutt.ly",
    ]
    /// Sign-in providers: a page there is a step on the way to somewhere else.
    static let identityHosts: Set<String> = ["accounts.google.com", "login.microsoftonline.com", "login.live.com", "appleid.apple.com"]
    static let identitySuffixes = [".onelogin.com", ".okta.com", ".auth0.com"]
    /// A host that starts with one of these exists only for signing in.
    static let authHostLabels: Set<String> = ["sso", "idp", "auth", "login", "signin", "accounts"]
    /// Mail and chat, where every page is an inbox.
    static let inboxHosts: [String] = [
        "mail.google.com", "outlook.live.com", "outlook.office.com", "outlook.office365.com", "outlook.com", "mail.yahoo.com",
        "mail.proton.me", "messenger.com", "web.whatsapp.com", "web.telegram.org", "app.slack.com", "discord.com",
        "teams.microsoft.com",
    ]
    /// A search engine's own front page.
    static let searchEngineHosts: [String] = [
        "google.*", "bing.com", "duckduckgo.com", "search.brave.com", "ecosia.org", "yahoo.com", "kagi.com", "startpage.com",
        "baidu.com", "yandex.com",
    ]
    /// Extensions of files that aren't pages, kept only when the file has a real name (three words or more).
    static let fileExtensions: Set<String> = [
        "pdf", "png", "jpg", "jpeg", "gif", "webp", "svg", "ico", "json", "txt", "xml", "csv", "zip", "js", "css", "mp3", "mp4", "mov",
    ]
    /// Titles of pages that are in the way: challenges and errors. A title is one of these only when it is short (five
    /// words or fewer) and starts with the phrase, so "The Forbidden City" and "How CAPTCHA works" are real pages.
    static let uselessTitles = [
        "403 forbidden", "404 not found", "404 page not found", "page not found", "not found", "forbidden", "access denied",
        "just a moment", "attention required", "verify you are human", "checking your browser", "502 bad gateway",
        "bad gateway", "503 service unavailable", "service unavailable", "privacy error", "captcha",
    ]
    /// Blank-page titles, useless only when they are the whole title: "Untitled UI" is a real page.
    static let blankTitles = ["new tab", "untitled", "redirecting"]
    static let searchKeys: Set<String> = [
        "q", "query", "search", "k", "keyword", "keywords", "search_query", "term", "s", "text", "searchterm", "find", "wd",
    ]

    /// Pages never kept, whatever else the rules say: local hosts, sign-in providers, captive portals and shorteners,
    /// inboxes, search results and a search engine's front page, one-time links, files, and challenge or error titles.
    static func isHardSkip(site: String, port: Int?, path: [String], title: String) -> Bool {
        if site.isEmpty || port != nil || isLocal(site) { return true }
        if passThroughHosts.contains(where: { SiteRules.hostMatches(site, $0) }) { return true }
        if identityHosts.contains(site) || identitySuffixes.contains(where: site.hasSuffix) { return true }
        if authHostLabels.contains(site.split(separator: ".").first.map(String.init) ?? "") { return true }
        if inboxHosts.contains(where: { SiteRules.hostMatches(site, $0) }) { return true }
        if path.isEmpty, searchEngineHosts.contains(where: { SiteRules.hostMatches(site, $0) }) { return true }
        if let first = path.first?.lowercased(), inboxWords.contains(first) { return true }
        if path.contains(where: { oneTimeWords.contains($0.lowercased()) }) || hasSecretPath(path) { return true }
        if isFile(path.last) { return true }
        return isUselessTitle(title)
    }

    /// A flow word followed (at any depth) by a long, random-looking section: a one-time link, never saved or kept.
    static func hasSecretPath(_ path: [String]) -> Bool {
        let flow = path.firstIndex { $0.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).contains { secretFlowWords.contains(String($0)) } }
        return flow.map { path[($0 + 1)...].contains(where: isSecretToken) } ?? false
    }

    /// shortcut: ASCII letters, digits, "-" and "_" only, and no all-digit codes; add dots (JWTs) or digit-only tokens if a site needs it.
    static func isSecretToken(_ s: String) -> Bool {
        if s.count == 36, s.split(separator: "-").map(\.count) == [8, 4, 4, 4, 12] { return true }
        guard s.count >= 12, s.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }),
              s.contains(where: \.isLetter), s.contains(where: \.isNumber) else { return false }
        // A lowercase section with separators is a slug ("team-handbook-2024"), not a token.
        return s.contains(where: \.isUppercase) || !s.contains(where: { $0 == "-" || $0 == "_" })
    }

    /// "report.pdf" or "data.json", but not "git-cheat-sheet-education.pdf" or "index.html".
    static func isFile(_ section: String?) -> Bool {
        guard let section, let dot = section.lastIndex(of: ".") else { return false }
        return fileExtensions.contains(section[section.index(after: dot)...].lowercased()) && !isSlug(section)
    }

    static func isUselessTitle(_ title: String) -> Bool {
        let words = title.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
        guard words.count <= 5 else { return false }
        return uselessTitles.contains { words.starts(with: $0.split(separator: " ").map(String.init)) }
            || blankTitles.contains { words.elementsEqual($0.split(separator: " ").map(String.init)) }
    }

    /// Localhost, home-network names and private addresses.
    static func isLocal(_ host: String) -> Bool {
        if !host.contains(".") || host.contains(":") { return true }   // "localhost", "nas", IPv6
        if [".localhost", ".local", ".test", ".internal", ".lan", ".home.arpa"].contains(where: host.hasSuffix) { return true }
        let octets = host.split(separator: ".").compactMap { Int($0) }
        guard octets.count == 4, octets.allSatisfy({ (0...255).contains($0) }) else { return false }
        return octets[0] == 10 || octets[0] == 127 || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 172 && (16...31).contains(octets[1])) || (octets[0] == 169 && octets[1] == 254)
    }

    static func isIDLike(_ s: String) -> Bool {
        var run = 0, longest = 0, letters = 0, digits = 0
        for c in s {
            if c.isASCII, c.isNumber {
                digits += 1
                run += 1
                longest = max(longest, run)
            } else {
                run = 0
                if c.isLetter { letters += 1 }
            }
        }
        if longest >= 6 { return true }
        if s.count == 36, s.split(separator: "-").map(\.count) == [8, 4, 4, 4, 12] { return true }
        return s.count >= 8 && letters >= 1 && digits >= 1
            && s.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-") }
    }

    static func isSlug(_ s: String) -> Bool {
        let words = s.split(whereSeparator: { $0 == "-" || $0 == "_" })
        return words.count >= 3 && words.allSatisfy { $0.contains(where: \.isLetter) }
    }

    static func hasDate(_ path: [String]) -> Bool {
        zip(path, path.dropFirst()).contains { year, month in
            year.count == 4 && (year.hasPrefix("19") || year.hasPrefix("20")) && year.allSatisfy(\.isNumber)
                && (1...2).contains(month.count) && month.allSatisfy(\.isNumber)
        }
    }

    /// Host and path shape only; query keys would split one kind of page into many.
    static func pageType(site: String, path: [String]) -> String {
        site + "/" + path.map { isIDLike($0) || isSlug($0) ? "*" : $0.lowercased() }.joined(separator: "/")
    }
}
