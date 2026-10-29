namespace Later.Windows.Engine;

/// The engine over a real `EngineThread`: posted work runs there, one-shot timers post back to it, background work uses the pool.
public sealed class ThreadHost(EngineThread thread) : IEngineHost
{
    public void Post(Action a) => thread.Post(a);

    public IDisposable After(TimeSpan delay, Action a) => new OneShot(thread, delay, a);

    public void RunBackground(Action work) => ThreadPool.QueueUserWorkItem(_ => work());

    /// A single timer shot that has not run once it is disposed, even when it had already fired and its action was still posted.
    sealed class OneShot : IDisposable
    {
        readonly Timer timer;
        volatile bool cancelled;

        public OneShot(EngineThread thread, TimeSpan delay, Action a) =>
            timer = new Timer(_ => { if (!cancelled) thread.Post(() => { if (!cancelled) a(); }); }, null, delay, Timeout.InfiniteTimeSpan);

        public void Dispose() { cancelled = true; timer.Dispose(); }
    }
}
