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
    [InlineData("Fixture 1 – Arbeitsspeichernutzung – 17,5 MB", "Fixture 1")]
    [InlineData("Fixture 1 - Utilisation de la mémoire - 17,5 Mo", "Fixture 1")]
    [InlineData("Fixture 1 - Utilisation de la mémoire - 17,5\u00A0Mo", "Fixture 1")]
    [InlineData("Fixture 1 - メモリ使用量 - 18.6 MB", "Fixture 1")]
    [InlineData("Download size – 17 MB", "Download size – 17 MB")]   // one dash: a real title that ends in a number and unit
    [InlineData("Release 4 - 2 GB", "Release 4 - 2 GB")]
    [InlineData("Memory usage - 85 MB", "Memory usage - 85 MB")]   // a page whose whole title looks like the suffix stays
    public void StripsTheMemoryUsageSuffix(string name, string expected) => Assert.Equal(expected, TabNames.Clean(name));
}
