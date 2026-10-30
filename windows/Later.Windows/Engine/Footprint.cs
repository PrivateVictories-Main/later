using Later.Windows.Native;

namespace Later.Windows.Engine;

/// Private memory at named points of the agent's start, printed when LATER_AGENT_TRACE is 1; otherwise a no-op.
/// Counts only. Used to see where the agent's memory goes if it ever outgrows its budget. With the same variable the agent also
/// prints each of its own counts-and-timings events (never a link or a title) and every wake-up by kind, so the idle gate can
/// say what woke it; nothing is written to disk by the agent, the caller redirects the output.
public static class Footprint
{
    static EventPrinter? printer;

    public static void Listen()
    {
        if (Environment.GetEnvironmentVariable("LATER_AGENT_TRACE") == "1") printer ??= new EventPrinter();
    }

    sealed class EventPrinter : System.Diagnostics.Tracing.EventListener
    {
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Later-Engine") EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Informational);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            Console.Out.WriteLine("EVT " + e.EventName + " " + (e.Payload is { } payload ? string.Join(" ", payload) : ""));
            Console.Out.Flush();
        }
    }

    public static unsafe void Mark(string label)
    {
        if (Environment.GetEnvironmentVariable("LATER_AGENT_TRACE") != "1") return;
        long bytes = NativeApi.GetProcessMemoryInfo(NativeApi.GetCurrentProcess(), out var c, (uint)sizeof(ProcessMemory)) != 0 ? (long)c.PrivateUsage : -1;
        Console.Out.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"TRACE {label}: private={bytes / 1048576.0:F2} MB"));
        Console.Out.Flush();
    }
}
