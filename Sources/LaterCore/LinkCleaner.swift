import Foundation

public enum LinkCleaner {
    /// The cleaned link, or nil when the page must never be tracked or saved.
    public static func clean(_ raw: String) -> URL? {
        guard var parts = URLComponents(string: raw),
              let scheme = parts.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = parts.host?.lowercased(), !host.isEmpty else { return nil }
        if parts.user != nil || parts.password != nil { return nil }
        parts.scheme = scheme
        parts.host = host
        if (scheme == "https" && parts.port == 443) || (scheme == "http" && parts.port == 80) { parts.port = nil }
        if parts.path.isEmpty { parts.path = "/" }

        // Percent-encoded items so kept parameters keep their exact encoding (C%2B%2B must not become C++).
        var items = parts.percentEncodedQueryItems ?? []
        if items.contains(where: { isSecret(key($0.name)) }) { return nil }
        items.removeAll { isTracking(key($0.name), host: host) }
        parts.percentEncodedQueryItems = items.isEmpty ? nil : items

        if let fragment = parts.percentEncodedFragment {
            // Decoded, so #%2F… and #%21… count as routes too; never merge distinct pages.
            let route = parts.fragment ?? fragment
            if !(route.hasPrefix("/") || route.hasPrefix("!")) {
                parts.fragment = nil
            } else {
                // Route fragments like #/login/callback?token=… carry their own query; check it encoded
                // and decoded once, since a hash router reads #/login%3Ftoken=x as /login?token=x.
                for text in [fragment, route] {
                    guard let q = text.firstIndex(of: "?") else { continue }
                    let names = text[text.index(after: q)...].split(separator: "&")
                        .map { key(String($0.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)[0])) }
                    if names.contains(where: isSecret) { return nil }
                }
            }
        }

