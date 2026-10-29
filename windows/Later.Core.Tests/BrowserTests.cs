using Xunit;

namespace Later.Core.Tests;

public class BrowserTests
{
    [Fact]
    public void ProductNamesMapToBrowsers()
    {
        Assert.Equal(Browser.Chrome, BrowserInfo.FromProductName("Google Chrome"));
        Assert.Equal(Browser.Helium, BrowserInfo.FromProductName("Helium"));
    }

    [Fact]
    public void UnknownOrMissingProductNameMatchesNothing()
    {
        Assert.Null(BrowserInfo.FromProductName("Chromium"));
        Assert.Null(BrowserInfo.FromProductName(null));
    }

    [Fact]
    public void WireNamesRoundTrip()
    {
        foreach (var b in BrowserInfo.All) Assert.Equal(b, BrowserInfo.FromWire(b.Wire()));
        Assert.Equal("Helium", Browser.Helium.DisplayName());
        Assert.Equal("chrome", Browser.Chrome.Wire());
        Assert.Null(BrowserInfo.FromWire("safari"));
    }

    [Fact]
    public void KindsRoundTripAndUnknownReadsAsOther()
    {
        foreach (var k in KindInfo.All) Assert.Equal(k, KindInfo.FromWire(k.Wire()));
        Assert.Equal(Kind.Other, KindInfo.FromWire("gaming"));
        Assert.Equal("Shopping", Kind.Shopping.Title());
    }
}
