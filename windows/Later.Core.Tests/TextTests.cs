using System.Text;
using Xunit;

namespace Later.Core.Tests;

public class TextTests
{
    [Fact]
    public void CountsGraphemeClusters()
    {
        Assert.Equal(0, Text.Count(""));
        Assert.Equal(1, Text.Count("\u00e9"));
        Assert.Equal(1, Text.Count("e\u0301"));
        Assert.Equal(3, Text.Count("abc"));
    }

    [Fact]
    public void StableSortKeepsEqualItemsInOrder()
    {
        var equal = Enumerable.Range(0, 1000).ToList();
        Assert.Equal(equal, Text.StableSort(equal, (_, _) => 0));
        var byThird = Text.StableSort(equal, (a, b) => (a % 3).CompareTo(b % 3));
        Assert.Equal(equal.Where(i => i % 3 == 0).Concat(equal.Where(i => i % 3 == 1)).Concat(equal.Where(i => i % 3 == 2)), byThird);
    }

    [Fact]
    public void LettersAndNumbersFollowUnicode()
    {
        Assert.True(Text.IsLetter(new Rune('\u00e9')));
        Assert.True(Text.IsLetter(new Rune('\u2160')));   // Roman numeral one is a letter-number
        Assert.False(Text.IsLetter(new Rune('-')));
        Assert.True(Text.IsNumber(new Rune('\u00b2')));
        Assert.True(Text.IsNumber(new Rune('7')));
        Assert.False(Text.IsNumber(new Rune('x')));
    }

    [Fact]
    public void SplitWhereOmitsEmptyPieces()
    {
        Assert.Equal(new List<string> { "a", "b" }, Text.SplitWhere("--a--b-", r => r.Value == '-'));
        Assert.Empty(Text.SplitWhere("---", r => r.Value == '-'));
        Assert.Equal(new List<string> { "a", "b" }, Text.SplitOmitEmpty("/a//b/", '/'));
    }
}