        if let asin = amazonASIN(host: host, path: parts.path) {
            var product = URLComponents()
            product.scheme = "https"
            product.host = host
            product.path = "/dp/\(asin)"
            return product.url
        }
        return parts.url
    }

    /// One string for one page, from a cleaned link: the same page opened as www or bare, over http or https, with or
    /// without a trailing or doubled slash, with its query in another order, or with noise keys (a YouTube timestamp, a
    /// playlist position) is one page. Pages that differ in path, in an identity key's value, or in a product variant
    /// (color, size) stay apart. Not a link: it is only ever compared and hashed.
    public static func pageKey(_ url: URL) -> String {
        guard let parts = URLComponents(url: url, resolvingAgainstBaseURL: false), let rawHost = parts.host?.lowercased() else {
            return url.absoluteString
        }
        var host = rawHost.hasPrefix("www.") ? String(rawHost.dropFirst(4)) : rawHost
        var path = parts.percentEncodedPath.split(separator: "/").map(String.init)
        var items = parts.percentEncodedQueryItems ?? []
        if host == "youtu.be", path.count == 1 {   // youtu.be/<id> is youtube.com/watch?v=<id>
            host = "youtube.com"
            items.append(URLQueryItem(name: "v", value: path[0]))
            path = ["watch"]
        }
        // A Google document is its id, whether it is opened to edit, view or preview.
        if host == "docs.google.com", documentKinds.contains(path.first ?? ""), ["edit", "view", "preview"].contains(path.last ?? "") {
            path.removeLast()
        }
        if let identity = SiteRules.identityKey(host: host, path: path) {
            items.removeAll { key($0.name) != identity }
        } else {
            let idPath = path.contains(where: Structure.isIDLike)
            let youtube = matches(host, ["youtube.com", "youtu.be"])
            items.removeAll {
                let name = key($0.name)
                return isTracking(name, host: host) || (youtube && youtubeNoiseKeys.contains(name)) || (idPath && viewStateKeys.contains(name))
            }
        }
        items.sort { ($0.name, $0.value ?? "") < ($1.name, $1.value ?? "") }
        var result = host + (parts.port.map { ":\($0)" } ?? "") + "/" + path.joined(separator: "/")
        if !items.isEmpty { result += "?" + items.map { $0.name + ($0.value.map { "=" + $0 } ?? "") }.joined(separator: "&") }
        if let route = parts.percentEncodedFragment { result += "#" + route }
        return result
    }

    static let documentKinds: Set<String> = ["document", "spreadsheets", "presentation"]
    static let youtubeNoiseKeys: Set<String> = [
        "autoplay", "embeds_referring_euri", "source_ve_path", "t", "start", "feature", "pp", "si",
    ]
    /// How a page is shown, not which page it is, on pages named by an ID in their path.
    static let viewStateKeys: Set<String> = ["view", "state", "tab", "sort", "order", "mode"]

    /// True for `name.<storefront suffix>` and its subdomains, e.g. amazon.com, www.amazon.co.uk.
    public static func isStorefront(_ host: String, name: String) -> Bool {
        matches(host.lowercased(), storefrontSuffixes.map { "\(name).\($0)" })
    }

    static let storefrontSuffixes = [
        "com", "co.uk", "de", "fr", "it", "es", "ca", "com.au", "co.jp", "in", "com.mx", "com.br", "nl", "se",
        "pl", "sg", "ae", "sa", "com.tr", "eg", "com.be", "ie", "at", "ch", "be", "cn",
    ]

    static func key(_ encodedName: String) -> String {
        (encodedName.removingPercentEncoding ?? encodedName).lowercased()
    }

    /// Exact secret keys, or keys with a secret word as a piece, e.g. auth_token, x-amz-signature.
    static func isSecret(_ key: String) -> Bool {
        secretKeys.contains(key)
            || key.split(whereSeparator: { !$0.isLetter && !$0.isNumber }).contains { secretWords.contains(String($0)) }
    }

    static let secretWords: Set<String> = [
        "token", "code", "session", "sessionid", "auth", "authorization", "key", "apikey", "password", "passwd",
        "pwd", "signature", "sig", "otp", "secret", "credential", "credentials",
    ]

    static let secretKeys: Set<String> = [
        "token", "access_token", "id_token", "refresh_token", "code", "session", "sessionid", "session_id",
        "auth", "authorization", "key", "api_key", "apikey", "password", "passwd", "pwd",
        "signature", "sig", "otp", "magic_link", "reset_token",
    ]

    static let trackingKeys: Set<String> = [
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "mc_cid", "mc_eid", "igshid", "igsh",
        "ref", "ref_", "ref_src", "ref_url", "referrer", "spm", "_hsenc", "_hsmi", "yclid", "twclid",
        "li_fat_id", "srsltid",
    ]
    static let amazonKeys: Set<String> = [
        "tag", "psc", "th", "qid", "sr", "keywords", "crid", "sprefix", "content-id", "linkcode", "linkid",
        "camp", "creative", "creativeasin", "ascsubtag", "smid", "dib", "dib_tag",
    ]
    static let youtubeKeys: Set<String> = ["si", "feature", "pp"]

    static func isTracking(_ key: String, host: String) -> Bool {
        if key.hasPrefix("utm_") || key.hasPrefix("pd_rd_") || key.hasPrefix("pf_rd_") { return true }
        if trackingKeys.contains(key) { return true }
        if isStorefront(host, name: "amazon"), amazonKeys.contains(key) { return true }
        if matches(host, ["youtube.com", "youtu.be"]), youtubeKeys.contains(key) { return true }
        if matches(host, ["x.com", "twitter.com"]), key == "s" || key == "t" { return true }
        if matches(host, ["notion.so", "notion.site"]), key == "pvs" { return true }
        if host == "docs.google.com" || host == "drive.google.com", key == "usp" { return true }
        return false
    }

    static func matches(_ host: String, _ domains: [String]) -> Bool {
        domains.contains { host == $0 || host.hasSuffix("." + $0) }
    }

    static func amazonASIN(host: String, path: String) -> String? {
        guard isStorefront(host, name: "amazon") else { return nil }
        let segments = path.split(separator: "/").map(String.init)
        for i in segments.indices where i + 1 < segments.count {
            let s = segments[i]
            let marker = s == "dp"
                || (s == "product" && i > 0 && segments[i - 1] == "gp")
                || (s == "d" && i > 1 && segments[i - 1] == "aw" && segments[i - 2] == "gp")
            let candidate = segments[i + 1]
            if marker, candidate.count == 10, candidate.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) }) {
                return candidate.uppercased()
            }
        }
        return nil
    }
}
