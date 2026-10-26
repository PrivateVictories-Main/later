import Foundation
import Testing
@testable import LaterCore

@Suite struct PageRulesTests {
    struct Case: Sendable, CustomTestStringConvertible {
        let url: String
        let title: String
        let specific: Bool
        let kind: Kind
        var testDescription: String { url }
    }

    static let cases: [Case] = [
        // Amazon
        Case(url: "https://www.amazon.com/", title: "Amazon.com. Spend less. Smile more.", specific: false, kind: .shopping),
        Case(url: "https://www.amazon.com/s?k=standing+desk", title: "Amazon.com : standing desk", specific: false, kind: .shopping),
        Case(url: "https://www.amazon.com/Sony-WH-1000XM6/dp/B0F1234567/ref=sr_1_3?keywords=sony", title: "Amazon.com: Sony WH-1000XM6 Wireless Headphones", specific: true, kind: .shopping),
        Case(url: "https://www.amazon.com/gp/cart/view.html", title: "Amazon.com Shopping Cart", specific: true, kind: .shopping),
        Case(url: "https://www.amazon.com/your-orders/orders", title: "Your Orders", specific: true, kind: .shopping),
        // X
        Case(url: "https://x.com/home", title: "Home / X", specific: false, kind: .posts),
        Case(url: "https://x.com/karpathy/status/1890000000000000000?s=20", title: "Andrej Karpathy on X: \"agents thread\"", specific: true, kind: .posts),
        Case(url: "https://x.com/explore", title: "Explore / X", specific: true, kind: .posts),
        Case(url: "https://x.com/karpathy", title: "Andrej Karpathy (@karpathy) / X", specific: true, kind: .posts),
        Case(url: "https://x.com/search?q=swift", title: "swift - Search / X", specific: false, kind: .posts),
        // YouTube
        Case(url: "https://www.youtube.com/", title: "YouTube", specific: false, kind: .videos),
        Case(url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=abc", title: "Never Gonna Give You Up - YouTube", specific: true, kind: .videos),
        Case(url: "https://www.youtube.com/results?search_query=swiftui", title: "swiftui - YouTube", specific: false, kind: .videos),
        Case(url: "https://www.youtube.com/shorts/abcDEF12345", title: "cool short - YouTube", specific: true, kind: .videos),
        Case(url: "https://www.youtube.com/feed/subscriptions", title: "Subscriptions - YouTube", specific: false, kind: .videos),
        Case(url: "https://youtu.be/dQw4w9WgXcQ", title: "Never Gonna Give You Up", specific: true, kind: .videos),
        // Reddit
        Case(url: "https://www.reddit.com/", title: "Reddit - Dive into anything", specific: false, kind: .posts),
        Case(url: "https://www.reddit.com/r/swift/", title: "r/swift", specific: true, kind: .posts),
        Case(url: "https://www.reddit.com/r/swift/comments/1abcde/how_do_you_structure_apps/", title: "How do you structure apps? : r/swift", specific: true, kind: .posts),
        // GitHub
        Case(url: "https://github.com/", title: "GitHub", specific: true, kind: .tools),
        Case(url: "https://github.com/notifications", title: "Notifications", specific: false, kind: .tools),
        Case(url: "https://github.com/apple/swift/issues/12345", title: "Crash in X · Issue #12345 · apple/swift", specific: true, kind: .tools),
        Case(url: "https://github.com/apple/swift/pull/678", title: "Fix Y by z · Pull Request #678 · apple/swift", specific: true, kind: .tools),
        Case(url: "https://github.com/apple/swift", title: "GitHub - apple/swift: The Swift Programming Language", specific: true, kind: .tools),
        Case(url: "https://github.com/settings/profile", title: "Your profile", specific: true, kind: .tools),
        Case(url: "https://github.com/octocat", title: "octocat (The Octocat)", specific: true, kind: .tools),
        Case(url: "https://github.com/login", title: "Sign in to GitHub", specific: true, kind: .tools),
        Case(url: "https://github.com/new", title: "Create a new repository", specific: true, kind: .tools),
        Case(url: "https://github.com/pricing", title: "Pricing", specific: true, kind: .tools),
        // Canvas
        Case(url: "https://canvas.school.edu/", title: "Dashboard", specific: true, kind: .school),
        Case(url: "https://canvas.school.edu/courses/12345", title: "HIST 210", specific: true, kind: .school),
        Case(url: "https://canvas.school.edu/courses/12345/assignments/67890", title: "Essay 2 prompt: HIST 210", specific: true, kind: .school),
        Case(url: "https://school.instructure.com/courses/12345/quizzes/222", title: "Quiz 3: BIO 101", specific: true, kind: .school),
        // Blackboard
        Case(url: "https://learn.school.edu/ultra/stream", title: "Activity Stream", specific: true, kind: .school),
        Case(url: "https://learn.school.edu/ultra/courses/_12345_1/outline", title: "Course Content – ENG 102", specific: true, kind: .school),
        Case(url: "https://learn.school.edu/ultra/courses/_12345_1/outline/assessment/_998877_1/overview", title: "Research Paper Draft", specific: true, kind: .school),
        // Google Classroom
        Case(url: "https://classroom.google.com/u/0/h", title: "Classes", specific: true, kind: .school),
        Case(url: "https://classroom.google.com/c/NjQ1MjM0NTY3ODk/a/NzEyMzQ1Njc4OTA/details", title: "Lab report 4", specific: true, kind: .school),
        // Moodle on a school's own host
        Case(url: "https://moodle.college.org/mod/assign/view.php?id=4567", title: "Assignment 2: Ethics", specific: true, kind: .school),
        // Reading
        Case(url: "https://en.wikipedia.org/wiki/Main_Page", title: "Wikipedia, the free encyclopedia", specific: true, kind: .reading),
        Case(url: "https://en.wikipedia.org/wiki/SQLite#History", title: "SQLite - Wikipedia", specific: true, kind: .reading),
        Case(url: "https://www.theverge.com/2026/10/20/24123456/apple-new-macbook", title: "Apple's new MacBook | The Verge", specific: true, kind: .reading),
        Case(url: "https://www.theverge.com/", title: "The Verge", specific: true, kind: .reading),
        Case(url: "https://someblog.dev/posts/how-sqlite-stores-data", title: "How SQLite stores data", specific: true, kind: .tools),
        // Unknown sites: structure rules, then a site's own shallow pages
        Case(url: "https://example.com/", title: "Example Domain", specific: true, kind: .tools),
        Case(url: "https://shop.example.com/products/9876543", title: "Walnut desk organizer", specific: true, kind: .other),
        Case(url: "https://example.com/settings/billing", title: "Billing", specific: true, kind: .tools),
        Case(url: "https://example.com/search?q=desk", title: "Results for desk", specific: false, kind: .other),
        Case(url: "https://example.com/about", title: "About us", specific: true, kind: .tools),
        Case(url: "https://example.com/pricing", title: "Pricing", specific: true, kind: .tools),
        Case(url: "https://example.com/about-us", title: "About Us", specific: true, kind: .tools),
        Case(url: "https://example.com/about-the-company", title: "About the Company | Example", specific: true, kind: .other),
        Case(url: "https://example.com/about_our_team", title: "Our Team", specific: true, kind: .other),
        Case(url: "https://example.com/contact-sales-team", title: "Contact Sales", specific: true, kind: .other),
        Case(url: "https://someblog.dev/blog/about-the-new-macbook", title: "About the new MacBook", specific: true, kind: .tools),
        Case(url: "https://blog.example.com/about-the-new-macbook", title: "About the new MacBook", specific: true, kind: .reading),
        Case(url: "https://example.com/contact-lens-review-for-runners", title: "Contact lens review for runners", specific: true, kind: .other),
        Case(url: "https://example.com/about-our-team", title: "Meet the team", specific: true, kind: .other),
        Case(url: "https://example.com/contact-support", title: "Contact Support", specific: true, kind: .tools),
        Case(url: "https://example.com/help", title: "Help | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/support", title: "Support | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/contact", title: "Contact | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/faq", title: "Faq | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/status", title: "Status | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/careers", title: "Careers | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/jobs", title: "Jobs | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/press", title: "Press | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/help/billing", title: "Billing help | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/legal/terms-of-service-agreement", title: "Terms of Service", specific: true, kind: .other),
        Case(url: "https://example.com/legal/how-to-license-open-source-software", title: "How to license open source software", specific: true, kind: .other),
        Case(url: "https://example.com/legal", title: "Legal | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/legal/privacy", title: "Privacy Policy | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/legal/terms", title: "Terms | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/terms-of-service", title: "Terms of Service | Example", specific: true, kind: .other),
        Case(url: "https://example.com/privacy-policy", title: "Privacy Policy | Example", specific: true, kind: .tools),
        Case(url: "https://example.com/aboutness-of-cats-explained", title: "The aboutness of cats", specific: true, kind: .other),
        Case(url: "https://example.com/items/9876543", title: "", specific: false, kind: .other),
        // Docs
        Case(url: "https://developer.apple.com/documentation", title: "Apple Developer Documentation", specific: true, kind: .reading),
        Case(url: "https://developer.apple.com/documentation/swiftui", title: "SwiftUI | Apple Developer Documentation", specific: true, kind: .reading),
        Case(url: "https://developer.apple.com/documentation/swiftui/view", title: "View | Apple Developer Documentation", specific: true, kind: .reading),
        Case(url: "https://developer.apple.com/documentation/swiftui/view/body-8kl5o", title: "body | Apple Developer Documentation", specific: true, kind: .reading),
        Case(url: "https://developer.apple.com/", title: "Apple Developer", specific: true, kind: .reading),
        Case(url: "https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQrStUvWxYz/edit", title: "Essay outline - Google Docs", specific: true, kind: .other),
    ]

    @Test(arguments: cases)
    func judges(_ c: Case) throws {
        let url = try #require(LinkCleaner.clean(c.url))
        let verdict = PageRules.judge(url, title: c.title)
        #expect(verdict.isSpecific == c.specific)
        #expect(verdict.kind == c.kind)
    }

    @Test func siteRulePageTypesCoverEveryPost() throws {
        let url = try #require(LinkCleaner.clean("https://x.com/someone/status/1234567890123"))
        #expect(PageRules.judge(url, title: "Post").pageType == "x.com/*/status/*")
    }

    @Test func structurePageTypesReplaceIDsAndSlugs() throws {
        let url = try #require(LinkCleaner.clean("https://someblog.dev/blog/how-sqlite-stores-data?page=2"))
        #expect(PageRules.judge(url, title: "How SQLite stores data").pageType == "someblog.dev/blog/*")
    }

    @Test func correctionsAlwaysWin() throws {
        let post = try #require(LinkCleaner.clean("https://x.com/someone/status/1234567890123"))
        let notIdea = PageRules.judge(post, title: "Post", corrections: ["x.com/*/status/*": Correction(isSpecific: false)])
        #expect(!notIdea.isSpecific)
        let blog = try #require(LinkCleaner.clean("https://someblog.dev/blog/how-sqlite-stores-data"))
        let moved = PageRules.judge(blog, title: "How SQLite stores data", corrections: ["someblog.dev/blog/*": Correction(kind: .school)])
        #expect(moved.isSpecific)
        #expect(moved.kind == .school)
    }

    @Test func hashRoutesAreJudgedByTheirRoute() throws {
        let item = try #require(LinkCleaner.clean("https://app.example.com/#/items/9876543"))
        let itemVerdict = PageRules.judge(item, title: "Walnut desk")
        #expect(itemVerdict.isSpecific)
        #expect(itemVerdict.pageType == "app.example.com/#/items/*")
        let dashboard = try #require(LinkCleaner.clean("https://app.example.com/#/dashboard"))
        let dashboardVerdict = PageRules.judge(dashboard, title: "Dashboard")
        #expect(dashboardVerdict.isSpecific)   // a plain route is kept too, under a page type of its own
        #expect(dashboardVerdict.pageType != itemVerdict.pageType)
    }

    @Test func aMoodleCourseHomeIsAKeptClass() throws {
        let url = try #require(LinkCleaner.clean("https://moodle.college.org/course/view.php?id=4567"))
        let verdict = PageRules.judge(url, title: "ETH 101")
        #expect(verdict.isSpecific)
        #expect(verdict.kind == .school)
    }

    @Test func mixedCodesNeedOneLetterAndOneDigit() throws {
        let url = try #require(LinkCleaner.clean("https://shop.example.com/items/ABCDEFG1"))
        let verdict = PageRules.judge(url, title: "Walnut desk")
        #expect(verdict.isSpecific)
        #expect(verdict.pageType.hasSuffix("/items/*"))
    }

    @Test func structureCorrectionsIgnoreQueryKeys() throws {
        let url = try #require(LinkCleaner.clean("https://someblog.dev/blog/how-sqlite-stores-data?page=2"))
        let verdict = PageRules.judge(url, title: "How SQLite stores data", corrections: ["someblog.dev/blog/*": Correction(kind: .school)])
        #expect(verdict.kind == .school)
    }

    @Test func aDateAloneIsKeptToo() throws {
        let url = try #require(LinkCleaner.clean("https://someblog.dev/2026/10/"))
        #expect(PageRules.judge(url, title: "October 2026 Archives").isSpecific)
    }

    @Test func helpArticlesAreSpecific() throws {
        let article = try #require(LinkCleaner.clean("https://example.com/help/how-to-reset-password"))
        #expect(PageRules.judge(article, title: "Reset your password").isSpecific)
    }

    @Test func profilesAreKeptWhateverTheName() throws {
        for raw in ["https://x.com/alice1234", "https://www.instagram.com/alice1234",
                    "https://www.threads.net/@alice1234", "https://bsky.app/profile/alice1234.bsky.social"] {
            let url = try #require(LinkCleaner.clean(raw))
            let verdict = PageRules.judge(url, title: "Alice (@alice1234)")
            #expect(verdict.isSpecific && verdict.kind == .posts, "\(raw)")
        }
        let post = try #require(LinkCleaner.clean("https://x.com/alice1234/status/1890000000000000000"))
        #expect(PageRules.judge(post, title: "Alice on X").isSpecific)
        let repo = try #require(LinkCleaner.clean("https://github.com/alice1234/notes"))
        #expect(PageRules.judge(repo, title: "alice1234/notes").isSpecific)
    }

    @Test func githubTopicPagesAreKept() throws {
        let topic = try #require(LinkCleaner.clean("https://github.com/topics/swift"))
        #expect(PageRules.judge(topic, title: "swift · GitHub Topics").isSpecific)
        let repo = try #require(LinkCleaner.clean("https://github.com/apple/swift"))
        let verdict = PageRules.judge(repo, title: "GitHub - apple/swift")
        #expect(verdict.isSpecific)
        #expect(verdict.pageType == "github.com/*/*")
    }

    @Test func encodedQueryValuesStayInsideTheirRouteKey() throws {
        for raw in ["https://app.example.com/#/items/ABCDEFG1?returnTo=%2Fsearch%3Fsort%3Dprice%26q%3Ddesk",
                    "https://app.example.com/items/ABCDEFG1?returnTo=%2Fsearch%3Fsort%3Dprice%26q%3Ddesk"] {
            let url = try #require(LinkCleaner.clean(raw))
            #expect(PageRules.judge(url, title: "Walnut desk organizer").isSpecific, "\(raw)")
        }
    }

    @Test func profileRulesStayOnTheirOwnHost() throws {
        let pdf = try #require(LinkCleaner.clean("https://education.github.com/git-cheat-sheet-education.pdf"))
        #expect(PageRules.judge(pdf, title: "GIT CHEAT SHEET").isSpecific)
        let profile = try #require(LinkCleaner.clean("https://github.com/alice1234"))
        #expect(PageRules.judge(profile, title: "alice1234 (Alice)").isSpecific)
        let repo = try #require(LinkCleaner.clean("https://github.com/apple/swift"))
        #expect(PageRules.judge(repo, title: "GitHub - apple/swift").isSpecific)
        let insta = try #require(LinkCleaner.clean("https://www.instagram.com/someuser"))
        #expect(PageRules.judge(insta, title: "Some User (@someuser)").isSpecific)
        let post = try #require(LinkCleaner.clean("https://x.com/karpathy/status/1890000000000000000"))
        #expect(PageRules.judge(post, title: "Andrej Karpathy on X").isSpecific)
    }

    static let routeMarkers = ["#/", "#!/", "#%2F", "#%21/"].flatMap { [($0, ""), ($0, "?returnTo=%2Fsearch%3Fq%3Ddesk")] }

    @Test(arguments: routeMarkers)
    func routeMarkersAreJudgedAfterDecoding(_ marker: String, _ query: String) throws {
        let item = try #require(LinkCleaner.clean("https://app.example.com/\(marker)items/ABCDEFG1\(query)"))
        let itemVerdict = PageRules.judge(item, title: "Walnut desk organizer")
        #expect(itemVerdict.isSpecific)
        #expect(itemVerdict.pageType == "app.example.com/#/items/*")
        let dashboard = try #require(LinkCleaner.clean("https://app.example.com/\(marker)dashboard\(query)"))
        let dashboardVerdict = PageRules.judge(dashboard, title: "Dashboard")
        #expect(dashboardVerdict.isSpecific)
        #expect(dashboardVerdict.pageType != itemVerdict.pageType)
        let corrections = ["app.example.com/#/items/*": Correction(isSpecific: false)]
        #expect(!PageRules.judge(item, title: "Walnut desk organizer", corrections: corrections).isSpecific)
        #expect(PageRules.judge(dashboard, title: "Dashboard", corrections: corrections) == dashboardVerdict)
    }

    @Test func gistProfilesAndGistsAreBothKept() throws {
        let profile = try #require(LinkCleaner.clean("https://gist.github.com/alice1234"))
        #expect(PageRules.judge(profile, title: "alice1234’s gists").isSpecific)
        let gist = try #require(LinkCleaner.clean("https://gist.github.com/alice1234/0123456789abcdef0123456789abcdef"))
        #expect(PageRules.judge(gist, title: "notes.md").isSpecific)
    }
}

@Suite struct PathPatternTests {
    static func segments(_ path: String) -> [String] { path.split(separator: "/").map(String.init) }

