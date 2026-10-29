using Later.Windows.Native;

namespace Later.Windows.Engine;

/// Private memory at named points of the agent's start, printed when LATER_AGENT_TRACE is 1; otherwise a no-op.
/// Counts only. Used to see where the agent's memory goes if it ever outgrows its budget.
public static class Footprint
{
    public static unsafe void Mark(string label)
    {
        if (Environment.GetEnvironmentVariable("LATER_AGENT_TRACE") != "1") return;
        long bytes = NativeApi.GetProcessMemoryInfo(NativeApi.GetCurrentProcess(), out var c, (uint)sizeof(ProcessMemory)) != 0 ? (long)c.PrivateUsage : -1;
        Console.Out.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"TRACE {label}: private={bytes / 1048576.0:F2} MB"));
        Console.Out.Flush();
    }
}
