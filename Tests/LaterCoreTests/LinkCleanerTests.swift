import Testing
@testable import LaterCore

@Suite struct LinkCleanerTests {
    @Test(arguments: [
        ("https://example.com/post/how-to-build-things?utm_source=x&fbclid=1&id=7",
         "https://example.com/post/how-to-build-things?id=7"),
        ("https://www.amazon.com/Sony-WH-1000XM6-Headphones/dp/B0F1234567/ref=sr_1_3?crid=AB&keywords=sony&qid=1&sr=8-3&th=1",
         "https://www.amazon.com/dp/B0F1234567"),
        ("https://www.amazon.co.uk/gp/product/b0f1234567?psc=1", "https://www.amazon.co.uk/dp/B0F1234567"),
        ("https://www.amazon.com/s?k=standing+desk&ref=nb_sb_noss", "https://www.amazon.com/s?k=standing+desk"),
        ("https://x.com/karpathy/status/1890000000000000000?s=20&t=abc", "https://x.com/karpathy/status/1890000000000000000"),
        ("https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=xyz&pp=1", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
        ("https://en.wikipedia.org/wiki/SQLite#History", "https://en.wikipedia.org/wiki/SQLite"),
        ("https://app.example.com/#/projects/42", "https://app.example.com/#/projects/42"),
        ("https://app.example.com/#/search%3Fq=desk", "https://app.example.com/#/search%3Fq=desk"),
        ("https://app.example.com/#%2Fprojects/42", "https://app.example.com/#%2Fprojects/42"),
        ("https://app.example.com/#%21/inbox", "https://app.example.com/#%21/inbox"),
        ("HTTPS://Example.COM:443/A", "https://example.com/A"),
        ("https://example.com", "https://example.com/"),
        ("https://example.com/search?q=C%2B%2B&utm_source=x", "https://example.com/search?q=C%2B%2B"),
        ("https://example.com/search?keywords=sony", "https://example.com/search?keywords=sony"),
        ("https://amazon.foo.io/guides/dp/B0F1234567?edition=2", "https://amazon.foo.io/guides/dp/B0F1234567?edition=2"),
    ])
    func cleans(_ raw: String, _ expected: String) {
        #expect(LinkCleaner.clean(raw)?.absoluteString == expected)
    }

    @Test(arguments: [
        "chrome://settings",
        "file:///Users/someone/a.pdf",
        "about:blank",
        "https://user:pw@example.com/",
        "https://example.com/login/callback?code=abc",
        "https://example.com/reset?token=abc",
        "https://example.com/files?key=abc",
        "https://example.com/a?auth_token=private",
        "https://example.com/a?X-Amz-Signature=private",
        "https://example.com/a?session_id=private",
        "https://example.com/a?api-key=private",
        "https://app.example.com/#/login/callback?token=private",
        "https://app.example.com/#%2Flogin?token=x",
        "https://app.example.com/#/login%3Ftoken=x",
        "https://app.example.com/#%2Flogin%3Ftoken=x",
        "https://app.example.com/#!/cb%3Fauth_token=x",
    ])
    func refusesPagesThatMustNeverBeSaved(_ raw: String) {
        #expect(LinkCleaner.clean(raw) == nil)
    }

    @Test func encodedRouteFragmentsStayDistinct() {
        #expect(LinkCleaner.clean("https://app.example.com/#%2Fprojects/42")
            != LinkCleaner.clean("https://app.example.com/#%2Fprojects/43"))
    }

    @Test func recognizesStorefronts() {
        #expect(LinkCleaner.isStorefront("www.amazon.co.uk", name: "amazon"))
        #expect(LinkCleaner.isStorefront("amazon.com", name: "amazon"))
        #expect(!LinkCleaner.isStorefront("amazonaws.com", name: "amazon"))
        #expect(!LinkCleaner.isStorefront("amazon.example.community", name: "amazon"))
        #expect(!LinkCleaner.isStorefront("amazon.foo.io", name: "amazon"))
        #expect(!LinkCleaner.isStorefront("notamazon.com", name: "amazon"))
        #expect(LinkCleaner.isStorefront("smile.amazon.com", name: "amazon"))
    }
}

