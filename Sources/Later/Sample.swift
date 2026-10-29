import AppKit
import LaterCore

/// Development only: `--sample` shows a made-up list, as if already onboarded; `--sample-onboarding` shows the
/// welcome window first. Every `--sample…` flag means a sample run, alone or not: no browser is watched or read, no Apple
/// Events are sent, Helium's icon cache isn't opened, and nothing is saved, not even a setting.
/// Compiled out of release builds, where `enabled` is always false.
enum Sample {
    #if DEBUG
    static let onboarding = CommandLine.arguments.contains("--sample-onboarding")
    /// `--sample-many` adds 1,500 more pages, for timing a long list.
    static let many = CommandLine.arguments.contains("--sample-many")
    static let enabled = CommandLine.arguments.contains { $0.hasPrefix("--sample") }
    /// `--sample-select N` selects the first N pages when the window opens, for screenshots of the inspector.
    static let select = value(after: "--sample-select").flatMap(Int.init) ?? 0
    /// `--sample-section shopping` (or all, older, done, any kind) opens on that section.
    static let section = value(after: "--sample-section").flatMap { name in
        LibrarySection.ordered.first { $0.title.lowercased() == name.lowercased() }
    }
    /// `--sample-group site` (or date, kind) groups the list that way.
    static let grouping = value(after: "--sample-group").flatMap(LibraryGrouping.init(rawValue:))
    /// `--sample-sort tabOrder` (or tabOrderReversed, dateOpened, dateSaved, lastSeen, title, site) orders the list that way;
    /// `--sample-reversed` flips dates and text.
    static let sort = value(after: "--sample-sort").flatMap(LibrarySort.init(rawValue:))
    static let sortReversed = CommandLine.arguments.contains("--sample-reversed")
    /// `--sample-search text` opens with that search.
    static let search = value(after: "--sample-search")
    /// `--sample-chrome needs|closed` shows Chrome as needing permission or as not open (default: watched); Helium is
    /// always watched. The state is only a drawing: no browser is read.
    static var statuses: [Browser: BrowserStatus] {
        let chrome: BrowserStatus = switch value(after: "--sample-chrome") {
        case "needs": .needsPermission
        case "closed": .notRunning
        default: .watching
        }
        return [.helium: .watching, .chrome: chrome]
    }
    /// `--sample-settings` opens Settings too.
    static let settings = CommandLine.arguments.contains("--sample-settings")

    private static func value(after flag: String) -> String? {
        CommandLine.arguments.firstIndex(of: flag).flatMap { CommandLine.arguments.dropFirst($0 + 1).first }
    }
    /// `--no-window` starts like a login launch, with no window, to time the first open (show it with
    /// `notifyutil -p com.ryan.later.dev.show`).
    static let noWindow = CommandLine.arguments.contains("--no-window")
    /// `--sample-key` draws the list's selection as it looks in the key window, for screenshots of a window in the background.
    static let emphasized = CommandLine.arguments.contains("--sample-key")
    /// `--sample-dark` draws the whole app in the dark appearance, whatever the Mac is set to, for screenshots.
    static let dark = CommandLine.arguments.contains("--sample-dark")
    /// `--sample-origin x,y` puts the window's top-left corner there (screen coordinates from the top left), so a test
    /// copy's window opens straight on its agent screen.
    static let origin = value(after: "--sample-origin").flatMap { text -> NSPoint? in
        let parts = text.split(separator: ",").compactMap { Double($0) }
        return parts.count == 2 ? NSPoint(x: parts[0], y: parts[1]) : nil
    }

