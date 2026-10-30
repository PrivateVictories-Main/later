using System.Globalization;

namespace Later.Core;

/// One JSON file. Every save is complete-or-nothing (temp file, flush to disk, atomic replace), the previous version is
/// kept as `library.json.bak`, and an unreadable file is kept, never overwritten.
/// Unlike the Mac's store: no 0600 (the profile's ACL already makes the folder private), no folder fsync (NTFS journals
/// its metadata), File.Replace instead of link()+rename() for the backup, and no one-time safety copies.
public sealed class LibraryStore
{
    public string FilePath { get; }
    string BackupPath => FilePath + ".bak";
    string TempPath => FilePath + ".tmp";
    bool primaryKnownGood;   // this process read or wrote the primary file itself, so a save needn't decode it first

    public LibraryStore(string filePath) => FilePath = filePath;

    public static LibraryStore AppDefault()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Later");
        Directory.CreateDirectory(dir);
        return new LibraryStore(Path.Combine(dir, "library.json"));
    }

    public sealed record Loaded(LibraryData Data, string? Notice, bool Writable = true);

    public enum Health
    {
        /// The list reads, or there is none yet.
        Fine,
        /// The list is damaged, or missing with a backup: `Load()` would set it aside or restore it, and say so.
        NeedsRecovery,
        /// The list could not be opened (locked, an I/O error); nothing says it is damaged, so it is worth asking again later.
        Unreadable,
    }

    /// What `Load()` would find, touching no file. The background helper uses it to leave recovery to the window app (which tells
    /// the user) and to wait out a list that is only locked.
    public Health Check()
    {
        if (Decode(FilePath, out var unreadable) is not null) return Health.Fine;
        if (unreadable) return Health.Unreadable;
        if (File.Exists(FilePath)) return Health.NeedsRecovery;   // malformed
        if (!File.Exists(BackupPath)) return Health.Fine;         // a new list
        Decode(BackupPath, out var backupUnreadable);
        return backupUnreadable ? Health.Unreadable : Health.NeedsRecovery;
    }

    /// The list as it is on disk, touching no file: for a process that does not hold the engine lock, so it neither recovers nor
    /// saves. Falls back to the backup copy, then to an empty list.
    public Loaded Peek(string notice) => new(Decode(FilePath, out _) ?? Decode(BackupPath, out _) ?? new LibraryData(), notice, Writable: false);

    public Loaded Load()
    {
        if (Decode(FilePath, out var unreadable) is { } data) { primaryKnownGood = true; return new Loaded(data, null); }
        primaryKnownGood = false;
        if (unreadable) return Unwritable("Later couldn't read its list just now. It will try again.");   // locked or I/O error, not damage: touch nothing
        string? keptAs = null;
        if (File.Exists(FilePath))
        {
            var name = $"library.corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}.json";
            try { File.Move(FilePath, Path.Combine(Path.GetDirectoryName(FilePath)!, name)); }
            catch (IOException) { return Unwritable(CantSetAside); }
            catch (UnauthorizedAccessException) { return Unwritable(CantSetAside); }
            keptAs = name;
        }
        if (Decode(BackupPath, out _) is { } backup) return new Loaded(backup, "Your list was restored from its backup copy.");
        if (keptAs is not null) return new Loaded(new LibraryData(), $"Your list couldn't be read, so a new one was started. The old file was kept as {keptAs}.");
        return new Loaded(new LibraryData(), null);
    }

    const string CantSetAside = "Later couldn't set aside a damaged copy of your list, so it won't save changes until it can. Nothing has been deleted.";

    Loaded Unwritable(string notice) => new(Decode(BackupPath, out _) ?? new LibraryData(), notice, Writable: false);

    /// Throws IOException when the save can't complete; the files on disk are then as they were.
    public void Save(LibraryData data)
    {
        var bytes = LibraryJson.Encode(data);
        if (!primaryKnownGood && File.Exists(FilePath) && Decode(FilePath, out _) is null)   // never rotate an unreadable list over the good backup
            throw new IOException("The list on disk can't be read, so it was not overwritten.");
        try
        {
            using (var f = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                f.Write(bytes);
                f.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(TempPath, FilePath, BackupPath, ignoreMetadataErrors: true);   // old primary becomes the backup
            else File.Move(TempPath, FilePath);
        }
        catch (UnauthorizedAccessException e) { throw new IOException("The list couldn't be saved.", e); }
        primaryKnownGood = true;
    }

    public void Reset()
    {
        foreach (var path in new[] { FilePath, BackupPath, TempPath }) if (File.Exists(path)) File.Delete(path);
    }

    /// Null when the file is missing, malformed (`unreadable` false) or couldn't be opened or read, e.g. locked (`unreadable` true).
    /// Opened with full sharing so a read never blocks another process's File.Replace and vice versa; only a malformed file may be set aside.
    static LibraryData? Decode(string path, out bool unreadable)
    {
        unreadable = false;
        byte[] bytes;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[f.Length];
            f.ReadExactly(bytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable = true; return null; }
        try { return LibraryJson.Decode(bytes); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return null; }   // ArgumentException: out-of-range dates
    }
}