    @Test func doubleStarMatchesZeroSegments() {
        #expect(("/explore/**" as PathPattern).matches(Self.segments("/explore"), queryKeys: []))
    }

    @Test func missingQueryKeyFails() {
        let watch: PathPattern = "/watch?v"
        #expect(!watch.matches(Self.segments("/watch"), queryKeys: []))
        #expect(watch.matches(Self.segments("/watch"), queryKeys: ["v"]))
    }

    @Test func segmentsIgnoreCase() {
        #expect(("/wiki/Main_Page" as PathPattern).matches(Self.segments("/WIKI/main_page"), queryKeys: []))
    }

    @Test func trailingSlashesDontMatter() {
        #expect(("/r/*" as PathPattern).matches(Self.segments("/r/swift/"), queryKeys: []))
    }
}

/// v1.1: a site's own shallow pages are kept (as Tools) unless the host is a utility, local, or the page is in the way.
@Suite struct SiteLevelTests {
    static let typicalSites = [
        "https://brightlane.example.com/", "https://www.pinecone.example.com/", "https://quill.example.com/", "https://search-lab.example.com/",
        "https://linkvault.example.net/", "https://linkvault.example.net/video", "https://acmehq.example.com/", "https://github.com/octocat",
    ]

    @Test(arguments: typicalSites, ["", "A page title"])
    func keptOpenSitesAreSpecificTools(_ raw: String, _ title: String) throws {
        let verdict = PageRules.judge(try #require(LinkCleaner.clean(raw)), title: title)
        #expect(verdict.isSpecific, "\(raw)")
        #expect(verdict.kind == .tools, "\(raw)")
    }

    @Test func oneTypePerHostSoOneCorrectionCoversItsShallowPages() throws {
        let home = PageRules.judge(try #require(LinkCleaner.clean("https://linkvault.example.net/")), title: "Link Vault")
        let video = PageRules.judge(try #require(LinkCleaner.clean("https://linkvault.example.net/video")), title: "Link Vault")
        #expect(home.pageType == "linkvault.example.net/~" && video.pageType == "linkvault.example.net/~")
    }

    /// Search, inboxes, one-time flows, sign-in providers, feed homes, local hosts and challenge titles are never kept.
    static let mustStayGeneral = [
        ("https://www.google.com/search?q=swift", "swift - Google Search"), ("https://www.google.com/", "Google"),
        ("https://mail.google.com/mail/u/0/", "Inbox (3) - Gmail"), ("https://outlook.live.com/mail/0/inbox", "Outlook"),
        ("https://discord.com/channels/@me", "Discord"),
        ("http://localhost:3000/", "My app"), ("http://192.168.1.1/", "Router"), ("http://10.0.0.5:8080/status", "Status"),
        ("https://nas.local/", "NAS"), ("https://example.com:8443/", "A port"),
        ("https://example.com/", "Just a moment..."), ("https://example.com/x", "404 Not Found"),
        ("https://example.com/?q=desk", "Results"), ("https://example.com/search/desks", "Results"),
        ("https://example.com/messages", "Messages"), ("https://example.com/notifications", "Notifications"),
        ("https://example.com/inbox", "Inbox"),
        ("https://www.youtube.com/", "YouTube"), ("https://www.reddit.com/", "Reddit"), ("https://x.com/home", "Home / X"),
        ("https://www.facebook.com/", "Facebook"), ("https://www.instagram.com/", "Instagram"),
        ("https://news.google.com/home?hl=en-US", "Google News"), ("https://www.amazon.com/ref=nav_logo", "Amazon"),
        ("https://accounts.google.com/signin/v2", "Sign in - Google Accounts"), ("https://login.microsoftonline.com/common/oauth2", "Sign in"),
        ("https://login.live.com/", "Sign in"), ("https://appleid.apple.com/sign-in", "Apple ID"),
        ("https://northgate.onelogin.com/login", "Sign in"), ("https://dev-123.okta.com/", "Sign In"),
        ("https://tenant.auth0.com/u/login", "Log in"), ("https://sso.example.com/", "Welcome"), ("https://idp.example.com/", "Welcome"),
    ]

    @Test(arguments: mustStayGeneral)
    func hardSkipsStayGeneral(_ raw: String, _ title: String) throws {
        #expect(!PageRules.judge(try #require(LinkCleaner.clean(raw)), title: title).isSpecific, "\(raw)")
    }

    /// What v1.1 refused (utility hosts, sign-in words, depth, generic words) and v1.2 keeps.
    static let nowKept: [(String, String)] = [
        ("https://calendar.google.com/calendar/u/0/r", "Google Calendar"), ("https://chatgpt.com/", "ChatGPT"),
        ("https://claude.ai/", "Claude"), ("https://open.spotify.com/", "Spotify"), ("https://www.netflix.com/", "Netflix"),
        ("https://www.paypal.com/", "PayPal"), ("https://github.com/login", "Sign in to GitHub"), ("https://github.com/new", "New repo"),
        ("https://example.com/", "Sign in"), ("https://example.com/login", "Welcome"), ("https://example.com/a/b/c", "Three deep"),
        ("https://example.com/2026/10", "Archives"), ("https://example.com/privacy", "Privacy"),
        ("https://blackboard.school.org/", "Blackboard"), ("https://school.instructure.com/", "Dashboard"),
        ("https://www.nytimes.com/", "The New York Times"), ("https://www.linkedin.com/", "LinkedIn"),
    ]

    @Test(arguments: nowKept)
    func everythingElseIsKept(_ raw: String, _ title: String) throws {
        #expect(PageRules.judge(try #require(LinkCleaner.clean(raw)), title: title).isSpecific, "\(raw)")
    }

    static let unchanged: [(String, String, Kind)] = [
        ("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Never Gonna Give You Up", .videos),
        ("https://www.amazon.com/Sony-WH-1000XM6/dp/B0F1234567", "Sony WH-1000XM6", .shopping),
        ("https://en.wikipedia.org/wiki/SQLite", "SQLite - Wikipedia", .reading),
        ("https://learn.school.edu/ultra/courses/_12345_1/outline/discussion/_555_1", "Week 3 discussion", .school),
    ]

    @Test(arguments: unchanged)
    func specificPagesKeepTheirKind(_ raw: String, _ title: String, _ kind: Kind) throws {
        let verdict = PageRules.judge(try #require(LinkCleaner.clean(raw)), title: title)
        #expect(verdict.isSpecific && verdict.kind == kind && !verdict.pageType.hasSuffix("/~"), "\(raw)")
    }

    @Test func toolsHostsAndDocsPrefixesAreTools() throws {
        for raw in ["https://gitlab.com/gitlab-org/gitlab", "https://www.npmjs.com/package/left-pad", "https://pypi.org/project/requests/",
                    "https://docs.example.com/guide/getting-started-with-sync", "https://api.example.com/v2/reference-for-widgets"] {
            let verdict = PageRules.judge(try #require(LinkCleaner.clean(raw)), title: "Getting started with sync and widgets")
            #expect(verdict.isSpecific && verdict.kind == .tools, "\(raw)")
        }
    }
}

/// What a keep-everything rule still refuses, and what it keeps that v1.1 refused.
@Suite struct SiteLevelRefusalTests {
    func judge(_ raw: String, _ title: String) throws -> Verdict {
        PageRules.judge(try #require(LinkCleaner.clean(raw)), title: title)
    }

    static let general: [(String, String)] = [
        // files
        ("https://example.com/report.pdf", "Report"), ("https://i.imgur.com/abc.png", "abc"), ("https://example.com/data.json", "data"),
        ("https://example.com/robots.txt", "robots.txt"),
        // more search keys
        ("https://example.com/?s=desk", "Results"), ("https://example.com/?text=desk", "Results"),
        ("https://example.com/?searchterm=desk", "Results"), ("https://example.com/?find=desk", "Results"),
        ("https://www.baidu.com/?wd=desk", "Results"), ("https://example.com/find?wd=desk", "Results"),
        // captive portals and shorteners, even with an ID-like path
        ("http://captive.apple.com/", "Success"), ("http://neverssl.com/", "NeverSSL"),
        ("http://detectportal.firefox.com/", "Portal"), ("http://www.msftconnecttest.com/", "Portal"),
        ("http://connectivitycheck.gstatic.com/", "Portal"),
        ("https://bit.ly/3AbCdEf", "Redirecting"), ("https://t.co/AbCdEfGh12", "t.co"), ("https://tinyurl.com/yabc1234", "x"),
        ("https://goo.gl/AbCdEf12", "x"), ("https://ow.ly/AbCdEf12", "x"), ("https://is.gd/AbCdEf12", "x"),
        ("https://buff.ly/3AbCdEf", "x"), ("https://lnkd.in/AbCdEf12", "x"), ("https://rb.gy/abc123", "x"), ("https://cutt.ly/AbCdEf12", "x"),
        // hosts that exist only to sign in
        ("https://login.example.com/", "Welcome"), ("https://auth.example.com/", "Welcome"), ("https://accounts.example.com/", "Welcome"),
        ("https://sso.example.com/", "Welcome"), ("https://signin.example.com/", "Welcome"),
        ("https://login.example.com/projects/some-long-project-slug", "Some long project slug"),
    ]

    @Test(arguments: general)
    func refused(_ raw: String, _ title: String) throws {
        let verdict = try judge(raw, title)
        #expect(!verdict.isSpecific, "\(raw)")
    }

    /// Shop and account hosts are kept: a checkout or an account page on a shop is a site the user is interested in.
    static let kept: [(String, String)] = [
        ("https://secure.example.com/", "Welcome"), ("https://checkout.example.com/", "Welcome"),
        ("https://billing.example.com/", "Welcome"), ("https://account.example.com/", "Welcome"),
        ("https://pay.example.com/", "Welcome"), ("https://www.apple.com/", "Apple"), ("https://www.microsoft.com/", "Microsoft"),
        ("https://zoom.us/", "Zoom"), ("https://www.live.com/", "Live"), ("https://id.wikipedia.org/wiki/Indonesia", "Indonesia - Wikipedia"), ("https://example.com/index.htm", "Home"),
    ]

    @Test(arguments: kept)
    func keptNowAlthoughV11Refused(_ raw: String, _ title: String) throws {
        #expect(try judge(raw, title).isSpecific, "\(raw)")
    }

    static let oneTimeWords = [
        "oauth", "oauth2", "authorize", "callback", "logout", "signout", "sign-out", "redirect", "unsubscribe", "error", "success",
        "thank-you", "thankyou", "verify", "verify-email", "consent",
    ]

    @Test(arguments: oneTimeWords)
    func oneTimeFlowsAreRefused(_ word: String) throws {
        let shallow = try judge("https://example.com/\(word)", "Welcome")
        let deeper = try judge("https://example.com/\(word)/step", "Welcome")
        #expect(!shallow.isSpecific && !deeper.isSpecific, "\(word)")
    }

    /// V1.1 refused these words; they are sign-in, account and payment pages of a site, which v1.2 keeps.
    static let accountWords = [
        "auth", "activate", "password", "reset", "forgot", "out", "go", "pay", "payment", "order", "confirm", "wp-admin",
        "login", "sign-up", "signin", "account", "cart", "checkout", "careers", "about", "settings", "billing",
    ]

    @Test(arguments: accountWords)
    func accountAndPaymentPagesAreKept(_ word: String) throws {
        let shallow = try judge("https://example.com/\(word)", "Welcome")
        let deeper = try judge("https://example.com/\(word)/step", "Welcome")
        #expect(shallow.isSpecific && deeper.isSpecific, "\(word)")
    }

    static let titles = [
        "Attention Required! | Cloudflare", "Verify you are human", "Checking your browser before accessing", "502 Bad Gateway",
        "403 Forbidden", "Service Unavailable", "Captcha", "Privacy error", "Just a moment...",
        "Access denied", "404 Not Found", "New Tab", "Untitled", "Redirecting...",
    ]

    @Test(arguments: titles)
    func theseTitlesAreInTheWay(_ title: String) throws {
        let verdict = try judge("https://example.com/", title)
        #expect(!verdict.isSpecific, "\(title)")
    }

    /// Pages that merely mention a challenge or error word are real pages: only a short title that starts with the phrase is in the way.
    @Test(arguments: [
        "The Forbidden City – Wikipedia", "How CAPTCHA works", "Route 404 Podcast", "Why a service unavailable error happens",
        "Page not found pages: a design history of the web", "Notes on 404 Not Found pages for editors",
    ])
    func titlesThatOnlyMentionAnErrorWordAreKept(_ title: String) throws {
        #expect(try judge("https://example.com/p/article", title).isSpecific, "\(title)")
    }

    @Test(arguments: [
        "403 Forbidden", "404 Not Found", "404 - Page Not Found | Example", "Page not found", "Access denied | Example",
        "Just a moment…", "Attention Required! | Cloudflare", "Verify you are human", "Checking your browser before accessing",
        "502 Bad Gateway", "Service Unavailable", "Privacy error", "Not Found – Example",
    ])
    func shortErrorAndChallengeTitlesAreInTheWay(_ title: String) throws {
        #expect(!(try judge("https://example.com/p/article", title).isSpecific), "\(title)")
    }

    /// A sign-in page is a page of a site the user follows, so its title doesn't keep it out.
    @Test(arguments: ["Sign in", "Log in to Example", "Sign up | Example"])
    func signInTitlesAreKept(_ title: String) throws {
        #expect(try judge("https://example.com/login", title).isSpecific, "\(title)")
    }

    @Test(arguments: ["Product catalog in Berlin", "Untitled UI — design system", "Blogging about prelogin flows"])
    func titleWordsAreWholeWordsNotSubstrings(_ title: String) throws {
        // "catalog in" holds "log in", "Blogging" holds "log"; neither is a sign-in wall. "Untitled UI" is a real page.
        let verdict = try judge("https://example.com/", title)
        #expect(verdict.isSpecific && verdict.kind == .tools, "\(title)")
    }

    @Test func specificPagesOnVendorHostsKeepTheirVerdicts() throws {
        for (raw, title) in [("https://support.apple.com/en-us/HT201222", "Use Touch ID on your Mac"),
                             ("https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blobs-introduction", "Introduction to Blob storage"),
                             ("https://zoom.us/docs/doc/getting-started-with-zoom-meetings", "Getting started with Zoom meetings")] {
            let verdict = try judge(raw, title)
            #expect(verdict.isSpecific, "\(raw)")
        }
    }

    @Test func githubIoPostsAreReadingAndItsHomeIsStillTools() throws {
        for raw in ["https://someone.github.io/posts/how-i-built-my-blog", "https://someone.github.io/2026/10/my-first-post"] {
            let verdict = try judge(raw, "How I built my blog")
            #expect(verdict.isSpecific && verdict.kind == .reading, "\(raw)")
        }
        let home = try judge("https://someone.github.io/", "Someone")
        #expect(home.isSpecific && home.kind == .tools)
    }

    @Test func aPDFWithARealNameIsReading() throws {
        let verdict = try judge("https://education.github.com/git-cheat-sheet-education.pdf", "GIT CHEAT SHEET")
        #expect(verdict.isSpecific && verdict.kind == .reading)
    }
}

/// The user's own tabs from the v1.2 investigation: kept, and sorted by what the page is.
@Suite struct KeepEverythingTests {
    func judge(_ raw: String, _ title: String, feeds: Bool = false) throws -> Verdict {
        PageRules.judge(try #require(LinkCleaner.clean(raw)), title: title, savesFeedHomes: feeds)
    }

    static let kept: [(String, String, Kind)] = [
        ("https://console.acme.example.com/login?returnTo=%2Fdashboard", "Sign in | Acme Console", .tools),
        ("https://app.example.com/sign-up?redirect_url=https%3A%2F%2Fapp.example.com%2Fdashboard", "Sign up - Example App", .tools),
        ("https://arcteryx.com/us/en/cart", "Cart | Arc'teryx", .shopping),
        ("https://arcteryx.com/", "Arc'teryx", .tools),
        ("https://gradescope.com/courses/123", "CS 101", .school),
        ("https://blackboard.northgate.edu/", "Blackboard Learn", .school),
        ("https://my.northgate.edu/ics", "Northgate University", .school),
        ("https://example.com/careers", "Careers | Example", .tools),
        ("https://www.amazon.com/gp/cart/view.html", "Amazon.com Shopping Cart", .shopping),
        ("https://shop.example.com/checkout", "Checkout", .shopping),
        // v1.1's examples, still kept
        ("https://linkvault.example.net/video", "Link Vault", .tools), ("https://github.com/octocat", "octocat", .tools),
        ("https://brightlane.example.com/", "Brightlane", .tools), ("https://quill.example.com/", "Quill", .tools),
    ]

    @Test(arguments: kept)
    func theseTabsAreKept(_ raw: String, _ title: String, _ kind: Kind) throws {
        let verdict = try judge(raw, title)
        #expect(verdict.isSpecific && verdict.kind == kind, "\(raw)")
    }

    @Test(arguments: [
        "https://www.youtube.com/", "https://x.com/home", "https://x.com/", "https://www.reddit.com/", "https://www.facebook.com/",
        "https://www.instagram.com/", "https://news.google.com/home", "https://www.amazon.com/", "https://www.amazon.com/ref=nav_logo",
    ])
    func feedHomesAreKeptOnlyWhenAsked(_ raw: String) throws {
        #expect(!(try judge(raw, "Home")).isSpecific, "\(raw)")
        #expect((try judge(raw, "Home", feeds: true)).isSpecific, "\(raw)")
    }

    @Test func aVideoOrAPostIsNotAFeedHome() throws {
        #expect(try judge("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "A video").isSpecific)
        #expect(try judge("https://x.com/karpathy/status/1890000000000000000", "A post").isSpecific)
    }

    @Test func aFeedHomeCorrectionStillWins() throws {
        let url = try #require(LinkCleaner.clean("https://www.youtube.com/"))
        let type = PageRules.judge(url, title: "YouTube", savesFeedHomes: true).pageType
        #expect(!PageRules.judge(url, title: "YouTube", corrections: [type: Correction(isSpecific: false)], savesFeedHomes: true).isSpecific)
    }
}
