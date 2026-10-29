using Xunit;

namespace Later.Core.Tests;

public class PathPatternTests
{
    static List<string> Segments(string path) => Text.SplitOmitEmpty(path, '/');
    static readonly HashSet<string> NoKeys = [];

    [Fact]
    public void DoubleStarMatchesZeroSegments()
    {
        PathPattern pattern = "/explore/**";
        Assert.True(pattern.Matches(Segments("/explore"), NoKeys));
    }

    [Fact]
    public void MissingQueryKeyFails()
    {
        PathPattern watch = "/watch?v";
        Assert.False(watch.Matches(Segments("/watch"), NoKeys));
        Assert.True(watch.Matches(Segments("/watch"), new HashSet<string> { "v" }));
    }

    [Fact]
    public void SegmentsIgnoreCase()
    {
        PathPattern pattern = "/wiki/Main_Page";
        Assert.True(pattern.Matches(Segments("/WIKI/main_page"), NoKeys));
    }

    [Fact]
    public void TrailingSlashesDontMatter()
    {
        PathPattern pattern = "/r/*";
        Assert.True(pattern.Matches(Segments("/r/swift/"), NoKeys));
    }
}
