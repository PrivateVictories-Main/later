using System.Security.Cryptography;
using System.Text;

namespace Later.Windows.Platform;

public sealed record AppPaths(string DataDir)
{
    public static AppPaths Default
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("LATER_DATA_DIR") is { Length: > 0 } dir) return new AppPaths(dir);
#endif
            return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Later"));
        }
    }

    public string Library => Path.Combine(DataDir, "library.json");
    public string Settings => Path.Combine(DataDir, "settings.json");
    /// What the background process tells the app when it could not do its job (see `AgentStatusFile`).
    public string AgentStatus => Path.Combine(DataDir, "agent-status.json");
    public string IconsDir => Path.Combine(DataDir, "icons");

    /// Unique per data folder, so a test run never touches a real install's mutex, event or instance key.
    public string InstanceSuffix => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDir.ToUpperInvariant())))[..12];
}
