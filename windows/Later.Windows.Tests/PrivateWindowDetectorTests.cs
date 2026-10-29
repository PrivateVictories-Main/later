using Later.Core;
using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class PrivateWindowDetectorTests
{
    [Theory]
    [InlineData("Fixture 9 - Google Chrome (Incognito)", "Incognito", true)]
    [InlineData("Fixture 9 - Helium (Incognito)", "Incognito", true)]
    [InlineData("Fixture 9 - Helium (Incognito)", null, true)]        // one marker is enough
    [InlineData("Fixture 9 - Helium", "Incognito", true)]
    [InlineData("Fixture 1 - Google Chrome", "You", false)]
    [InlineData("Fixture 1 - Helium", "You", false)]
    [InlineData("Fixture 1 - Helium", null, false)]
    public void ReadsTheMarkerOnTheWindowChrome(string rootView, string? profileButton, bool expected) =>
        Assert.Equal(expected, PrivateWindowDetector.IsPrivate(new RawWindowInfo(Browser.Chrome, rootView, profileButton)));
}
