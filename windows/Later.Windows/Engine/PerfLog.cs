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

    [Event(6, Level = EventLevel.Informational)]
    public void VisitDays(int sites, int regularSites, double ms)
    {
        if (IsEnabled()) WriteEvent(6, sites, regularSites, ms);
    }

    [Event(7, Level = EventLevel.Informational)]
    public void RegularsMigrated(int migrated, int of)
    {
        if (IsEnabled()) WriteEvent(7, migrated, of);
    }

    [Event(9, Level = EventLevel.Informational)]
    public void Armed(string browser, double seconds)
    {
        if (IsEnabled()) WriteEvent(9, browser, seconds);
    }

    /// Something woke the process: `kind` says what, nothing else. The idle gate counts these (see `Footprint.Listen`).
    [Event(10, Level = EventLevel.Informational)]
    public void Wake(string kind)
    {
        if (IsEnabled()) WriteEvent(10, kind);
    }

    [Event(8, Level = EventLevel.Informational)]
    public void PlacesRefreshed(int backToList, int regulars, int done, int items)
    {
        if (IsEnabled()) WriteEvent(8, backToList, regulars, done, items);
    }
}
