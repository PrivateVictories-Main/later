using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Later.Core;
using Later.Windows.Reader;

namespace Later.Windows.Engine;

/// The real world for the engine. All reads happen on one MTA thread that owns the `BrowserReader` (its UI Automation session
/// belongs to the thread that made it, and the reader keeps plain dictionaries), made at the first read, so a machine
/// with no browser open pays for none of it.
sealed class RealBrowserIO(Func<Browser, string>? dataFolder = null) : IBrowserIO, IDisposable
{
    readonly Lock gate = new();
    ReaderThread? thread;
    volatile BrowserReader? reader;

    string Folder(Browser b) => (dataFolder ?? BrowserDiscovery.DataFolder)(b);

    public bool IsInstalled(Browser b) => BrowserDiscovery.ExePath(b) is not null;

    public ReadResult Read(Browser b) => OnReaderThread(r => r.Read(b));

    public void BrowserQuit(Browser b) => OnReaderThread(r => { r.BrowserQuit(b); return 0; });

    public IReadOnlyCollection<nint> KnownWindows(Browser b) => reader?.KnownWindows(b) ?? [];

    public Dictionary<string, HistoryResult> History(IReadOnlyList<string> links) =>
        HistoryLookupStore.Lookup(links, BrowserInfo.All.SelectMany(b => HistoryLookupStore.Databases(Folder(b))).ToList());

    public Browser? FrontBrowser() => BrowserDiscovery.FrontBrowser();

    public Browser? BrowserOfWindow(nint hwnd) => BrowserDiscovery.BrowserOfWindow(hwnd);

    T OnReaderThread<T>(Func<BrowserReader, T> work)
    {
        ReaderThread t;
        lock (gate) t = thread ??= new ReaderThread();
        return t.Run(() => work(reader ??= dataFolder is null
            ? new BrowserReader()
            : new BrowserReader(new UiaSource(dataFolder), new HistorySource(dataFolder), new SessionSource(dataFolder), BrowserDiscovery.Enumerate, () => DateTime.UtcNow)));
    }

    public void Dispose()
    {
        ReaderThread? t;
        lock (gate) { t = thread; thread = null; }
        if (t is null) return;
        t.Run(() => { reader?.Dispose(); reader = null; return 0; });   // on the thread that made the UI Automation session
        t.Dispose();
    }

    sealed class ReaderThread : IDisposable
    {
        readonly BlockingCollection<Action> work = [];
        readonly Thread thread;

        public ReaderThread()
        {
            thread = new Thread(() => { foreach (var a in work.GetConsumingEnumerable()) a(); }) { Name = "Later.Reader", IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public T Run<T>(Func<T> f)
        {
            T result = default!; Exception? error = null;
            using var done = new ManualResetEventSlim();
            work.Add(() => { try { result = f(); } catch (Exception e) { error = e; } finally { done.Set(); } });
            done.Wait();
            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }

        public void Dispose() { work.CompleteAdding(); thread.Join(); work.Dispose(); }
    }
}
