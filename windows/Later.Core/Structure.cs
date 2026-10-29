using System.Globalization;
using System.Text;

namespace Later.Core;

/// What a page is made of, for sites without a rule of their own, and the pages never kept.
internal static class Structure
{
    /// Pages that are in the way: sign-in redirects, one-time links, confirmations and errors. A sign-in or sign-up page
    /// itself is kept; these words only ever show up on the way through. Matched as whole path sections at any depth,
    /// except in documentation (a docs host, or see `DocsSections`), where they are a page's own name (/documentation/swift/error).
    static readonly HashSet<string> OneTimeWords =
    [
        "oauth", "oauth2", "authorize", "callback", "logout", "signout", "log-out", "sign-out", "redirect", "unsubscribe",
        "error", "success", "thank-you", "thankyou", "verify", "verify-email", "consent",
    ];
    /// Words of a one-time flow whose link carries its secret in the path: /reset-password/<token>, /invite/<code>.
    /// Matched as whole pieces of a section split at "-" and "_". The flow page without a token (/reset) is kept.
    static readonly HashSet<string> SecretFlowWords =
    [
        "reset", "invite", "invites", "invitation", "magic", "accept", "token", "confirm", "confirmation", "activate",
        "activation", "claim", "redeem", "join",
    ];
    /// A path that starts with one of these is a mail, chat or notification inbox, or a page of search results.
    static readonly HashSet<string> InboxWords = ["messages", "notifications", "inbox", "search"];
    internal static readonly HashSet<string> CartWords = ["cart", "basket", "bag", "checkout"];
    /// Hosts that only exist to be passed through: captive-portal checks and link shorteners.
    static readonly string[] PassThroughHosts =
    [
        "captive.apple.com", "neverssl.com", "detectportal.firefox.com", "msftconnecttest.com", "connectivitycheck.gstatic.com",
        "bit.ly", "t.co", "tinyurl.com", "goo.gl", "ow.ly", "is.gd", "buff.ly", "lnkd.in", "rb.gy", "cutt.ly",
    ];
    /// Sign-in providers: a page there is a step on the way to somewhere else.
    static readonly HashSet<string> IdentityHosts = ["accounts.google.com", "login.microsoftonline.com", "login.live.com", "appleid.apple.com"];
    static readonly string[] IdentitySuffixes = [".onelogin.com", ".okta.com", ".auth0.com"];
    /// Hosts of those providers that are for reading, not signing in: developer.okta.com, support.okta.com.
    static readonly HashSet<string> IdentityDocLabels = ["developer", "developers", "docs", "support", "help", "blog", "www"];
    /// A host that starts with one of these exists only for signing in.
    static readonly HashSet<string> AuthHostLabels = ["sso", "idp", "auth", "login", "signin", "accounts"];
    /// Mail and chat, where every page is an inbox.
    static readonly string[] InboxHosts =
    [
        "mail.google.com", "outlook.live.com", "outlook.office.com", "outlook.office365.com", "outlook.com", "mail.yahoo.com",
        "mail.proton.me", "messenger.com", "web.whatsapp.com", "web.telegram.org", "app.slack.com", "teams.microsoft.com",
    ];
    /// Sites that are chat in one place and a website in the rest: only the chat paths are inboxes.
    /// They cover the site's subdomains too (ptb.discord.com), except the ones for reading (support.discord.com).
    static readonly (string Host, HashSet<string> Paths)[] ChatAppPaths = [("discord.com", new HashSet<string> { "channels", "app" })];
    static readonly HashSet<string> ChatDocLabels = ["support", "status", "blog", "developer", "developers", "docs", "help", "dis"];
    /// A first path section that marks documentation or an article, where a flow's word is a page's own name
    /// (/documentation/swift/error, /wiki/Consent).
    static readonly HashSet<string> DocsSections =
        ["docs", "documentation", "reference", "wiki", "manual", "guide", "learn", "api-reference"];
    /// Separators between a page's own title and its site's name.
    static readonly string[] TitleSeparators = [" | ", " – ", " — ", " - ", " · "];
    /// A search engine's own front page.
    static readonly string[] SearchEngineHosts =
    [
        "google.*", "bing.com", "duckduckgo.com", "search.brave.com", "ecosia.org", "yahoo.com", "kagi.com", "startpage.com",
        "baidu.com", "yandex.com",
    ];
    /// Extensions of files that aren't pages, kept only when the file has a real name (three words or more).
    static readonly HashSet<string> FileExtensions =
    [
        "pdf", "png", "jpg", "jpeg", "gif", "webp", "svg", "ico", "json", "txt", "xml", "csv", "zip", "js", "css", "mp3", "mp4", "mov",
    ];
    /// Titles of pages that are in the way: challenges and errors. A title is one of these only when it is short (five
    /// words or fewer) and starts with the phrase, so "The Forbidden City" and "How CAPTCHA works" are real pages. A
    /// one-word phrase must be the whole title: "Forbidden Planet - Wikipedia" is a real page.
    static readonly string[][] UselessTitles = new[]
    {
        "403 forbidden", "404 not found", "404 page not found", "page not found", "not found", "forbidden", "access denied",
        "just a moment", "attention required", "verify you are human", "checking your browser", "502 bad gateway",
        "bad gateway", "503 service unavailable", "service unavailable", "privacy error", "captcha",
    }.Select(p => p.Split(' ')).ToArray();
    /// Blank-page titles, useless only when they are the whole title: "Untitled UI" is a real page.
    static readonly string[][] BlankTitles = new[] { "new tab", "untitled", "redirecting" }.Select(p => p.Split(' ')).ToArray();
    internal static readonly HashSet<string> SearchKeys =
    [
        "q", "query", "search", "k", "keyword", "keywords", "search_query", "term", "s", "text", "searchterm", "find", "wd",
    ];
    static readonly string[] LocalSuffixes = [".localhost", ".local", ".test", ".internal", ".lan", ".home.arpa"];

