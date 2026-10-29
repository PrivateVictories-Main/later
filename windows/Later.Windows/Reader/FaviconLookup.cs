using Later.Core;
using Later.Windows.Native;

namespace Later.Windows.Reader;

public abstract record FaviconOutcome
{
    /// PNG bytes of the host's icon, its largest bitmap.
    public sealed record Found(byte[] Png) : FaviconOutcome;
    /// No profile has an icon for the host.
    public sealed record Missing : FaviconOutcome;
    /// A read failed, as an immutable read can while the browser writes; ask again later.
    public sealed record RetryLater : FaviconOutcome;
}

/// Site icons from a Chromium browser's own icon cache: each profile's `Favicons` file, opened read-only and immutable
/// (no locks, no journal, nothing written). It's asked only about saved pages' hosts and never lists the cache.
public static class FaviconStore
{
    /// The `Favicons` file of each of a Chromium data folder's profiles.
    public static IReadOnlyList<string> Databases(string dataFolder) => ProfileFiles.In(dataFolder, "Favicons");

    /// Each host's icon: one of its saved pages' own, else its home page's, else the icon most of the host's pages use,
    /// with or without `www.`. `pagesByHost` maps a host to its saved links, newest first. The first profile with an icon
    /// wins. A bitmap `accept` turns down (one that won't decode) is passed over for the next one: a smaller size, then the
    /// next fallback, then the next profile.
    public static Dictionary<string, FaviconOutcome> Icons(IReadOnlyDictionary<string, List<string>> pagesByHost, IReadOnlyList<string> databases, Func<byte[], bool>? accept = null)
    {
        accept ??= _ => true;
        var outcomes = pagesByHost.Keys.ToDictionary(h => h, _ => (FaviconOutcome)new FaviconOutcome.Missing(), StringComparer.Ordinal);
        foreach (var path in databases)
        {
            var open = outcomes.Where(kv => kv.Value is not FaviconOutcome.Found).Select(kv => kv.Key).ToList();
            if (open.Count == 0) break;
            try
            {
                using var db = new IconDb(path);
                foreach (var host in open)
                    if (db.Icon(host, pagesByHost[host], accept) is { } data) outcomes[host] = new FaviconOutcome.Found(data);
            }
            catch (SqliteException e) when (e.IsTransient)
            {
                foreach (var host in open) if (outcomes[host] is FaviconOutcome.Missing) outcomes[host] = new FaviconOutcome.RetryLater();
            }
            catch (SqliteException) { }   // not a usable icon cache (no such file, another schema): it has no icons to give
        }
        return outcomes;
    }

    /// One profile's icon cache, open for one lookup. Closed when disposed, also when opening it failed halfway.
    sealed class IconDb : IDisposable
    {
        readonly SqliteDb db;
        readonly SqliteStatement exact;
        readonly SqliteStatement site;

        public IconDb(string path)
        {
            db = new SqliteDb(path);
            try
            {
                exact = db.Prepare(@"SELECT b.image_data FROM icon_mapping m JOIN favicon_bitmaps b ON b.icon_id = m.icon_id
                    WHERE m.page_url = ?1 AND b.width > 0 AND length(b.image_data) > 0
                    ORDER BY b.width DESC");
                // The icon most of the host's pages map to, with and without www. counted together, then its largest bitmap.
                // The ranges keep to the page_url index.
                site = db.Prepare(@"SELECT b.image_data FROM favicon_bitmaps b JOIN (
                        SELECT icon_id, count(*) AS pages FROM icon_mapping
                        WHERE (page_url >= ?1 AND page_url < ?2) OR (page_url >= ?3 AND page_url < ?4) GROUP BY icon_id
                    ) m ON b.icon_id = m.icon_id
                    WHERE b.width > 0 AND length(b.image_data) > 0
                    ORDER BY m.pages DESC, b.width DESC");
            }
            catch { exact?.Dispose(); db.Dispose(); throw; }
        }

        public void Dispose() { exact.Dispose(); site.Dispose(); db.Dispose(); }

        public byte[]? Icon(string host, List<string> pages, Func<byte[], bool> accept)
        {
            var scheme = pages.Select(p => ParsedUrl.Parse(p)?.Scheme).FirstOrDefault(s => s is not null) ?? "https";
            string[] names = [host, "www." + host];
            // A saved page's own icon, then the site's home page's: its usual icon, where a page here and there can carry
            // a variant (a badge, a player state) that even most of the host's pages share.
            foreach (var page in pages.Take(3).Concat(names.Select(n => $"{scheme}://{n}/")))
                if (First(exact, accept, page) is { } data) return data;
            // Every link on the host sorts between "https://host/" and "https://host0" ("0" follows "/").
            return First(site, accept, names.SelectMany(n => new[] { $"{scheme}://{n}/", $"{scheme}://{n}0" }).ToArray());
        }

        /// The first bitmap the statement finds that `accept` takes.
        static byte[]? First(SqliteStatement statement, Func<byte[], bool> accept, params string[] values)
        {
            try
            {
                for (int i = 0; i < values.Length; i++) statement.Bind(i + 1, values[i]);
                while (statement.Step())
                    if (statement.Blob(0) is { } bytes && accept(bytes)) return bytes;
                return null;
            }
            finally { statement.Reset(); }
        }
    }
}
