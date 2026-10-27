namespace Later.Core.Tests;

/// A scratch folder that removes itself, so tests leave nothing on disk.
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "later-tests-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    public TempDir() => Directory.CreateDirectory(Path);

    public string At(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
