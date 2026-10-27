using Xunit;

namespace Later.Core.Tests;

public class UrlTextTests
{
    [Fact]
    public void PercentDecodeRejectsBrokenEscapes()
    {
        Assert.Null(UrlText.PercentDecode("%4"));
        Assert.Null(UrlText.PercentDecode("%E0%A4%A"));
        Assert.Null(UrlText.PercentDecode("%FF"));   // not UTF-8
    }

    [Fact]
    public void PercentDecodeDecodesEscapes()
    {
        Assert.Equal("aA", UrlText.PercentDecode("a%41"));
        Assert.Equal("C++", UrlText.PercentDecode("C%2B%2B"));
        Assert.Equal("é", UrlText.PercentDecode("%C3%A9"));
        Assert.Equal("plain", UrlText.PercentDecode("plain"));
    }

    [Fact]
    public void EncodeInvalidEncodesOnlyWhatTheComponentCannotHold()
    {
        Assert.Equal("a%20b%25zz", UrlText.EncodeInvalid("a b%zz", UrlText.PathChar));
        Assert.Equal("/a%2Fb/c", UrlText.EncodeInvalid("/a%2Fb/c", UrlText.PathChar));   // existing escapes stay
        Assert.Equal("%C3%A9", UrlText.EncodeInvalid("é", UrlText.PathChar));
    }
}
