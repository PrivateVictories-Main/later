// A thin wrapper over the system SQLite (winsqlite3.dll). No NuGet package, nothing written to the databases.
using System.Runtime.InteropServices;

namespace Later.Windows.Native;

internal sealed class SqliteException(int code) : Exception($"SQLite error {code}")
{
    public int Code { get; } = code;

    /// Failures a read can hit while the browser is writing the file, which pass once it is done.
    public bool IsTransient => (Code & 0xff) is 5 or 6 or 10 or 11 or 15 or 17 or 26;   // BUSY LOCKED IOERR CORRUPT PROTOCOL SCHEMA NOTADB
}

internal static partial class SqliteNative
{
    const string Lib = "winsqlite3";
    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)] internal static partial int open_v2(string file, out nint db, int flags, nint vfs);
    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")] internal static partial int close_v2(nint db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)] internal static partial int prepare_v2(nint db, string sql, int n, out nint stmt, nint tail);
    [LibraryImport(Lib, EntryPoint = "sqlite3_finalize")] internal static partial int finalize(nint stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_step")] internal static partial int step(nint stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_reset")] internal static partial int reset(nint stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_clear_bindings")] internal static partial int clear_bindings(nint stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_text", StringMarshalling = StringMarshalling.Utf8)] internal static partial int bind_text(nint stmt, int i, string value, int n, nint destructor);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_int64")] internal static partial int bind_int64(nint stmt, int i, long value);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_int")] internal static partial int column_int(nint stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_int64")] internal static partial long column_int64(nint stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_blob")] internal static partial nint column_blob(nint stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_bytes")] internal static partial int column_bytes(nint stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_text")] internal static partial nint column_text(nint stmt, int col);
}

/// A read-only, immutable connection: no locks, no journal, nothing written.
internal sealed class SqliteDb : IDisposable
{
    nint db;

    public SqliteDb(string path)
    {
        var uri = new Uri(path).AbsoluteUri + "?immutable=1&mode=ro";
        int code = SqliteNative.open_v2(uri, out db, 0x1 /*READONLY*/ | 0x40 /*URI*/, 0);
        if (code != 0) { Dispose(); throw new SqliteException(code); }
    }

    public SqliteStatement Prepare(string sql)
    {
        int code = SqliteNative.prepare_v2(db, sql, -1, out var stmt, 0);
        if (code != 0) throw new SqliteException(code);
        return new SqliteStatement(stmt);
    }

    public void Dispose() { if (db != 0) { _ = SqliteNative.close_v2(db); db = 0; } }
}

internal sealed class SqliteStatement(nint stmt) : IDisposable
{
    static readonly nint Transient = new(-1);   // SQLITE_TRANSIENT

    public SqliteStatement Bind(int i, string v) { _ = SqliteNative.bind_text(stmt, i, v, -1, Transient); return this; }
    public SqliteStatement Bind(int i, long v) { _ = SqliteNative.bind_int64(stmt, i, v); return this; }

    /// True for a row, false at the end; throws SqliteException otherwise.
    public bool Step() { int c = SqliteNative.step(stmt); return c == 100 ? true : c == 101 ? false : throw new SqliteException(c); }
    public void Reset() { _ = SqliteNative.reset(stmt); _ = SqliteNative.clear_bindings(stmt); }
    public int Int(int col) => SqliteNative.column_int(stmt, col);
    public long Long(int col) => SqliteNative.column_int64(stmt, col);
    public string Text(int col) => Marshal.PtrToStringUTF8(SqliteNative.column_text(stmt, col)) ?? "";

    public byte[]? Blob(int col)
    {
        var p = SqliteNative.column_blob(stmt, col); if (p == 0) return null;
        var bytes = new byte[SqliteNative.column_bytes(stmt, col)]; Marshal.Copy(p, bytes, 0, bytes.Length); return bytes;
    }

    public void Dispose() => _ = SqliteNative.finalize(stmt);
}