    /// Pages never kept, whatever else the rules say: local hosts, sign-in providers, captive portals and shorteners,
    /// inboxes, search results and a search engine's front page, one-time links, files, and challenge or error titles.
    /// `route` is the path inside a hash route (#/oauth/callback), which has its own first two sections.
    internal static bool IsHardSkip(string site, int? port, IReadOnlyList<string> path, string title, IReadOnlyList<string>? route = null)
    {
        var all = route is null ? path : path.Concat(route).ToList();
        if (site.Length == 0 || port is not null || IsLocal(site)) return true;
        if (PassThroughHosts.Any(h => SiteRules.HostMatches(site, h))) return true;
        var firstLabel = Text.SplitOmitEmpty(site, '.').FirstOrDefault() ?? "";
        if (IdentityHosts.Contains(site)) return true;
        if (IdentitySuffixes.Any(s => site.EndsWith(s, StringComparison.Ordinal)) && !IdentityDocLabels.Contains(firstLabel)) return true;
        if (AuthHostLabels.Contains(firstLabel)) return true;
        if (InboxHosts.Any(h => SiteRules.HostMatches(site, h))) return true;
        if (all.Count > 0 && ChatAppPaths.Any(c => IsChatHost(site, c.Host) && c.Paths.Contains(all[0].ToLowerInvariant()))) return true;
        if (all.Count == 0 && SearchEngineHosts.Any(h => SiteRules.HostMatches(site, h))) return true;
        if (all.Count > 0 && InboxWords.Contains(all[0].ToLowerInvariant())) return true;
        bool reading = firstLabel is "docs" or "developer" or "developers";
        if ((!reading && (HasOneTimeWord(path) || (route is not null && HasOneTimeWord(route)))) || HasSecretPath(all)) return true;
        if (IsFile(all.Count > 0 ? all[^1] : null)) return true;
        return IsUselessTitle(title);
    }

    /// Nouns that may end a flow section after its flow word: reset-password, confirm-email, magic-link, redeem-code.
    static readonly HashSet<string> SecretFlowTails = ["password", "link", "email", "account", "code"];

    /// A flow section (reset-password, accept-invite; not magic-string or token-service) followed straight away by a long,
    /// random-looking section: a one-time link, never saved or kept. A section that is only the flow word (/invite/acme/<code>)
    /// may have one section between it and the token.
    internal static bool HasSecretPath(IReadOnlyList<string> path)
    {
        for (int i = 0; i < path.Count; i++)
        {
            var pieces = Text.SplitWhere(path[i].ToLowerInvariant(), r => r.Value == '-' || r.Value == '_');
            if (pieces.Count == 0 || !pieces.Any(SecretFlowWords.Contains)) continue;
            if (!SecretFlowWords.Contains(pieces[^1]) && !SecretFlowTails.Contains(pieces[^1])) continue;
            int reach = pieces.Count == 1 ? 2 : 1;
            for (int j = i + 1; j < path.Count && j <= i + reach; j++) if (IsSecretToken(path[j])) return true;
        }
        return false;
    }

    /// shortcut: ASCII letters, digits, "-" and "_" only, and no all-digit codes; add dots (JWTs) or digit-only tokens if a site needs it.
    internal static bool IsSecretToken(string s)
    {
        if (Text.Count(s) == 36 && Text.SplitOmitEmpty(s, '-').Select(Text.Count).SequenceEqual([8, 4, 4, 4, 12])) return true;
        if (s.Length < 12 || !s.All(c => c < 128 && (Text.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))) return false;
        if (!s.Any(char.IsAsciiLetter) || !s.Any(char.IsAsciiDigit)) return false;
        // A lowercase section with separators is a slug ("team-handbook-2024"), not a token.
        return s.Any(char.IsAsciiLetterUpper) || !s.Any(c => c == '-' || c == '_');
    }

