namespace Later.Core;

/// A made-up list for the app's sample mode, the Mac's `Sample.swift` page for page: fictional and well-known public hosts only.
/// Built through `Library`'s own calls, so the items are shaped like real saves. Also what the measurements run on.
public static class SampleLibrary
{
    sealed record Page(Kind Kind, string Url, string Title, double DaysAgo, int Visits, bool Done = false);
    sealed record Place(string Url, string Title, double DaysAgo, int Days);

    static readonly Page[] Pages =
    [
        new(Kind.Shopping, "https://www.rei.com/product/229153/patagonia-nano-puff-jacket-mens", "Patagonia Nano Puff Jacket - Men's | REI Co-op", 0.05, 3),
        new(Kind.Reading, "https://www.theatlantic.com/technology/archive/2024/03/slow-web-movement/677712/", "The Case for a Slower, Quieter, More Deliberate Internet That Respects Your Attention", 0.2, 1),
        new(Kind.Videos, "https://www.youtube.com/watch?v=aircAruvnKk", "But what is a neural network? | Deep learning, chapter 1", 0.4, 2),
        new(Kind.Posts, "https://news.ycombinator.com/item?id=39223456", "Show HN: A tiny SQLite-backed task queue", 0.6, 1),
        new(Kind.School, "https://ocw.mit.edu/courses/18-06-linear-algebra-spring-2010/pages/syllabus/", "Syllabus | Linear Algebra | Mathematics | MIT OpenCourseWare", 1.1, 4),
        new(Kind.Shopping, "https://www.bhphotovideo.com/c/product/1759417-REG/sony_ilce7cm2_b_alpha_a7c_ii_mirrorless.html", "Sony a7C II Mirrorless Camera (Black)", 1.3, 2),
        new(Kind.Reading, "https://en.wikipedia.org/wiki/Byzantine_fault", "Byzantine fault - Wikipedia", 1.6, 1, true),
        new(Kind.Posts, "https://www.reddit.com/r/MechanicalKeyboards/comments/1b2c3d4/finally_finished_my_first_build/", "Finally finished my first build after three months of waiting for switches, keycaps and a case that kept getting delayed", 2.5, 1),
        new(Kind.Other, "https://www.google.com/maps/place/Golden+Gate+Park/@37.7694,-122.4862,14z", "Golden Gate Park - Google Maps", 3.2, 2),
        new(Kind.School, "https://www.khanacademy.org/math/calculus-1/cs1-derivatives-definition-and-basic-rules", "Derivatives: definition and basic rules | Calculus 1 | Khan Academy", 3.8, 6),
        new(Kind.Videos, "https://vimeo.com/76979871", "The New Vimeo Player (You Know, For Videos)", 4.5, 1),
        new(Kind.Reading, "https://www.nytimes.com/2024/02/14/well/eat/sourdough-starter-tips.html", "How to Keep a Sourdough Starter Alive (Even When You Forget About It)", 5.1, 2),
        new(Kind.Shopping, "https://www.ikea.com/us/en/p/kallax-shelf-unit-white-80275887/", "KALLAX Shelf unit, white, 30 3/8x57 7/8\" - IKEA", 6.3, 5, true),
        new(Kind.Posts, "https://mastodon.social/@ivory/111234567890123456", "Ivory for Mac is out of beta today", 9, 1),
        new(Kind.School, "https://www.coursera.org/learn/machine-learning/lecture/db3jS/what-is-machine-learning", "What is Machine Learning? - Supervised Machine Learning: Regression and Classification | Coursera", 12, 3),
        new(Kind.Reading, "https://developer.apple.com/documentation/swiftui/managing-model-data-in-your-app", "Managing model data in your app | Apple Developer Documentation", 15, 7),
        new(Kind.Videos, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Rick Astley - Never Gonna Give You Up (Official Music Video)", 18, 1, true),
        new(Kind.Tools, "https://github.com/apple/swift-async-algorithms", "GitHub - apple/swift-async-algorithms: Async algorithms for Swift", 0.8, 2),
        new(Kind.Tools, "https://quill.example.com/", "Quill - a quiet, fast code editor", 2.0, 1),
        new(Kind.Tools, "https://linkvault.example.net/video", "Video - Link Vault", 7, 1),
        new(Kind.Other, "https://www.airbnb.com/rooms/12345678", "Cabin in Big Sur · ★4.92 · 2 bedrooms · 3 beds · 1 bath", 21, 2),
        new(Kind.Shopping, "https://www.amazon.com/dp/B0BDHWDR12", "Apple AirPods Pro (2nd Generation) Wireless Ear Buds with USB-C Charging", 26, 3),
        new(Kind.Posts, "https://bsky.app/profile/jay.bsky.team/post/3kabcdefg2x2a", "Post by Jay on Bluesky", 29, 1),
        new(Kind.School, "https://learn.northgate.edu/ultra/courses/_48213_1/discussion", "Discussion", 0.3, 4),
        new(Kind.School, "https://learn.northgate.edu/ultra/courses/_51877_1/discussion", "Discussion", 1.0, 2),
        new(Kind.School, "https://learn.northgate.edu/ultra/courses/_50102_1/discussion", "Discussion", 2.2, 3),
        new(Kind.School, "https://learn.northgate.edu/ultra/courses/organic-chemistry-laboratory-section-b/gradebook", "Gradebook", 0.7, 2),
        new(Kind.School, "https://learn.northgate.edu/ultra/courses/introduction-to-macroeconomics/gradebook", "Gradebook", 1.4, 5),
        new(Kind.Reading, "https://www.newyorker.com/magazine/2023/11/20/the-long-history-of-the-paperback", "The Long History of the Paperback", 45, 2),
        new(Kind.School, "https://www.chegg.com/flashcards/organic-chemistry-reactions-9a8b7c6d", "Organic Chemistry Reactions Flashcards", 60, 1),
        new(Kind.Videos, "https://www.twitch.tv/videos/2012345678", "Speedrun practice - any% glitchless", 75, 1),
        new(Kind.Other, "https://www.allrecipes.com/recipe/10813/best-chocolate-chip-cookies/", "Best Chocolate Chip Cookies Recipe", 110, 4),
        new(Kind.Shopping, "https://www.etsy.com/listing/1234567890/handmade-ceramic-coffee-mug", "Handmade Ceramic Coffee Mug, Speckled Stoneware, 12 oz", 160, 1),
    ];

    /// Home pages and dashboards, with the days of the last 28 the sample's history says the site was visited: four or more makes a regular.
    static readonly Place[] Places =
    [
        new("https://github.com/", "GitHub", 0.1, 24),
        new("https://news.ycombinator.com/", "Hacker News", 0.3, 19),
        new("https://www.reddit.com/", "reddit: the front page of the internet", 0.9, 14),
        new("https://www.theatlantic.com/", "The Atlantic", 2.2, 6),
        new("https://tinytools.example.net/", "Tiny Tools: small utilities for everyday jobs", 1.4, 1),
    ];

    /// The sample's clock: 12:00 local time on the day of `day`, so the counts do not depend on the hour or time zone it is run in.
    public static DateTime NoonUtc(DateTime day) => new DateTime(day.Year, day.Month, day.Day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();

    static DateTime Ago(DateTime now, double days) => now.AddSeconds(-days * 86_400);

    /// What the sample's browser history says: on which of the last 28 days each place's site was visited.
    static Dictionary<string, HashSet<int>> VisitDays(DateTime now)
    {
        int today = Familiarity.Today(now);
        return Places.ToDictionary(p => Library.HostOf(p.Url), p => Enumerable.Range(1, p.Days).Select(i => today - i).ToHashSet());
    }

    public static LibraryData Data(DateTime now)
    {
        var library = new Library();
        foreach (var page in Pages)
        {
            var when = Ago(now, page.DaysAgo);
            var host = Library.HostOf(page.Url);
            library.Save(page.Url, page.Title, new Verdict(true, page.Kind, host + "/*"), Browser.Helium, when);
            for (int v = 1; v < page.Visits; v++) library.RecordVisit(page.Url, when);
            if (page.Done) library.SetDone(library.Data.Items[^1].Id, true, when.AddHours(1));
        }
        library.SetHistoryDays(VisitDays(now));
        foreach (var place in Places)
        {
            var host = Library.HostOf(place.Url);
            library.Save(place.Url, place.Title, new Verdict(true, Kind.Tools, host + "/~", IsPlace: true), Browser.Helium, Ago(now, place.DaysAgo));
        }
        // What Helium's history would add, for most pages: when each was first opened, earlier than Later saw it, and a visit
        // count; and the places of the pages in open tabs, across two windows. Every fourth page has no history.
        var history = new Dictionary<string, HistoryResult>();
        var placed = new List<(string Url, int Tab, int Window)>();
        int[] next = [0, 0];
        for (int i = 0; i < Pages.Length; i++)
        {
            var page = Pages[i];
            if (i % 4 != 3)
            {
                double earlier = i % 5 == 0 ? 0.01 : (i * 7) % 40 + 2;
                history[page.Url] = new HistoryResult(Ago(now, page.DaysAgo + earlier), page.Visits + (i % 3 == 0 ? 6 : 0));
            }
            if (i % 2 == 0 || page.Url.Contains("northgate", StringComparison.Ordinal) || page.Url.Contains("linkvault", StringComparison.Ordinal))
            {
                int window = page.Url.Contains("northgate", StringComparison.Ordinal) || i % 6 >= 3 ? 1 : 0;
                placed.Add((page.Url, next[window], window));
                next[window]++;
            }
        }
        library.ApplyHistory(history);
        library.SetPlaces(placed);
        return library.Data;
    }

    /// The tabs open for each page, by page key: one for every page placed in a window, two for one of them.
    public static IReadOnlyDictionary<string, int> OpenCopies(Library library)
    {
        var copies = new Dictionary<string, int>();
        foreach (var item in library.Data.Items.Where(i => i.TabIndex is not null)) copies[library.PageKey(item.Url)] = 1;
        if (library.Data.Items.FirstOrDefault(i => i.Host == "linkvault.example.net") is { } twice) copies[library.PageKey(twice.Url)] = 2;
        return copies;
    }
}
