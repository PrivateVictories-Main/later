using System.Globalization;
using System.Text;

namespace Later.Core;

/// What a page is made of, for sites without a rule of their own, and the pages never kept.
internal static class Structure
{
    /// Pages that are in the way: sign-in redirects, one-time links, confirmations and errors. A sign-in or sign-up page
    /// itself is kept; these words only ever show up on the way through. Matched as whole path sections.
    static readonly HashSet<string> OneTimeWords =
    [
        "oauth", "oauth2", "authorize", "callback", "logout", "signout", "log-out", "sign-out", "redirect", "unsubscribe",
        "error", "success", "thank-you", "thankyou", "verify", "verify-email", "consent",
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
    /// A host that starts with one of these exists only for signing in.
    static readonly HashSet<string> AuthHostLabels = ["sso", "idp", "auth", "login", "signin", "accounts"];
    /// Mail and chat, where every page is an inbox.
    static readonly string[] InboxHosts =
    [
        "mail.google.com", "outlook.live.com", "outlook.office.com", "outlook.office365.com", "outlook.com", "mail.yahoo.com",
        "mail.proton.me", "messenger.com", "web.whatsapp.com", "web.telegram.org", "app.slack.com", "discord.com",
        "teams.microsoft.com",
    ];
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
    /// words or fewer) and starts with the phrase, so "The Forbidden City" and "How CAPTCHA works" are real pages.
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
    internal static bool IsHardSkip(string site, int? port, IReadOnlyList<string> path, string title)
    {
        if (site.Length == 0 || port is not null || IsLocal(site)) return true;
        if (PassThroughHosts.Any(h => SiteRules.HostMatches(site, h))) return true;
        if (IdentityHosts.Contains(site) || IdentitySuffixes.Any(s => site.EndsWith(s, StringComparison.Ordinal))) return true;
        var firstLabel = Text.SplitOmitEmpty(site, '.').FirstOrDefault() ?? "";
        if (AuthHostLabels.Contains(firstLabel)) return true;
        if (InboxHosts.Any(h => SiteRules.HostMatches(site, h))) return true;
        if (path.Count == 0 && SearchEngineHosts.Any(h => SiteRules.HostMatches(site, h))) return true;
        if (path.Count > 0 && InboxWords.Contains(path[0].ToLowerInvariant())) return true;
        if (path.Any(p => OneTimeWords.Contains(p.ToLowerInvariant()))) return true;
        if (IsFile(path.Count > 0 ? path[^1] : null)) return true;
        return IsUselessTitle(title);
    }

    /// "report.pdf" or "data.json", but not "git-cheat-sheet-education.pdf" or "index.html".
    internal static bool IsFile(string? section)
    {
        if (section is null) return false;
        int dot = section.LastIndexOf('.');
        if (dot < 0) return false;
        return FileExtensions.Contains(section[(dot + 1)..].ToLowerInvariant()) && !IsSlug(section);
    }

    internal static bool IsUselessTitle(string title)
    {
        var words = Text.SplitWhere(title.ToLowerInvariant(), r => !Text.IsLetter(r) && !Text.IsNumber(r));
        if (words.Count > 5) return false;
        return UselessTitles.Any(phrase => words.Count >= phrase.Length && words.Take(phrase.Length).SequenceEqual(phrase))
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
