using System.Buffers.Binary;
using System.Text;
using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class PreviousSessionTests
{
    /// Builds a Chromium session file the way the browser writes one.
#pragma warning disable CA1001 // a MemoryStream holds no resource that needs releasing
    sealed class Snss
    {
        readonly MemoryStream bytes = new();
        public Snss(int version = 3) { bytes.Write("SNSS"u8); Int(bytes, version); }

        static void Int(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }
        static void Pad(Stream s, int n) { for (int i = n; i % 4 != 0; i++) s.WriteByte(0); }

        void Command(int id, byte[] payload)
        {
            Span<byte> size = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)(payload.Length + 1));
            bytes.Write(size); bytes.WriteByte((byte)id); bytes.Write(payload);
        }

        static byte[] Ints(params int[] values) { var m = new MemoryStream(); foreach (var v in values) Int(m, v); return m.ToArray(); }

        public Snss TabWindow(int window, int tab) { Command(0, Ints(window, tab)); return this; }
        public Snss TabIndex(int tab, int index) { Command(2, Ints(tab, index)); return this; }
        public Snss Selected(int tab, int nav) { Command(7, Ints(tab, nav)); return this; }
        public Snss TabClosed(int tab) { Command(16, [.. Ints(tab), .. new byte[8]]); return this; }
        public Snss Navigation(int tab, int nav, string url, string title)
        {
            var body = new MemoryStream();
            Int(body, tab); Int(body, nav);
            var u = Encoding.UTF8.GetBytes(url); Int(body, u.Length); body.Write(u); Pad(body, u.Length);
            var t = Encoding.Unicode.GetBytes(title); Int(body, title.Length); body.Write(t); Pad(body, t.Length);
            Int(body, 0);   // a field the parser does not need
            var pickle = new MemoryStream(); Int(pickle, (int)body.Length); body.Position = 0; body.CopyTo(pickle);
            Command(6, pickle.ToArray());
            return this;
        }
        public byte[] ToArray() => bytes.ToArray();
    }
#pragma warning restore CA1001

    static Snss TwoTabsAndAClosedOne() => new Snss()
        .TabWindow(1, 10).TabIndex(10, 0).Navigation(10, 0, "https://a.example/old", "Old").Navigation(10, 1, "https://a.example/one", "Page one").Selected(10, 1)
        .TabWindow(1, 11).TabIndex(11, 1).Navigation(11, 0, "https://a.example/two", "Page two - ünï").Selected(11, 0)
        .TabWindow(1, 12).TabIndex(12, 2).Navigation(12, 0, "https://a.example/gone", "Gone").Selected(12, 0).TabClosed(12);

    [Fact]
    public void LiveTabsComeBackWithTheirSelectedNavigation()
    {
        var tabs = PreviousSession.Parse(TwoTabsAndAClosedOne().ToArray()).OrderBy(t => t.Index).ToList();
        Assert.Equal(2, tabs.Count);
        Assert.Equal(new SessionTab(1, 0, "https://a.example/one", "Page one"), tabs[0]);
        Assert.Equal(new SessionTab(1, 1, "https://a.example/two", "Page two - ünï"), tabs[1]);
    }

    [Fact]
    public void ATruncatedFileKeepsWhatParsedSoFar()
    {
        var bytes = TwoTabsAndAClosedOne().ToArray();
        var tabs = PreviousSession.Parse(bytes.AsSpan(0, bytes.Length - 5));
        Assert.Contains(tabs, t => t.Url == "https://a.example/one");
    }

    [Fact]
    public void ANonSessionFileGivesNothing()
    {
        Assert.Empty(PreviousSession.Parse("not a session file at all"u8));
        Assert.Empty(PreviousSession.Parse(new Snss(version: 9).TabWindow(1, 1).ToArray()));
        Assert.Empty(PreviousSession.Parse([]));
    }

    [Fact]
    public void HugeLengthsInTheFileDoNotThrow()
    {
        var bad = new Snss().TabWindow(1, 1).ToArray().ToList();
        // a navigation whose url length claims 2 GB
        var payload = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), int.MaxValue);
        bad.AddRange([(byte)(payload.Length + 1), 0, 6]); bad.AddRange(payload);
        Assert.Empty(PreviousSession.Parse(bad.ToArray()));
    }

    [Fact]
    public void ReadTakesTheNewestReadableFileWithTabs()
    {
        using var dir = new TempDir();
        var sessions = dir.At(Path.Combine("Default", "Sessions")); Directory.CreateDirectory(sessions);
        var older = Path.Combine(sessions, "Session_13400000000000001"); var newer = Path.Combine(sessions, "Session_13400000000000002");
        File.WriteAllBytes(older, TwoTabsAndAClosedOne().ToArray());
        Assert.Equal(2, PreviousSession.Read(dir.Path).Count);                 // a lone file that nobody holds is the previous run's
        File.WriteAllBytes(newer, new Snss().ToArray());
        Assert.Equal(2, PreviousSession.Read(dir.Path).Count);                 // a newer file with no tabs yet is passed over
        File.WriteAllBytes(newer, new Snss().TabWindow(1, 5).TabIndex(5, 0).Navigation(5, 0, "https://a.example/new", "New").Selected(5, 0).ToArray());
        Assert.Equal("https://a.example/new", Assert.Single(PreviousSession.Read(dir.Path)).Url);
        using (new FileStream(newer, FileMode.Open, FileAccess.Read, FileShare.None))   // the running browser holds its file exclusively
            Assert.Equal(2, PreviousSession.Read(dir.Path).Count);
        Assert.Empty(PreviousSession.Read(dir.At("missing")));
    }
}