    /// "report.pdf" or "data.json", but not "git-cheat-sheet-education.pdf" or "index.html".
    internal static bool IsFile(string? section)
    {
        if (section is null) return false;
        int dot = section.LastIndexOf('.');
        if (dot < 0) return false;
        return FileExtensions.Contains(section[(dot + 1)..].ToLowerInvariant()) && !IsSlug(section);
    }

    /// A one-time word anywhere in a path, unless it is documentation.
    static bool HasOneTimeWord(IReadOnlyList<string> path)
    {
        if (path.Count == 0 || DocsSections.Contains(path[0].ToLowerInvariant())) return false;
        return path.Any(p => OneTimeWords.Contains(p.ToLowerInvariant()));
    }

    /// The chat site itself or one of its subdomains (ptb., canary.) that isn't one for reading.
    static bool IsChatHost(string site, string chat) =>
        site == chat || (site.EndsWith("." + chat, StringComparison.Ordinal) && !ChatDocLabels.Contains(Text.SplitOmitEmpty(site, '.')[0]));

    internal static bool IsUselessTitle(string title)
    {
        var words = Text.SplitWhere(title.ToLowerInvariant(), r => !Text.IsLetter(r) && !Text.IsNumber(r));
        if (words.Count > 5) return false;
        // A one-word phrase is the page's own title, alone or before its site's name: "Forbidden – Site", not "Forbidden Planet".
        int cut = TitleSeparators.Select(s => title.IndexOf(s, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(title.Length).Min();
        var lead = Text.SplitWhere(title[..cut].ToLowerInvariant(), r => !Text.IsLetter(r) && !Text.IsNumber(r));
        return UselessTitles.Any(phrase => phrase.Length == 1
                ? lead.Count == 1 && lead[0] == phrase[0]
                : words.Count >= phrase.Length && words.Take(phrase.Length).SequenceEqual(phrase))
            || BlankTitles.Any(phrase => words.SequenceEqual(phrase));
    }

    /// Localhost, home-network names and private addresses.
    internal static bool IsLocal(string host)
    {
        if (!host.Contains('.', StringComparison.Ordinal) || host.Contains(':', StringComparison.Ordinal)) return true;   // "localhost", "nas", IPv6
        if (LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal))) return true;
        var octets = new List<int>();
        foreach (var piece in Text.SplitOmitEmpty(host, '.'))
            if (int.TryParse(piece, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) octets.Add(n);
        if (octets.Count != 4 || !octets.All(o => o is >= 0 and <= 255)) return false;
        return octets[0] == 10 || octets[0] == 127 || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 172 && octets[1] is >= 16 and <= 31) || (octets[0] == 169 && octets[1] == 254);
    }

    internal static bool IsIDLike(string s)
    {
        int run = 0, longest = 0, letters = 0, digits = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (r.IsAscii && Text.IsNumber(r))
            {
                digits++;
                run++;
                longest = Math.Max(longest, run);
            }
            else
            {
                run = 0;
                if (Text.IsLetter(r)) letters++;
            }
        }
        if (longest >= 6) return true;
        if (Text.Count(s) == 36 && Text.SplitOmitEmpty(s, '-').Select(Text.Count).SequenceEqual([8, 4, 4, 4, 12])) return true;
        return Text.Count(s) >= 8 && letters >= 1 && digits >= 1
            && s.All(c => c < 128 && (Text.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'));
    }

    internal static bool IsSlug(string s)
    {
        var words = Text.SplitWhere(s, r => r.Value == '-' || r.Value == '_');
        return words.Count >= 3 && words.All(w => Text.Any(w, Text.IsLetter));
    }

    internal static bool HasDate(IReadOnlyList<string> path)
    {
        for (int i = 0; i + 1 < path.Count; i++)
        {
            string year = path[i], month = path[i + 1];
            if (Text.Count(year) == 4 && (year.StartsWith("19", StringComparison.Ordinal) || year.StartsWith("20", StringComparison.Ordinal))
                && Text.All(year, Text.IsNumber) && Text.Count(month) is 1 or 2 && Text.All(month, Text.IsNumber)) return true;
        }
        return false;
    }

    /// Host and path shape only; query keys would split one kind of page into many.
    internal static string PageType(string site, IEnumerable<string> path) =>
        site + "/" + string.Join('/', path.Select(p => IsIDLike(p) || IsSlug(p) ? "*" : p.ToLowerInvariant()));
}