/// The page key: one string per page, however its link was written.
@Suite struct PageKeyTests {
    func key(_ raw: String) throws -> String { LinkCleaner.pageKey(try #require(LinkCleaner.clean(raw))) }

    /// Links that are the same page.
    static let same: [(String, String)] = [
        ("https://www.example.com/a/b", "https://example.com/a/b"),
        ("http://example.com/a/b", "https://example.com/a/b"),
        ("https://example.com/a/b/", "https://example.com/a/b"),
        ("https://example.com//a///b", "https://example.com/a/b"),
        ("https://Example.COM/a/b", "https://example.com/a/b"),
        ("https://example.com/a?b=2&a=1", "https://example.com/a?a=1&b=2"),
        ("https://example.com/a?utm_source=x&id=7", "https://example.com/a?id=7"),
        ("https://example.com", "https://example.com/"),
        ("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s&feature=shared", "https://youtube.com/watch?v=dQw4w9WgXcQ"),
        ("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLabc&index=3", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
        ("https://www.youtube.com/shorts/abc?autoplay=1&si=xyz&embeds_referring_euri=https%3A%2F%2Fx.com", "https://youtube.com/shorts/abc"),
        ("https://news.ycombinator.com/item?id=39223456&p=2", "https://news.ycombinator.com/item?id=39223456"),
        ("https://shop.example.com/items/ABCDEFG1?view=grid&sort=price&tab=reviews", "https://shop.example.com/items/ABCDEFG1"),
        ("https://moodle.college.org/course/view.php?id=4567&section=2", "https://moodle.college.org/course/view.php?id=4567"),
        ("https://app.example.com/#/projects/42", "http://www.app.example.com/#/projects/42"),
    ]

    @Test(arguments: same)
    func sameLinkWrittenTwoWays(_ a: String, _ b: String) throws {
        #expect(try key(a) == key(b), "\(a) vs \(b)")
    }

    /// Links that are different pages.
    static let different: [(String, String)] = [
        ("https://www.youtube.com/watch/AAAAAAAAAAA", "https://www.youtube.com/watch/BBBBBBBBBBB"),
        ("https://www.youtube.com/watch?v=aaaaaaaaaaa", "https://www.youtube.com/watch?v=bbbbbbbbbbb"),
        ("https://blackboard.school.edu/ultra/courses/_1_1/outline/discussion/_555_1", "https://blackboard.school.edu/ultra/courses/_1_1/outline/discussion/_556_1"),
        ("https://news.ycombinator.com/item?id=1", "https://news.ycombinator.com/item?id=2"),
        ("https://example.com/a", "https://example.com/b"),
        ("https://example.com/post-one-two-three", "https://example.com/post-one-two-four"),
        ("https://shop.example.com/items/ABCDEFG1?color=red", "https://shop.example.com/items/ABCDEFG1?color=blue"),
        ("https://shop.example.com/items/ABCDEFG1?size=m", "https://shop.example.com/items/ABCDEFG1?size=l"),
        ("https://shop.example.com/items/ABCDEFG1?colour=red", "https://shop.example.com/items/ABCDEFG1?colour=blue"),
        ("https://example.com/a?page=1", "https://example.com/a?page=2"),
        ("https://app.example.com/#/projects/42", "https://app.example.com/#/projects/43"),
        ("https://example.com/a", "https://sub.example.com/a"),
    ]

    @Test(arguments: different)
    func differentPagesStayApart(_ a: String, _ b: String) throws {
        #expect(try key(a) != key(b), "\(a) vs \(b)")
    }

    @Test func theKeyIsNotTheLink() throws {
        #expect(try key("https://www.example.com/a/") == "example.com/a")
    }

    private func pageKeyOf(_ raw: String) -> String? { LinkCleaner.clean(raw).map(LinkCleaner.pageKey) }

    @Test func shortAndLongYouTubeLinksAreOnePage() {
        #expect(pageKeyOf("https://youtu.be/dQw4w9WgXcQ?si=abc&t=42") == pageKeyOf("https://www.youtube.com/watch?v=dQw4w9WgXcQ"))
        #expect(pageKeyOf("https://youtu.be/dQw4w9WgXcQ") != pageKeyOf("https://www.youtube.com/watch?v=other-video"))
    }

    @Test func notionsPvsIsDroppedAndGoogleDocumentsAreTheirIDs() {
        #expect(pageKeyOf("https://www.notion.so/Roadmap-1234567890abcdef?pvs=4") == pageKeyOf("https://www.notion.so/Roadmap-1234567890abcdef"))
        #expect(LinkCleaner.clean("https://www.notion.so/Roadmap-1234567890abcdef?pvs=4")?.absoluteString == "https://www.notion.so/Roadmap-1234567890abcdef")
        let edit = pageKeyOf("https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQ/edit?usp=sharing")
        #expect(edit == pageKeyOf("https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQ/view"))
        #expect(edit == pageKeyOf("https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQ/preview?usp=drive_link"))
        #expect(edit != pageKeyOf("https://docs.google.com/document/d/ZzZzOtherDocument/edit"))
        #expect(pageKeyOf("https://docs.google.com/spreadsheets/d/1Sheet/edit?gid=1") != pageKeyOf("https://docs.google.com/spreadsheets/d/1Sheet/edit?gid=2"))
        #expect(pageKeyOf("https://docs.google.com/presentation/d/1Deck/edit?usp=sharing") == pageKeyOf("https://docs.google.com/presentation/d/1Deck/preview"))
    }
}
