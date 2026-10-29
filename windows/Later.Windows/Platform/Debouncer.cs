namespace Later.Windows.Platform;

/// A pause-then-fire timer. `fire` never overlaps itself: a poke that arrives while it runs fires once more after it ends.
public sealed class Debouncer : IDisposable
{
    readonly Timer timer;
    readonly TimeSpan delay;
    readonly Action fire;
    readonly object gate = new();
    bool running, again, disposed;

    public Debouncer(TimeSpan delay, Action fire)
    {
        this.delay = delay; this.fire = fire;
        timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// Re-arms the one-shot timer. While nothing pokes it, no timer is scheduled and nothing wakes. After `Dispose` it does nothing.
    public void Poke()
    {
        lock (gate)
        {
            if (disposed) return;
            if (running) again = true;
            else timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire()
    {
        lock (gate)
        {
            if (disposed || running) return;
            running = true;
        }
        try { fire(); }
        finally
        {
            lock (gate)
            {
                running = false;
                if (again && !disposed) { again = false; timer.Change(delay, Timeout.InfiniteTimeSpan); }
            }
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        timer.Dispose();
    }
}
