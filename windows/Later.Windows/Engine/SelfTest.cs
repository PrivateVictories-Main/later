using Later.Core;
using Later.Windows.Native;
using Later.Windows.Platform;
using Later.Windows.Reader;

namespace Later.Windows.Engine;

/// `Later.exe --self-test`: the product's own code run from the published folder, headless, before any WinUI type. It catches
/// a runtime file that did not make it into the folder (the stripped System.Net* assemblies, for one) on the machine that
/// would hit it. It writes nothing to disk and opens no browser.
public static class SelfTest
{
    public static int Run(TextWriter output)
    {
        try
        {
            Check();
            output.WriteLine("self-test ok");
            return 0;
        }
        catch (Exception e)
        {
            output.WriteLine(e.GetType().FullName);
            return 1;
        }
    }

    static void Expect(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    sealed class QuietIO : IBrowserIO
    {
        public bool IsInstalled(Browser b) => false;
        public ReadResult Read(Browser b) => new(ReadStatus.NotRunning, null, 0, 0, 0);
        public Dictionary<string, HistoryResult> History(IReadOnlyList<string> links) => [];
        public Dictionary<string, HashSet<int>> VisitDays(DateTime since) => [];
        public IReadOnlyCollection<nint> KnownWindows(Browser b) => [];
        public void BrowserQuit(Browser b) { }
        public Browser? FrontBrowser() => null;
        public Browser? BrowserOfWindow(nint hwnd) => null;
    }

    sealed class InlineHost : IEngineHost
    {
        public void Post(Action a) => a();
        public IDisposable After(TimeSpan delay, Action a) => new Nothing();
        public void RunBackground(Action work) => work();
        sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    static void Check()
    {
        // Core: link cleaning, the page rules, the judge, the library and its JSON.
        const string product = "https://shop.example.com/products/1000001?utm_source=newsletter";
        var clean = LinkCleaner.Clean(product);
        Expect(clean is not null && !clean.Absolute.Contains("utm_source", StringComparison.Ordinal), "LinkCleaner");
        Expect(PageRules.Judge(clean!, "Walnut desk organizer").IsSpecific, "PageRules");
        Expect(!PageRules.Judge(LinkCleaner.Clean("https://www.amazon.com/")!, "Shop").IsSpecific, "PageRules, a general page");

        var library = new Library();
        var judge = new Judge(library, new HashSet<Browser> { Browser.Helium });
        var t0 = new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        judge.SetFrontBrowser(Browser.Helium, t0);
        var tab = new Tab(product, "Walnut desk organizer", true, 0);
        judge.Ingest(new Snapshot(Browser.Helium, [tab]), t0);
        judge.Ingest(new Snapshot(Browser.Helium, [tab]), t0.AddSeconds(130));
        Expect(library.Data.Items.Count == 1, "Judge");
        var decoded = LibraryJson.Decode(LibraryJson.Encode(library.Data));
        Expect(decoded.Items.Count == 1 && decoded.Items[0].Url == library.Data.Items[0].Url, "LibraryJson");

        // Later.Windows: the engine on stand-ins (no store, no browser), and the system calls the agent makes.
        var engine = new Engine(new Library(), new Judge(new Library()), new QuietIO(), new InlineHost(), (LibraryStore?)null, new Settings());
        engine.Start();
        engine.SetAway("lock", true);
        engine.Stop(flush: true);
        Expect(AppPaths.Default.InstanceSuffix.Length == 12, "AppPaths");
        using (var gate = EngineLock.Open("selftest" + Guid.NewGuid().ToString("N")[..8]))
        {
            Expect(gate.TryAcquire(TimeSpan.Zero), "EngineLock");
            gate.Release();
        }
        _ = NativeApi.PeekMessage(out _, 0, 0, 0, 0);
        using (new Debouncer(TimeSpan.FromSeconds(1), () => { })) { }
        // Loading these types needs their assemblies and imports; nothing of them is started.
        Expect(typeof(WinEventWatcher).IsClass && typeof(BrowserReader).IsClass && typeof(SessionNotifier).IsClass && typeof(EngineHost).IsClass, "agent types");
    }
}
