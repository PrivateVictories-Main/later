using Later.Core;
using Later.Windows.Reader;

namespace Later.App.Icons;

/// Saved sites' icons, from the browsers' own icon caches (`FaviconStore`): each is kept per host in memory and as a PNG in Later's
/// own folder, so icons survive restarts and the browser's cache cleanups. Asked for only about saved pages' hosts and only when
/// the window shows or the list gains a page (`Update`): no timer. A host with no icon anywhere is asked about at most once an hour.
/// A host that leaves the list loses its icon, in memory and on disk (the folder is trimmed once per launch). Any thread.
internal sealed class SiteIconStore
{
    static readonly TimeSpan MissRetry = TimeSpan.FromHours(1);

    readonly string? folder;
    readonly Func<IReadOnlyList<string>> databases;
    readonly Func<byte[], bool> accept;
    readonly Func<DateTime> clock;
    readonly object gate = new();
    readonly Dictionary<string, byte[]> png = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> misses = new(StringComparer.Ordinal);
    readonly Action<Action> run;
    bool running, runAgain, pruned;
    IReadOnlyList<Item> latest = [];

    /// `folder`: where the PNGs are kept, null to keep them in memory only. `databases`: the icon caches to read. `accept`: whether
    /// a bitmap decodes and shows something (one that doesn't is passed over for the next). `run`: how a lookup is started off the
    /// calling thread (the thread pool; tests run it at once).
    public SiteIconStore(string? folder, Func<IReadOnlyList<string>> databases, Func<byte[], bool> accept, Func<DateTime>? clock = null, Action<Action>? run = null)
    {
        this.folder = folder; this.databases = databases; this.accept = accept;
        this.clock = clock ?? (() => DateTime.UtcNow);
        this.run = run ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
    }

    /// Raised after icons were found or dropped, on whatever thread finished the lookup.
    public event Action? Changed;

    /// The PNG of a host's icon, or null.
    public byte[]? For(string host) { lock (gate) return png.GetValueOrDefault(host); }

    /// Called when the window shows or the list gains a page. Forgets hosts that left, then looks up saved hosts that have no icon yet.
    /// One lookup runs at a time; a call meanwhile runs once it ends.
    public void Update(IReadOnlyList<Item> items)
    {
        Dictionary<string, List<string>> asked = new(StringComparer.Ordinal);
        HashSet<string>? keep = null;
        List<string> gone;
        bool start = false;
        lock (gate)
        {
            latest = items;
            var hosts = items.Select(i => i.Host).ToHashSet(StringComparer.Ordinal);
            gone = png.Keys.Concat(misses.Keys).Where(h => !hosts.Contains(h)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var host in gone) { png.Remove(host); misses.Remove(host); }
            if (running) { runAgain = true; }
            else
            {
                var now = clock();
                for (int i = items.Count - 1; i >= 0; i--)   // newest saves first
                {
                    var item = items[i];
                    if (png.ContainsKey(item.Host)) continue;
                    if (misses.TryGetValue(item.Host, out var missed) && now - missed < MissRetry) continue;
                    if (!asked.TryGetValue(item.Host, out var pages)) asked[item.Host] = pages = [];
                    if (pages.Count < 3) pages.Add(item.Url);
                }
                if (!pruned && folder is not null) keep = hosts;   // the PNG folder is trimmed to saved hosts once a launch, even to none
                if (asked.Count > 0 || keep is not null) { running = true; pruned = true; start = true; }
            }
        }
        if (gone.Count > 0) { RemoveFiles(gone); Changed?.Invoke(); }
        if (start) run(() => Fetch(asked, keep));
    }

    void Fetch(Dictionary<string, List<string>> asked, HashSet<string>? keep)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var missing = new List<string>();
        try
        {
            if (folder is not null && keep is not null) Prune(keep);
            var lookUp = new Dictionary<string, List<string>>(asked, StringComparer.Ordinal);
            if (folder is not null)
            {
                // A kept PNG wins until its site leaves the list.
                foreach (var host in asked.Keys)
                {
                    try
                    {
                        var data = File.ReadAllBytes(FileFor(host));
                        if (accept(data)) { found[host] = data; lookUp.Remove(host); }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            if (lookUp.Count > 0)
            {
                foreach (var (host, outcome) in FaviconStore.Icons(lookUp, databases(), accept))
                {
                    switch (outcome)
                    {
                        case FaviconOutcome.Found f:
                            found[host] = f.Png;
                            Keep(host, f.Png);
                            break;
                        case FaviconOutcome.Missing: missing.Add(host); break;
                    }   // RetryLater: asked again on the next show or save
                }
            }
        }
        finally { Finish(found, missing); }
    }

    void Finish(Dictionary<string, byte[]> found, List<string> missing)
    {
        IReadOnlyList<Item> again = [];
        bool changed;
        List<string> stale;
        lock (gate)
        {
            running = false;
            var hosts = latest.Select(i => i.Host).ToHashSet(StringComparer.Ordinal);
            // Hosts deleted while the lookup ran are left out, and the PNGs it just kept for them go too.
            stale = found.Keys.Where(h => !hosts.Contains(h)).ToList();
            var now = clock();
            foreach (var (host, data) in found) if (hosts.Contains(host)) png[host] = data;
            foreach (var host in missing) if (hosts.Contains(host)) misses[host] = now;
            changed = found.Keys.Any(hosts.Contains);
            if (runAgain) { runAgain = false; again = latest; }
        }
        if (stale.Count > 0) RemoveFiles(stale);
        if (changed) Changed?.Invoke();
        if (again.Count > 0) Update(again);
    }

    /// A host as a file name: letters, digits, dots and hyphens stay, anything else is its code.
    string FileFor(string host) => Path.Combine(folder!, string.Concat(host.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c.ToString() : "_" + ((int)c).ToString("x", System.Globalization.CultureInfo.InvariantCulture))) + ".png");

    void Keep(string host, byte[] data)
    {
        if (folder is null) return;
        try
        {
            Directory.CreateDirectory(folder);
            var path = FileFor(host);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the icon still shows this session
    }

    void RemoveFiles(IEnumerable<string> hosts)
    {
        if (folder is null) return;
        foreach (var host in hosts)
        {
            try { File.Delete(FileFor(host)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    void Prune(HashSet<string> hosts)
    {
        if (!Directory.Exists(folder)) return;
        var kept = hosts.Select(h => Path.GetFileName(FileFor(h))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder!))
        {
            try { if (!kept.Contains(Path.GetFileName(file))) File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
