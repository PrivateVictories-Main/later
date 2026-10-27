using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Later.Core;
using Xunit;

namespace Later.Core.Tests;

public class LibraryJsonTests
{
    static readonly double[] Dwell = [12.5, 130];

    static byte[] Golden() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "library-golden.json"));

    static byte[] GoldenWith(string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Golden()).Replace(from, to, StringComparison.Ordinal));

    [Fact]
    public void TheMacGoldenDecodesToTheExpectedList()
    {
        var d = LibraryJson.Decode(Golden());
        Assert.Equal(2, d.Items.Count);
        var a = d.Items[0];
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), a.Id);
        Assert.Equal(Kind.Reading, a.Kind); Assert.Equal(Browser.Helium, a.Browser); Assert.Equal(ItemStatus.Done, a.Status);
        Assert.Equal(LibraryJson.ReferenceDate.AddSeconds(800_000_060.5), a.LastSeen);
        Assert.Equal(7, a.HistoryVisits); Assert.Equal(2, a.TabIndex); Assert.Equal(1, a.WindowRank); Assert.Equal("https://example.com/a", a.HistoryChecked);
        Assert.Null(d.Items[1].DoneAt); Assert.Null(d.Items[1].OpenedAt);
        Assert.Equal(false, d.Corrections["example.com/*"].IsSpecific); Assert.Equal(Kind.School, d.Corrections["example.com/*"].Kind);
        Assert.Null(d.Corrections["x.com/*"].IsSpecific);
        Assert.Equal(Dwell, d.DwellSamples);
        Assert.Single(d.Dismissed);
    }

    [Fact]
    public void EncodingReproducesTheGoldenAsParsedJson()
    {
        var round = LibraryJson.Encode(LibraryJson.Decode(Golden()));
        using var a = JsonDocument.Parse(round); using var b = JsonDocument.Parse(Golden());
        Assert.True(JsonEquals(a.RootElement, b.RootElement), "re-encoded golden differs from the Mac's");
    }

    [Fact]
    public void UuidsAreWrittenUppercase() =>
        Assert.Contains("\"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\"", Encoding.UTF8.GetString(LibraryJson.Encode(LibraryJson.Decode(GoldenWith("AAAAAAAA-BBBB", "aaaaaaaa-bbbb")))), StringComparison.Ordinal);

    [Fact]
    public void AKindFromANewerBuildReadsAsOther() =>
        Assert.Equal(Kind.Other, LibraryJson.Decode(GoldenWith("\"reading\"", "\"podcasts\"")).Items[0].Kind);

    [Fact]
    public void AFileWithoutDismissalsLoads()
    {
        var json = Encoding.UTF8.GetString(Golden());
        var without = Regex.Replace(json, "\"dismissed\"\\s*:\\s*\\{[^}]*\\},?", "");
        Assert.DoesNotContain("dismissed", without, StringComparison.Ordinal);
        Assert.Empty(LibraryJson.Decode(Encoding.UTF8.GetBytes(without)).Dismissed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"version\":1,\"items\":[{\"id\":\"x\"}],\"corrections\":{},\"seen\":{},\"dwellSamples\":[]}")]
    public void UnreadableFilesFail(string json) =>
        Assert.Throws<FormatException>(() => LibraryJson.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void AnUnknownBrowserFailsLikeTheMac() =>
        Assert.Throws<FormatException>(() => LibraryJson.Decode(GoldenWith("\"helium\"", "\"edge\"")));

    static bool JsonEquals(JsonElement x, JsonElement y)
    {
        if (x.ValueKind != y.ValueKind) return false;
        switch (x.ValueKind)
        {
            case JsonValueKind.Object:
                var xs = x.EnumerateObject().ToDictionary(p => p.Name, p => p.Value); var ys = y.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                return xs.Count == ys.Count && xs.All(kv => ys.TryGetValue(kv.Key, out var o) && JsonEquals(kv.Value, o));
            case JsonValueKind.Array:
                var xa = x.EnumerateArray().ToList(); var ya = y.EnumerateArray().ToList();
                return xa.Count == ya.Count && xa.Zip(ya).All(p => JsonEquals(p.First, p.Second));
            case JsonValueKind.Number: return Math.Abs(x.GetDouble() - y.GetDouble()) < 1e-6;
            default: return x.GetRawText() == y.GetRawText();
        }
    }
}
