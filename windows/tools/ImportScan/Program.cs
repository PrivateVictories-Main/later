using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// ImportScan <folder> [--expected file] [--write file]
// Ours (file names starting with "Later"): any import of a banned native DLL (PE imports, delay imports and managed P/Invoke
// targets), or any reference to a System.Net.* assembly, is a violation (exit 1). So is any System.Net.*.dll, msquic.dll or
// createdump.exe anywhere in the folder: the app ships no network-capable runtime files. Every other file that imports a banned DLL (the Windows App SDK runtime) is listed; with
// --expected the list must equal the file (exit 2 on drift, 3 when the file does not exist yet, printing what to record).
string[] neverShipped = ["msquic.dll", "createdump.exe"];
string[] banned = ["winhttp.dll", "wininet.dll", "ws2_32.dll", "dnsapi.dll", "mswsock.dll", "wsock32.dll", "urlmon.dll", "httpapi.dll"];

if (args.Length == 0) { Console.Error.WriteLine("usage: ImportScan <folder> [--expected file] [--write file]"); return 64; }
string root = Path.GetFullPath(args[0]); string? expectedPath = null, writePath = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--expected") expectedPath = args[++i];
    else if (args[i] == "--write") writePath = args[++i];
}

var violations = new List<string>();
var others = new SortedDictionary<string, string>(StringComparer.Ordinal);
int scanned = 0, ours = 0;
foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
             .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
             .OrderBy(f => f, StringComparer.Ordinal))
{
    var fileName = Path.GetFileName(file);
    if (fileName.StartsWith("System.Net.", StringComparison.OrdinalIgnoreCase) || neverShipped.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        violations.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')} is a network-capable runtime file and must not be published");
    var info = Read(file);
    if (info is null) continue;
    scanned++;
    var bad = info.Value.Native.Where(n => banned.Contains(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
    var name = Path.GetRelativePath(root, file).Replace('\\', '/');
    if (Path.GetFileName(file).StartsWith("Later", StringComparison.OrdinalIgnoreCase))
    {
        ours++;
        if (bad.Count > 0) violations.Add($"{name} imports {string.Join(", ", bad)}");
        foreach (var r in info.Value.ManagedRefs.Where(r => r.StartsWith("System.Net.", StringComparison.Ordinal)).Distinct())
            violations.Add($"{name} references assembly {r}");
    }
    else if (bad.Count > 0) others[name] = string.Join(",", bad);
}

Console.WriteLine($"ImportScan: scanned {scanned} files, {ours} of them ours");
foreach (var v in violations) Console.WriteLine($"VIOLATION: {v}");
var listing = string.Join('\n', others.Select(kv => $"{kv.Key}: {kv.Value}")) + (others.Count > 0 ? "\n" : "");
Console.WriteLine("others with network imports (documented, not edited):");
Console.Write(listing.Length == 0 ? "  (none)\n" : listing);
if (writePath is not null) File.WriteAllText(writePath, listing);
if (violations.Count > 0) return 1;
Console.WriteLine("ours: clean");
if (expectedPath is null) return 0;
if (!File.Exists(expectedPath)) { Console.WriteLine($"RECORD THIS in {expectedPath}:\n{listing}"); return 3; }
var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n", StringComparison.Ordinal);
if (expected != listing) { Console.WriteLine($"DRIFT from {expectedPath}"); return 2; }
return 0;

(List<string> Native, List<string> ManagedRefs)? Read(string path)
{
    try
    {
        var bytes = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(bytes));
        var h = pe.PEHeaders;
        if (h.PEHeader is not { } header) return null;
        var native = new List<string>();
        void Imports(DirectoryEntry dir, int size, int nameOffset)
        {
            if (dir.Size == 0 || Offset(dir.RelativeVirtualAddress) is not { } start) return;
            for (int p = start; p + size <= bytes.Length; p += size)
            {
                if (bytes.AsSpan(p, size).IndexOfAnyExcept((byte)0) < 0) break;
                if (Offset(BitConverter.ToInt32(bytes, p + nameOffset)) is { } n) native.Add(Ascii(bytes, n).ToLowerInvariant());
            }
        }
        int? Offset(int rva)
        {
            foreach (var s in h.SectionHeaders)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)) return rva - s.VirtualAddress + s.PointerToRawData;
            return null;
        }
        Imports(header.ImportTableDirectory, 20, 12);        // IMAGE_IMPORT_DESCRIPTOR.Name
        Imports(header.DelayImportTableDirectory, 32, 4);    // IMAGE_DELAYLOAD_DESCRIPTOR.DllNameRVA
        var managed = new List<string>();
        if (pe.HasMetadata)
        {
            var md = pe.GetMetadataReader();
            foreach (var r in md.AssemblyReferences) managed.Add(md.GetString(md.GetAssemblyReference(r).Name));
            // P/Invoke targets (ModuleRef rows): a managed [DllImport("ws2_32")] has no PE import, only this metadata.
            for (int i = 1, n = md.GetTableRowCount(TableIndex.ModuleRef); i <= n; i++)
            {
                var module = Path.GetFileName(md.GetString(md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(i)).Name)).TrimEnd('.').ToLowerInvariant();
                native.Add(Path.HasExtension(module) ? module : module + ".dll");   // the loader appends .dll to a bare name
            }
        }
        return (native, managed);
    }
    catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException) { return null; }   // not a PE file
}

static string Ascii(byte[] bytes, int offset)
{
    int end = offset; while (end < bytes.Length && bytes[end] != 0) end++;
    return Encoding.ASCII.GetString(bytes, offset, end - offset);
}
