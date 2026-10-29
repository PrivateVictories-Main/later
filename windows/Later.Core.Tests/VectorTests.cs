using Xunit;

namespace Later.Core.Tests;

public class VectorTests
{
    static void NoFailures(List<string> failures) => Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));

    [Fact]
    public void CountsMatchTheTables()
    {
        var l = Vectors.LoadLinkClean();
        Assert.Equal(l.Counts["clean"], l.Clean.Count); Assert.Equal(l.Counts["refuse"], l.Refuse.Count);
        Assert.Equal(l.Counts["distinct"], l.Distinct.Count); Assert.Equal(l.Counts["storefront"], l.Storefront.Count);
        var k = Vectors.Load<Vectors.PageKeys>("page-key.json");
        Assert.Equal(k.Counts["same"], k.Same.Count); Assert.Equal(k.Counts["different"], k.Different.Count); Assert.Equal(k.Counts["keys"], k.Keys.Count);
        foreach (var f in new[] { "page-rules.json", "site-rules.json" })
        { var j = Vectors.Load<Vectors.JudgeFile>(f); Assert.Equal(j.Counts["cases"], j.Cases.Count); }
    }

    [Fact]
    public void LinksClean()
    {
        var failures = new List<string>();
        foreach (var c in Vectors.LoadLinkClean().Clean)
        {
            var actual = LinkCleaner.Clean(c.Raw)?.Absolute;
            if (actual != c.Expected) failures.Add($"clean({c.Raw})\n   expected {c.Expected}\n   actual   {actual ?? "nil"}");
        }
        NoFailures(failures);
    }

    [Fact]
    public void LinksThatMustNeverBeSavedAreRefused()
    {
        NoFailures(Vectors.LoadLinkClean().Refuse.Where(r => LinkCleaner.Clean(r) is not null).Select(r => $"clean({r}) should be nil").ToList());
    }

    [Fact]
    public void DistinctLinksStayDistinct()
    {
        var failures = new List<string>();
        foreach (var p in Vectors.LoadLinkClean().Distinct)
        {
            var a = LinkCleaner.Clean(p[0]); var b = LinkCleaner.Clean(p[1]);
            if (a is null || b is null || a == b) failures.Add($"{p[0]} vs {p[1]}");
        }
        NoFailures(failures);
    }

    [Fact]
    public void Storefronts() =>
        NoFailures(Vectors.LoadLinkClean().Storefront.Where(s => LinkCleaner.IsStorefront(s.Host, s.Name) != s.Is).Select(s => s.Host).ToList());

    static string? Key(string raw) => LinkCleaner.Clean(raw) is { } u ? LinkCleaner.PageKey(u) : null;

    [Fact]
    public void PageKeys()
    {
        var k = Vectors.Load<Vectors.PageKeys>("page-key.json");
        var failures = new List<string>();
        foreach (var p in k.Same) if (Key(p[0]) is not { } a || a != Key(p[1])) failures.Add($"same? {p[0]} | {p[1]} -> {Key(p[0])} | {Key(p[1])}");
        foreach (var p in k.Different) if (Key(p[0]) is not { } a || Key(p[1]) is not { } b || a == b) failures.Add($"different? {p[0]} | {p[1]}");
        foreach (var c in k.Keys) if (Key(c.Raw) != c.Key) failures.Add($"key({c.Raw}) expected {c.Key} actual {Key(c.Raw) ?? "nil"}");
        NoFailures(failures);
    }

    static List<string> Judge(string file)
    {
        var failures = new List<string>();
        foreach (var c in Vectors.Load<Vectors.JudgeFile>(file).Cases)
        {
            var url = LinkCleaner.Clean(c.Url);
            if (url is null) { failures.Add($"{c.Url}: must clean"); continue; }
            var fixes = (c.Corrections ?? new()).ToDictionary(kv => kv.Key,
                kv => new Correction(kv.Value.IsSpecific, kv.Value.Kind is null ? null : KindInfo.FromWire(kv.Value.Kind)));
            var v = PageRules.Judge(url, c.Title, fixes, c.Feeds ?? false);
            string who = $"{c.Url} [{c.Title}]";
            if (c.Specific is { } s && v.IsSpecific != s) failures.Add($"{who}: specific expected {s} actual {v.IsSpecific}");
            if (c.Kind is { } k && v.Kind.Wire() != k) failures.Add($"{who}: kind expected {k} actual {v.Kind.Wire()}");
            if (c.PageType is { } t && v.PageType != t) failures.Add($"{who}: pageType expected {t} actual {v.PageType}");
            if (c.PageTypeSuffix is { } ps && !v.PageType.EndsWith(ps, StringComparison.Ordinal)) failures.Add($"{who}: pageType {v.PageType} should end with {ps}");
            if (c.NotPageTypeSuffix is { } ns && v.PageType.EndsWith(ns, StringComparison.Ordinal)) failures.Add($"{who}: pageType {v.PageType} should not end with {ns}");
        }
        return failures;
    }

    [Fact] public void PageRulesJudgePagesAsTheMacDoes() => NoFailures(Judge("page-rules.json"));
    [Fact] public void SiteRulesJudgePagesAsTheMacDoes() => NoFailures(Judge("site-rules.json"));
}
