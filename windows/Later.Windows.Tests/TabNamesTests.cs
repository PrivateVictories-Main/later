using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class TabNamesTests
{
    [Theory]
    [InlineData("Page 1", "Page 1")]
    [InlineData("Page 1 - Memory usage - 85 MB", "Page 1")]
    [InlineData("A - B - Memory usage - 1.2 GB", "A - B")]
    [InlineData("Docs - Memory usage - 512 KB", "Docs")]
    [InlineData("Memory usage - 85 MB", "Memory usage - 85 MB")]   // a page whose whole title looks like the suffix stays
    public void StripsTheMemoryUsageSuffix(string name, string expected) => Assert.Equal(expected, TabNames.Clean(name));
}
