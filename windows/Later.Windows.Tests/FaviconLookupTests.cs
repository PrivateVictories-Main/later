using System.Globalization;
using System.Text;
using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class FaviconLookupTests
{
    /// A profile's icon cache with Chromium's tables. `icons` names each icon's bitmaps (width -> stand-in PNG bytes);
    /// `pages` maps page links to icon names.
    static string MakeCache(string root, Dictionary<string, Dictionary<int, string>> icons, Dictionary<string, string> pages, string profile = "Default")
    {
        var path = Path.Combine(root, profile, "Favicons");
        if (File.Exists(path)) File.Delete(path);
        var sql = new StringBuilder(@"
            CREATE TABLE icon_mapping(id INTEGER PRIMARY KEY, page_url LONGVARCHAR NOT NULL, icon_id INTEGER);
            CREATE INDEX icon_mapping_page_url_idx ON icon_mapping(page_url);
            CREATE TABLE favicons(id INTEGER PRIMARY KEY, url LONGVARCHAR NOT NULL, icon_type INTEGER DEFAULT 1);
            CREATE TABLE favicon_bitmaps(id INTEGER PRIMARY KEY, icon_id INTEGER NOT NULL, image_data BLOB,
                                         width INTEGER DEFAULT 0, height INTEGER DEFAULT 0);");
        var ids = icons.Keys.OrderBy(k => k, StringComparer.Ordinal).Select((k, i) => (k, i + 1)).ToDictionary(x => x.k, x => x.Item2, StringComparer.Ordinal);
        foreach (var (name, bitmaps) in icons)
        {
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO favicons(id, url) VALUES({ids[name]}, 'https://icons.example/{name}');");
            foreach (var (width, bytes) in bitmaps)
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO favicon_bitmaps(icon_id, image_data, width, height) VALUES({ids[name]}, CAST('{bytes}' AS BLOB), {width}, {width});");
        }
        foreach (var (page, name) in pages) sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO icon_mapping(page_url, icon_id) VALUES('{page}', {ids[name]});");
        TestSqlite.Exec(path, sql.ToString());
        return path;
    }

    static FaviconOutcome? LookUp(string host, string[] pages, string root) =>
        FaviconStore.Icons(new Dictionary<string, List<string>> { [host] = [.. pages] }, FaviconStore.Databases(root)).GetValueOrDefault(host);

    static void AssertFound(string text, FaviconOutcome? outcome) =>
        Assert.Equal(text, Assert.IsType<FaviconOutcome.Found>(outcome) is { Png: var png } ? Encoding.UTF8.GetString(png) : null);

    static Dictionary<int, string> Sizes(params (int Width, string Bytes)[] sizes) => sizes.ToDictionary(s => s.Width, s => s.Bytes);

    [Fact]
    public void AnExactPageMatchComesFirst()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path, new() { ["shop"] = Sizes((32, "shop32")), ["home"] = Sizes((32, "home32")) },
            new() { ["https://www.a.com/p/1"] = "shop", ["https://www.a.com/"] = "home", ["https://www.a.com/x"] = "home" });
        AssertFound("shop32", LookUp("a.com", ["https://www.a.com/p/1"], dir.Path));
    }

    [Fact]
    public void OtherwiseTheIconMostOfTheHostsPagesUse()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path, new() { ["one"] = Sizes((32, "one32")), ["two"] = Sizes((32, "two32")), ["near"] = Sizes((32, "near32")) },
            new()
            {
                ["https://b.com/1"] = "one", ["https://b.com/2"] = "two", ["https://b.com/3"] = "two",
                // neighbours in sort order that aren't b.com, so the range must leave them out
                ["https://b.company.com/1"] = "near", ["https://b.company.com/2"] = "near", ["https://b.company.com/3"] = "near", ["https://b.co/"] = "near",
            });
        AssertFound("two32", LookUp("b.com", ["https://b.com/not-in-the-cache"], dir.Path));
    }

    [Fact]
    public void TheHomePagesIconBeatsAVariantMostPagesShare()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path, new() { ["home"] = Sizes((32, "home32")), ["variant"] = Sizes((32, "variant32")) },
            new()
            {
                ["https://www.e.com/"] = "home", ["https://www.e.com/watch?v=1"] = "variant",
                ["https://www.e.com/watch?v=2"] = "variant", ["https://www.e.com/watch?v=3"] = "variant",
            });
        AssertFound("home32", LookUp("e.com", ["https://www.e.com/watch?v=9"], dir.Path));
    }

    [Fact]
    public void FindsTheHostWithOrWithoutWww()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path, new() { ["rei"] = Sizes((32, "rei32")), ["plain"] = Sizes((16, "plain16")) },
            new() { ["https://www.rei.com/c/jackets"] = "rei", ["https://plain.org/a"] = "plain" });
        AssertFound("rei32", LookUp("rei.com", ["https://rei.com/product/1"], dir.Path));
        AssertFound("plain16", LookUp("plain.org", ["https://www.plain.org/b"], dir.Path));
        Assert.IsType<FaviconOutcome.Missing>(LookUp("other.com", ["https://www.other.com/"], dir.Path));
    }

    /// The most-used icon counts the host's pages with and without www. together, not one name before the other.
    [Fact]
    public void TheMostUsedIconCountsBothNamesTogether()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path, new() { ["bare"] = Sizes((32, "bare32")), ["www"] = Sizes((32, "www32")) },
            new() { ["https://f.com/1"] = "bare", ["https://f.com/2"] = "www", ["https://www.f.com/3"] = "www", ["https://www.f.com/4"] = "www" });
        AssertFound("www32", LookUp("f.com", ["https://f.com/not-in-the-cache"], dir.Path));
    }

    /// A bitmap that won't decode is passed over: for a smaller one, then the next fallback, then the next profile.
    [Fact]
    public void ABitmapThatWontDecodeFallsThroughToTheNextCandidate()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path,
            new() { ["sizes"] = Sizes((64, "bad"), (16, "sizes16")), ["page"] = Sizes((32, "bad")), ["home"] = Sizes((32, "home32")), ["only"] = Sizes((32, "bad")) },
            new() { ["https://g.com/"] = "sizes", ["https://h.com/p"] = "page", ["https://h.com/"] = "home", ["https://k.com/"] = "only" });
        MakeCache(dir.Path, new() { ["good"] = Sizes((32, "k32")) }, new() { ["https://k.com/"] = "good" }, "Profile 1");
        var databases = FaviconStore.Databases(dir.Path);
        var pages = new Dictionary<string, List<string>>
        {
            ["g.com"] = ["https://g.com/"], ["h.com"] = ["https://h.com/p"], ["k.com"] = ["https://k.com/"], ["m.com"] = ["https://m.com/"],
        };
        var icons = FaviconStore.Icons(pages, databases, bytes => !bytes.AsSpan().SequenceEqual("bad"u8));
        AssertFound("sizes16", icons["g.com"]); AssertFound("home32", icons["h.com"]); AssertFound("k32", icons["k.com"]);
        Assert.IsType<FaviconOutcome.Missing>(icons["m.com"]);
        var allBad = FaviconStore.Icons(new Dictionary<string, List<string>> { ["h.com"] = ["https://h.com/p"] }, databases, _ => false);
        Assert.IsType<FaviconOutcome.Missing>(allBad["h.com"]);
    }

    [Fact]
    public void PicksTheLargestBitmapAndSkipsPlaceholdersAndEmptyOnes()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path,
            new() { ["sizes"] = Sizes((0, "placeholder"), (16, "sizes16"), (32, "sizes32")), ["blank32"] = Sizes((16, "blank16"), (32, "")), ["only0"] = Sizes((0, "placeholder")) },
            new() { ["https://sizes.com/"] = "sizes", ["https://blank.com/"] = "blank32", ["https://zero.com/"] = "only0" });
        AssertFound("sizes32", LookUp("sizes.com", ["https://sizes.com/"], dir.Path));
        AssertFound("blank16", LookUp("blank.com", ["https://blank.com/"], dir.Path));
        Assert.IsType<FaviconOutcome.Missing>(LookUp("zero.com", ["https://zero.com/"], dir.Path));
    }

    [Fact]
    public void SearchesEveryProfileInOrder()
    {
        using var dir = new TempDir();
        MakeCache(dir.Path, new() { ["x"] = Sizes((16, "default16")) }, new() { ["https://elsewhere.com/"] = "x" });
        MakeCache(dir.Path, new() { ["x"] = Sizes((32, "ten32")) }, new() { ["https://c.com/"] = "x" }, "Profile 10");
        MakeCache(dir.Path, new() { ["x"] = Sizes((32, "two32")) }, new() { ["https://c.com/"] = "x" }, "Profile 2");
        MakeCache(dir.Path, new() { ["x"] = Sizes((32, "system32")) }, new() { ["https://c.com/"] = "x" }, "System Profile");
        Directory.CreateDirectory(dir.At("Profile 1"));   // no icon cache yet
        Assert.Equal(["Default", "Profile 2", "Profile 10"],
            FaviconStore.Databases(dir.Path).Select(p => Path.GetFileName(Path.GetDirectoryName(p))).ToArray());
        AssertFound("two32", LookUp("c.com", ["https://c.com/"], dir.Path));
    }

    [Fact]
    public void AMissingCacheHasNoIcons()
    {
        using var dir = new TempDir();
        Assert.Empty(FaviconStore.Databases(dir.At("nothing-here")));
        var pages = new Dictionary<string, List<string>> { ["a.com"] = ["https://a.com/"] };
        Assert.IsType<FaviconOutcome.Missing>(FaviconStore.Icons(pages, [Path.Combine(dir.Path, "Default", "Favicons")])["a.com"]);
        Assert.IsType<FaviconOutcome.Missing>(FaviconStore.Icons(pages, [])["a.com"]);
    }

    /// An immutable read during the browser's write can see a damaged file. That's "try later", and the next try works.
    [Fact]
    public void ADamagedReadIsTriedAgainLater()
    {
        using var dir = new TempDir();
        var path = MakeCache(dir.Path, new() { ["d"] = Sizes((32, "d32")) }, new() { ["https://d.com/"] = "d" });
        var good = File.ReadAllBytes(path);
        Assert.True(good.Length > 4096);
        // Tables intact on the first page, their contents garbage.
        var damaged = (byte[])good.Clone(); Array.Fill(damaged, (byte)0xAB, 4096, damaged.Length - 4096);
        File.WriteAllBytes(path, damaged);
        Assert.IsType<FaviconOutcome.RetryLater>(LookUp("d.com", ["https://d.com/"], dir.Path));
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xCD, good.Length).ToArray());   // not even a header
        Assert.IsType<FaviconOutcome.RetryLater>(LookUp("d.com", ["https://d.com/"], dir.Path));
        File.WriteAllBytes(path, good);
        AssertFound("d32", LookUp("d.com", ["https://d.com/"], dir.Path));
        Assert.Equal(["Favicons"], Directory.GetFiles(dir.At("Default")).Select(f => Path.GetFileName(f)).ToArray());   // read-only: no journal, no lock files
    }
}
