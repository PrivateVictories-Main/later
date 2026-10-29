using System.Runtime.InteropServices;
using Later.Windows.Native;

namespace Later.Windows.Tests;

/// Builds browser-shaped databases for the lookup tests. The product only ever opens them read-only.
internal static partial class TestSqlite
{
    [LibraryImport("winsqlite3", EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Exec(nint db, string sql, nint callback, nint arg, nint error);

    public static void Exec(string path, string sql)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int code = SqliteNative.open_v2(path, out var db, 0x2 | 0x4 /*READWRITE | CREATE*/, 0);
        try
        {
            if (code != 0) throw new InvalidOperationException($"open failed: {code}");
            code = Exec(db, sql, 0, 0, 0);
            if (code != 0) throw new InvalidOperationException($"exec failed: {code}");
        }
        finally { _ = SqliteNative.close_v2(db); }
    }
}
