using Later.Core;
using Later.Windows.Reader;

namespace Later.Windows.Tests.Integration;

internal static class ItSupport
{
    public static bool Required => Environment.GetEnvironmentVariable("LATER_REQUIRE_BROWSERS") == "1";

    /// The browsers this run can drive. When the run requires browsers, one that is missing is a failure, not a skip.
    public static IEnumerable<object[]> Browsers()
    {
        foreach (var b in new[] { Browser.Chrome, Browser.Helium })
            if (BrowserFixture.Exe(b) is { Length: > 0 } || Required) yield return [b];
    }

    public static string ExeOf(Browser b)
    {
        var exe = BrowserFixture.Exe(b);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) throw new Xunit.Sdk.XunitException($"{b} was not found (LATER_{(b == Browser.Chrome ? "CHROME" : "HELIUM")}='{exe}')");
        return exe;
    }

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            if (condition()) return true;
            if (DateTime.UtcNow >= end) return false;
            Thread.Sleep(100);
        }
    }

    public static List<BrowserWindow> WindowsOf(Browser b) => BrowserDiscovery.Enumerate().Where(w => w.Browser == b).ToList();

    sealed class NoHistory : IHistorySource
    {
        public Dictionary<string, string> LinksForTitles(Browser b, IReadOnlyCollection<string> titles, DateTime since) => new(StringComparer.Ordinal);
    }

    sealed class NoSession : ISessionSource
    {
        public IReadOnlyList<SessionTab> Read(Browser b) => [];
    }

    /// A reader on the real window enumeration and UI Automation, looking in `profile` (the fixture's) for History and sessions.
    public static BrowserReader ReaderFor(string profile) =>
        new(new UiaSource(_ => profile), new HistorySource(_ => profile), new SessionSource(_ => profile), BrowserDiscovery.Enumerate, () => DateTime.UtcNow);

    /// A reader with no fallbacks at all: only what the window itself gave.
    public static BrowserReader MemoryOnlyReader() =>
        new(new UiaSource(), new NoHistory(), new NoSession(), BrowserDiscovery.Enumerate, () => DateTime.UtcNow);

    /// Reads until `done` accepts the result or the time is up; returns the last result.
    public static ReadResult ReadUntil(BrowserReader reader, Browser b, Func<ReadResult, bool> done, TimeSpan timeout)
    {
        var last = reader.Read(b);
        var end = DateTime.UtcNow + timeout;
        while (!done(last) && DateTime.UtcNow < end) { Thread.Sleep(250); last = reader.Read(b); }
        return last;
    }

    public static string Describe(ReadResult r) =>
        $"{r.Status} windows={r.WindowsRead} private={r.PrivateWindowsSkipped} unresolved={r.UnresolvedTabs} tabs=[{string.Join("; ", r.Snapshot?.Tabs.Select(t => $"{t.Title} {t.Url} active={t.IsActive} w{t.WindowIndex}/{t.WindowId} t{t.TabIndex}") ?? [])}]";
}