    /// (kind, link, title, days ago, visits, done)
    private static let pages: [(Kind, String, String, Double, Int, Bool)] = [
        (.shopping, "https://www.rei.com/product/229153/patagonia-nano-puff-jacket-mens", "Patagonia Nano Puff Jacket - Men's | REI Co-op", 0.05, 3, false),
        (.reading, "https://www.theatlantic.com/technology/archive/2024/03/slow-web-movement/677712/", "The Case for a Slower, Quieter, More Deliberate Internet That Respects Your Attention", 0.2, 1, false),
        (.videos, "https://www.youtube.com/watch?v=aircAruvnKk", "But what is a neural network? | Deep learning, chapter 1", 0.4, 2, false),
        (.posts, "https://news.ycombinator.com/item?id=39223456", "Show HN: A tiny SQLite-backed task queue", 0.6, 1, false),
        (.school, "https://ocw.mit.edu/courses/18-06-linear-algebra-spring-2010/pages/syllabus/", "Syllabus | Linear Algebra | Mathematics | MIT OpenCourseWare", 1.1, 4, false),
        (.shopping, "https://www.bhphotovideo.com/c/product/1759417-REG/sony_ilce7cm2_b_alpha_a7c_ii_mirrorless.html", "Sony a7C II Mirrorless Camera (Black)", 1.3, 2, false),
        (.reading, "https://en.wikipedia.org/wiki/Byzantine_fault", "Byzantine fault - Wikipedia", 1.6, 1, true),
        (.posts, "https://www.reddit.com/r/MechanicalKeyboards/comments/1b2c3d4/finally_finished_my_first_build/", "Finally finished my first build after three months of waiting for switches, keycaps and a case that kept getting delayed", 2.5, 1, false),
        (.other, "https://www.google.com/maps/place/Golden+Gate+Park/@37.7694,-122.4862,14z", "Golden Gate Park - Google Maps", 3.2, 2, false),
        (.school, "https://www.khanacademy.org/math/calculus-1/cs1-derivatives-definition-and-basic-rules", "Derivatives: definition and basic rules | Calculus 1 | Khan Academy", 3.8, 6, false),
        (.videos, "https://vimeo.com/76979871", "The New Vimeo Player (You Know, For Videos)", 4.5, 1, false),
        (.reading, "https://www.nytimes.com/2024/02/14/well/eat/sourdough-starter-tips.html", "How to Keep a Sourdough Starter Alive (Even When You Forget About It)", 5.1, 2, false),
        (.shopping, "https://www.ikea.com/us/en/p/kallax-shelf-unit-white-80275887/", "KALLAX Shelf unit, white, 30 3/8x57 7/8\" - IKEA", 6.3, 5, true),
        (.posts, "https://mastodon.social/@ivory/111234567890123456", "Ivory for Mac is out of beta today", 9, 1, false),
        (.school, "https://www.coursera.org/learn/machine-learning/lecture/db3jS/what-is-machine-learning", "What is Machine Learning? - Supervised Machine Learning: Regression and Classification | Coursera", 12, 3, false),
        (.reading, "https://developer.apple.com/documentation/swiftui/managing-model-data-in-your-app", "Managing model data in your app | Apple Developer Documentation", 15, 7, false),
        (.videos, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Rick Astley - Never Gonna Give You Up (Official Music Video)", 18, 1, true),
        (.tools, "https://github.com/apple/swift-async-algorithms", "GitHub - apple/swift-async-algorithms: Async algorithms for Swift", 0.8, 2, false),
        (.tools, "https://quill.example.com/", "Quill - a quiet, fast code editor", 2.0, 1, false),
        (.tools, "https://linkvault.example.net/video", "Video - Link Vault", 7, 1, false),
        (.other, "https://www.airbnb.com/rooms/12345678", "Cabin in Big Sur · ★4.92 · 2 bedrooms · 3 beds · 1 bath", 21, 2, false),
        (.shopping, "https://www.amazon.com/dp/B0BDHWDR12", "Apple AirPods Pro (2nd Generation) Wireless Ear Buds with USB-C Charging", 26, 3, false),
        (.posts, "https://bsky.app/profile/jay.bsky.team/post/3kabcdefg2x2a", "Post by Jay on Bluesky", 29, 1, false),
        (.school, "https://learn.northgate.edu/ultra/courses/_48213_1/discussion", "Discussion", 0.3, 4, false),
        (.school, "https://learn.northgate.edu/ultra/courses/_51877_1/discussion", "Discussion", 1.0, 2, false),
        (.school, "https://learn.northgate.edu/ultra/courses/_50102_1/discussion", "Discussion", 2.2, 3, false),
        (.school, "https://learn.northgate.edu/ultra/courses/organic-chemistry-laboratory-section-b/gradebook", "Gradebook", 0.7, 2, false),
        (.school, "https://learn.northgate.edu/ultra/courses/introduction-to-macroeconomics/gradebook", "Gradebook", 1.4, 5, false),
        (.reading, "https://www.newyorker.com/magazine/2023/11/20/the-long-history-of-the-paperback", "The Long History of the Paperback", 45, 2, false),
        (.school, "https://www.chegg.com/flashcards/organic-chemistry-reactions-9a8b7c6d", "Organic Chemistry Reactions Flashcards", 60, 1, false),
        (.videos, "https://www.twitch.tv/videos/2012345678", "Speedrun practice - any% glitchless", 75, 1, false),
        (.other, "https://www.allrecipes.com/recipe/10813/best-chocolate-chip-cookies/", "Best Chocolate Chip Cookies Recipe", 110, 4, false),
        (.shopping, "https://www.etsy.com/listing/1234567890/handmade-ceramic-coffee-mug", "Handmade Ceramic Coffee Mug, Speckled Stoneware, 12 oz", 160, 1, false),
    ]

    /// (link, title, days ago, days of the last 28 the site was visited): home pages and dashboards, which the sample's
    /// history makes regulars when visited on four or more.
    private static let places: [(String, String, Double, Int)] = [
        ("https://github.com/", "GitHub", 0.1, 24),
        ("https://news.ycombinator.com/", "Hacker News", 0.3, 19),
        ("https://www.reddit.com/", "reddit: the front page of the internet", 0.9, 14),
        ("https://www.theatlantic.com/", "The Atlantic", 2.2, 6),
        ("https://tinytools.example.net/", "Tiny Tools: small utilities for everyday jobs", 1.4, 1),
    ]

    /// What the sample's browser history says: how many of the last 28 days each place's site was visited.
    @MainActor static func visitDays(now: Date = .now) -> [String: Set<Int>] {
        let today = Familiarity.dayNumber(of: now)
        return Dictionary(uniqueKeysWithValues: places.map { place in
            (Library.host(of: place.0), Set((1...place.3).map { today - $0 }))
        })
    }

    /// Nil without `--sample`. Built through `Library`'s own calls, so the items are shaped like real saves.
    @MainActor static func data(now: Date = .now) -> LibraryData? {
        guard enabled else { return nil }
        let library = Library()
        for (kind, url, title, daysAgo, visits, done) in pages {
            let when = now.addingTimeInterval(-daysAgo * 86_400)
            let host = Library.host(of: url)
            library.save(url: url, title: title, verdict: Verdict(isSpecific: true, kind: kind, pageType: host + "/*"),
                         browser: .helium, now: when)
            for _ in 1..<visits { library.recordVisit(url: url, now: when) }
            if done, let item = library.data.items.last { library.setDone(item.id, true, now: when.addingTimeInterval(3_600)) }
        }
        library.setHistoryDays(visitDays(now: now))
        for (url, title, daysAgo, _) in places {
            let host = Library.host(of: url)
            library.save(url: url, title: title, verdict: Verdict(isSpecific: true, kind: .tools, pageType: host + "/~", isPlace: true),
                         browser: .helium, now: now.addingTimeInterval(-daysAgo * 86_400))
        }
        // What Helium's history would add, for most pages: when each was first opened, earlier than Later saw it, and a
        // visit count; and the places of the pages in open tabs, across two windows. Every fourth page has no history.
        var history: [String: HistoryLookup.Result] = [:], places: [(url: String, tab: Int, window: Int)] = []
        var next = [0, 0]
        for (i, page) in pages.enumerated() {
            if i % 4 != 3 {
                let earlier = i % 5 == 0 ? 0.01 : Double((i * 7) % 40 + 2)
                history[page.1] = HistoryLookup.Result(firstVisit: now.addingTimeInterval(-(page.3 + earlier) * 86_400),
                                                       visitCount: page.4 + (i % 3 == 0 ? 6 : 0))
            }
            if i % 2 == 0 || page.1.contains("northgate") || page.1.contains("linkvault") {
                let window = page.1.contains("northgate") || i % 6 >= 3 ? 1 : 0
                places.append((page.1, next[window], window))
                next[window] += 1
            }
        }
        library.applyHistory(history)
        library.setPlaces(places)
        if many {
            let words = ["Walnut", "Linen", "Ceramic", "Notes", "Lecture", "Review", "Guide", "Thread", "Trailer", "Recipe"]
            for i in 0..<1_500 {
                let host = "site\(i % 140).example.com", kind = Kind.allCases[i % Kind.allCases.count]
                let when = now.addingTimeInterval(-Double(i) * 21_000)   // about a year in all
                library.save(url: "https://\(host)/page/\(i)", title: "\(words[i % words.count]) \(kind.title.lowercased()) page \(i)",
                             verdict: Verdict(isSpecific: true, kind: kind, pageType: host + "/page/*"), browser: .helium, now: when)
            }
        }
        return library.data
    }

    /// The tabs open for each page: one for every page placed in a window, two for one of them.
    @MainActor static func openCopies(_ library: Library) -> [String: Int] {
        var copies: [String: Int] = [:]
        for item in library.data.items where item.tabIndex != nil { copies[library.pageKey(of: item.url)] = 1 }
        if let twice = library.data.items.first(where: { $0.host == "linkvault.example.net" }) {
            copies[library.pageKey(of: twice.url)] = 2
        }
        return copies
    }

    /// Stand-ins for some sample sites' favicons, drawn at the sizes Helium keeps them (16, 32 and 64 px), since a sample
    /// run never opens Helium's cache: thin gray marks, a red mark, a tiny filled square and large colored badges.
    @MainActor static func iconPNGs() -> [String: Data] {
        func png(_ pixels: Int, _ draw: (CGFloat) -> Void) -> Data? {
            guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
                                                samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                                bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
            draw(CGFloat(pixels))
            NSGraphicsContext.restoreGraphicsState()
            return bitmap.representation(using: .png, properties: [:])
        }
        func symbol(_ name: String, _ color: NSColor, fill: CGFloat, _ side: CGFloat) {
            guard let image = NSImage(systemSymbolName: name, accessibilityDescription: nil)?
                .withSymbolConfiguration(NSImage.SymbolConfiguration(paletteColors: [color])) else { return }
            let scale = side * fill / max(image.size.width, image.size.height)
            let size = NSSize(width: image.size.width * scale, height: image.size.height * scale)
            image.draw(in: NSRect(x: (side - size.width) / 2, y: (side - size.height) / 2, width: size.width, height: size.height))
        }
        func letter(_ text: String, _ font: NSFont, _ color: NSColor, _ side: CGFloat) {
            let string = NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: color])
            let size = string.size()
            string.draw(at: NSPoint(x: (side - size.width) / 2, y: (side - size.height) / 2))
        }
        func disc(_ color: NSColor, _ side: CGFloat, square: Bool = false) {
            color.setFill()
            (square ? NSBezierPath(rect: NSRect(x: 0, y: 0, width: side, height: side))
                : NSBezierPath(ovalIn: NSRect(x: 0, y: 0, width: side, height: side))).fill()
        }
        let serif = NSFont.systemFont(ofSize: 26).fontDescriptor.withDesign(.serif).flatMap { NSFont(descriptor: $0, size: 26) }
        let icons: [String: Data?] = [
            "rei.com": png(32) { symbol("mountain.2.fill", NSColor(white: 0.36, alpha: 1), fill: 0.66, $0) },
            "youtube.com": png(32) { symbol("play.rectangle.fill", .systemRed, fill: 0.9, $0) },
            "news.ycombinator.com": png(16) { side in
                disc(.systemOrange, side, square: true)
                letter("Y", .boldSystemFont(ofSize: 12), .white, side)
            },
            "reddit.com": png(64) { side in
                disc(.systemOrange, side)
                symbol("face.smiling.inverse", .white, fill: 0.6, side)
            },
            "en.wikipedia.org": png(32) { letter("W", serif ?? .systemFont(ofSize: 26), .black, $0) },
            "developer.apple.com": png(64) { symbol("apple.logo", .black, fill: 0.8, $0) },
        ]
        return icons.compactMapValues { $0 }
    }
    #else
    static let onboarding = false
    static let enabled = false
    static let noWindow = false
    static let emphasized = false
    #endif
}
