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

    public Loaded Load()
    {
        if (Decode(FilePath) is { } data) { primaryKnownGood = true; return new Loaded(data, null); }
        primaryKnownGood = false;
        string? keptAs = null;
        if (File.Exists(FilePath))
        {
            var name = $"library.corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}.json";
            try { File.Move(FilePath, Path.Combine(Path.GetDirectoryName(FilePath)!, name)); }
            catch (IOException) { return Unwritable(); }
            catch (UnauthorizedAccessException) { return Unwritable(); }
            keptAs = name;
        }
        if (Decode(BackupPath) is { } backup) return new Loaded(backup, "Your list was restored from its backup copy.");
        if (keptAs is not null) return new Loaded(new LibraryData(), $"Your list couldn't be read, so a new one was started. The old file was kept as {keptAs}.");
        return new Loaded(new LibraryData(), null);
    }

    Loaded Unwritable() => new(Decode(BackupPath) ?? new LibraryData(),
        "Later couldn't set aside a damaged copy of your list, so it won't save changes until it can. Nothing has been deleted.", Writable: false);

    /// Throws IOException when the save can't complete; the files on disk are then as they were.
    public void Save(LibraryData data)
    {
        var bytes = LibraryJson.Encode(data);
        if (!primaryKnownGood && File.Exists(FilePath) && Decode(FilePath) is null)   // never rotate an unreadable list over the good backup
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

    static LibraryData? Decode(string path)
    {
        try { return LibraryJson.Decode(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }
}
