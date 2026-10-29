using System.Diagnostics.Tracing;

namespace Later.Windows.Engine;

/// Counts and timings only, never a link or a title. An ETW/EventSource listener sees them; nothing is written to disk.
[EventSource(Name = "Later-Engine")]
public sealed class PerfLog : EventSource
{
    public static readonly PerfLog Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Ring(string browser, double readMs, double judgeMs, int tabs, int skippedPrivate, int unresolved, int items)
    {
        if (IsEnabled()) WriteEvent(1, browser, readMs, judgeMs, tabs, skippedPrivate, unresolved, items);
    }

    [Event(2, Level = EventLevel.Informational)]
    public void HistoryAsked(int asked, int found, int changed, double ms)
    {
        if (IsEnabled()) WriteEvent(2, asked, found, changed, ms);
    }

    [Event(3, Level = EventLevel.Error)]
    public void SaveFailed()
    {
        if (IsEnabled()) WriteEvent(3);
    }

    [Event(4, Level = EventLevel.Informational)]
    public void Opened(int merged, int items, int changed, int toTools)
    {
        if (IsEnabled()) WriteEvent(4, merged, items, changed, toTools);
    }

    [Event(5, Level = EventLevel.Warning)]
    public void ReadFailed(string browser, string errorClass)
    {
        if (IsEnabled()) WriteEvent(5, browser, errorClass);
    }
}
