namespace Later.Windows.Platform;

public sealed class Debouncer : IDisposable
{
    readonly Timer timer;
    readonly TimeSpan delay;

    public Debouncer(TimeSpan delay, Action fire) { this.delay = delay; timer = new Timer(_ => fire(), null, Timeout.Infinite, Timeout.Infinite); }

    /// Re-arms the one-shot timer. While nothing pokes it, no timer is scheduled and nothing wakes.
    public void Poke() => timer.Change(delay, Timeout.InfiniteTimeSpan);

    public void Dispose() => timer.Dispose();
}
