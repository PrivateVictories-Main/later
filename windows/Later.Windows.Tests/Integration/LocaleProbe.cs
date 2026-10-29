using Later.Core;
using Later.Windows.Reader;
using Xunit;
using Xunit.Abstractions;

namespace Later.Windows.Tests.Integration;

/// Diagnostic only: what the window chrome says under other interface languages and with a second profile.
[Trait("Category", "Probe")]
public class LocaleProbe(ITestOutputHelper output)
{
    void P(string line) => output.WriteLine("LOCALE " + line);

    static string Esc(string? s) => s is null ? "<null>" : string.Concat(s.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:x4}"));

    void Windows(Browser b, UiaTest uia, string label)
    {
        foreach (var w in ItSupport.WindowsOf(b))
        {
            var lines = uia.Dump(w.Hwnd, 9, 400).Where(l => l.Contains("| BrowserRootView", StringComparison.Ordinal) || l.Contains("| AvatarToolbarButton", StringComparison.Ordinal));
            P($"{label} {b} hwnd={w.Hwnd} windowText='{Esc(w.Title)}' :: {string.Join(" || ", lines.Select(l => Esc(l.Trim())))}");
        }
    }

    void LocalState(string profile, string label)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(profile, "Local State"));
            foreach (var key in new[] { "\"app_locale\":\"[^\"]*\"", "\"name\":\"[^\"]*\"", "\"gaia_name\":\"[^\"]*\"", "\"user_name\":\"[^\"]*\"" })
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, key, System.Text.RegularExpressions.RegexOptions.CultureInvariant).Take(6))
                    P($"{label} local-state {Esc(m.Value)}");
        }
        catch (IOException e) { P($"{label} local state unreadable: {e.Message}"); }
    }

    [Theory]
    [MemberData(nameof(Browsers))]
    public void Languages(Browser b)
    {
        var exe = ItSupport.ExeOf(b);
        using var server = new FixtureServer();
        using var uia = new UiaTest();
        foreach (var lang in new[] { "de", "ja", "fr", "es" })
        {
            using var normal = BrowserFixture.Start(exe, server, [FixtureServer.Url(1)], extra: [$"--lang={lang}"]);
            P($"--- {b} --lang={lang} normal window appeared={ItSupport.WaitUntil(() => ItSupport.WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), TimeSpan.FromSeconds(40))}");
            using var priv = BrowserFixture.Start(exe, server, [FixtureServer.Url(9)], incognito: true, profile: normal.Profile, extra: [$"--lang={lang}"]);
            P($"{b} {lang} private window appeared={ItSupport.WaitUntil(() => ItSupport.WindowsOf(b).Count >= 2, TimeSpan.FromSeconds(40))}");
            Thread.Sleep(4000);
            Windows(b, uia, lang);
            LocalState(normal.Profile, lang);
            priv.Close(); normal.Close();
            ItSupport.WaitUntil(() => ItSupport.WindowsOf(b).Count == 0, TimeSpan.FromSeconds(20));
        }
    }

    [Theory]
    [MemberData(nameof(Browsers))]
    public void SecondProfile(Browser b)
    {
        var exe = ItSupport.ExeOf(b);
        using var server = new FixtureServer();
        using var uia = new UiaTest();
        using var first = BrowserFixture.Start(exe, server, [FixtureServer.Url(1)], extra: ["--profile-directory=Default"]);
        P($"--- {b} second profile: first window={ItSupport.WaitUntil(() => ItSupport.WindowsOf(b).Any(w => w.Title.Contains("Fixture 1", StringComparison.Ordinal)), TimeSpan.FromSeconds(40))}");
        Thread.Sleep(3000);
        Windows(b, uia, "one-profile");
        using var second = BrowserFixture.Start(exe, server, [FixtureServer.Url(2)], profile: first.Profile, extra: ["--profile-directory=Profile 2"]);
        P($"{b} second window={ItSupport.WaitUntil(() => ItSupport.WindowsOf(b).Count >= 2, TimeSpan.FromSeconds(40))}");
        using var priv = BrowserFixture.Start(exe, server, [FixtureServer.Url(9)], incognito: true, profile: first.Profile, extra: ["--profile-directory=Profile 2"]);
        P($"{b} private window={ItSupport.WaitUntil(() => ItSupport.WindowsOf(b).Count >= 3, TimeSpan.FromSeconds(40))}");
        Thread.Sleep(5000);
        Windows(b, uia, "two-profiles");
        LocalState(first.Profile, "two-profiles");
        P($"two-profiles Default Preferences name: {Esc(PrefName(first.Profile, "Default"))}; Profile 2: {Esc(PrefName(first.Profile, "Profile 2"))}");
    }

    static string? PrefName(string profile, string dir)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(profile, dir, "Preferences")), "\"profile\":\\{[^}]*?\"name\":\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (IOException) { return null; }
    }

    public static IEnumerable<object[]> Browsers() => ItSupport.Browsers();
}
