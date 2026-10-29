using System.Buffers.Binary;
using System.Text;

namespace Later.Windows.Reader;

public sealed record SessionTab(int WindowId, int Index, string Url, string Title);

public static class PreviousSession
{
    const long MaxFileBytes = 64 * 1024 * 1024;   // a session file is kilobytes; anything this big is not one

    sealed class TabState
    {
        public int Window = -1, Index, Selected = -1;
        public Dictionary<int, (string Url, string Title)> Navs = new();
    }

    /// Each profile's most recent `Session_*` file that can be read and holds tabs. The running browser keeps its own
    /// file locked (those are skipped), and a restarted browser may not have written a new one yet, so the newest readable
    /// file is the previous run's. Read with sharing allowed; nothing is written.
    public static IReadOnlyList<SessionTab> Read(string dataFolder)
    {
        var all = new List<SessionTab>();
        if (!Directory.Exists(dataFolder)) return all;
        foreach (var profile in Directory.EnumerateDirectories(dataFolder).Where(d => Path.GetFileName(d) is var n && (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal))))
        {
            var sessions = Path.Combine(profile, "Sessions");
            if (!Directory.Exists(sessions)) continue;
            foreach (var file in Directory.EnumerateFiles(sessions, "Session_*").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                try
                {
                    using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (f.Length > MaxFileBytes) continue;
                    var bytes = new byte[f.Length]; f.ReadExactly(bytes);
                    var tabs = Parse(bytes);
                    if (tabs.Count == 0) continue;
                    all.AddRange(tabs);
                    break;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return all;
    }

    public static IReadOnlyList<SessionTab> Parse(ReadOnlySpan<byte> d)
    {
        var tabs = new Dictionary<int, TabState>(); var closedTabs = new HashSet<int>(); var closedWindows = new HashSet<int>();
        if (d.Length < 8 || d[0] != (byte)'S' || d[1] != (byte)'N' || d[2] != (byte)'S' || d[3] != (byte)'S') return [];
        int version = I32(d, 4);
        if (version != 1 && version != 3) return [];
        for (int p = 8; p + 3 <= d.Length;)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]); int id = d[p + 2];
            if (size < 1 || p + 2 + size > d.Length) break;
            var payload = d.Slice(p + 3, size - 1); p += 2 + size;
            switch (id)
            {
                case 0 when payload.Length >= 8: Tab(tabs, I32(payload, 4)).Window = I32(payload, 0); break;
                case 2 when payload.Length >= 8: Tab(tabs, I32(payload, 0)).Index = I32(payload, 4); break;
                case 7 when payload.Length >= 8: Tab(tabs, I32(payload, 0)).Selected = I32(payload, 4); break;
                case 16 when payload.Length >= 4: closedTabs.Add(I32(payload, 0)); break;
                case 17 when payload.Length >= 4: closedWindows.Add(I32(payload, 0)); break;
                case 6 when payload.Length >= 16:
                {
                    int o = 4;   // skip the pickle's own length
                    int tabId = I32(payload, o), navIndex = I32(payload, o + 4); o += 8;
                    if (!ReadString(payload, ref o, out var url) || !ReadString16(payload, ref o, out var title)) break;
                    Tab(tabs, tabId).Navs[navIndex] = (url, title); break;
                }
            }
        }
        var result = new List<SessionTab>();
        foreach (var (tabId, t) in tabs)
        {
            if (closedTabs.Contains(tabId) || t.Window < 0 || closedWindows.Contains(t.Window)) continue;
            if (!t.Navs.TryGetValue(t.Selected, out var nav) && !t.Navs.TryGetValue(t.Navs.Keys.DefaultIfEmpty(-1).Max(), out nav)) continue;
            result.Add(new SessionTab(t.Window, t.Index, nav.Url, nav.Title));
        }
        return result;
    }

    static TabState Tab(Dictionary<int, TabState> tabs, int id) => tabs.TryGetValue(id, out var t) ? t : tabs[id] = new TabState();
    static int I32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s[o..]);
    static int Pad4(int n) => (n + 3) & ~3;

    // Lengths come from the file: compare against what is left, never add to the offset first (that can overflow).
    static bool ReadString(ReadOnlySpan<byte> s, ref int o, out string value)
    {
        value = ""; if (s.Length - o < 4) return false;
        int n = I32(s, o); o += 4; if (n < 0 || n > s.Length - o) return false;
        value = Encoding.UTF8.GetString(s.Slice(o, n)); o += Pad4(n); return true;
    }

    static bool ReadString16(ReadOnlySpan<byte> s, ref int o, out string value)
    {
        value = ""; if (s.Length - o < 4) return false;
        int n = I32(s, o); o += 4; if (n < 0 || n > (s.Length - o) / 2) return false;
        value = Encoding.Unicode.GetString(s.Slice(o, 2 * n)); o += Pad4(2 * n); return true;
    }
}
