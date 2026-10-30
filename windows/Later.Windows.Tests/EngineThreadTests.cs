using Later.Windows.Engine;
using Later.Windows.Native;
using Xunit;

namespace Later.Windows.Tests;

public sealed class EngineThreadTests
{
    /// An engine thread running on a helper thread; `Handles` is what it waits on next.
    sealed class Running : IDisposable
    {
        public readonly EngineThread Engine = new();
        public volatile nint[] Handles = [];
        public readonly List<int> Signaled = [];
        public Action<int>? OnSignal;
        public int ExitedOn = -1;
        readonly Thread runner;
        readonly ManualResetEventSlim started = new();

        public Running()
        {
            runner = new Thread(() => Engine.Run(
                init: () => started.Set(),
                waitHandles: () => Handles,
                signaled: i => { lock (Signaled) Signaled.Add(i); OnSignal?.Invoke(i); },
                onExit: () => ExitedOn = Environment.CurrentManagedThreadId));
            runner.Start();
            Assert.True(started.Wait(10_000));
        }

        public void Wake() => Engine.Post(() => { });   // makes the loop ask for its handles again

        public bool Joined(int ms = 10_000) => runner.Join(ms);

        public void Dispose()
        {
            Engine.Quit(); runner.Join(10_000);
            Engine.Dispose(); started.Dispose();
        }
    }

    static bool WaitFor(Func<bool> condition, int ms = 10_000)
    {
        var end = Environment.TickCount64 + ms;
        while (!condition()) { if (Environment.TickCount64 > end) return false; Thread.Sleep(10); }
        return true;
    }

    [Fact]
    public void APostedActionRunsOnTheEngineThread()
    {
        using var run = new Running();
        int ranOn = -1; bool isEngine = false;
        run.Engine.Post(() => { ranOn = Environment.CurrentManagedThreadId; isEngine = run.Engine.IsEngineThread; });
        Assert.True(WaitFor(() => Volatile.Read(ref ranOn) != -1));
        Assert.True(isEngine);
        Assert.NotEqual(Environment.CurrentManagedThreadId, ranOn);
        Assert.False(run.Engine.IsEngineThread);
    }

    [Fact]
    public void ASignaledEventCallsSignaledZeroWithoutAnyMessage()
    {
        using var run = new Running();
        var ev = NativeApi.CreateEvent(0, 1, 0, null!);
        try
        {
            run.OnSignal = _ => NativeApi.ResetEvent(ev);   // manual reset: left set it would call again at once
            run.Handles = [ev]; run.Wake();
            _ = NativeApi.SetEvent(ev);
            Assert.True(WaitFor(() => { lock (run.Signaled) return run.Signaled.Count == 1; }));
            lock (run.Signaled) Assert.Equal([0], run.Signaled.ToArray());
        }
        finally { run.Handles = []; run.Wake(); _ = User32.CloseHandle(ev); }
    }

    [Fact]
    public void AnAbandonedMutexAlsoCallsSignaled()
    {
        using var run = new Running();
        var mutex = NativeApi.CreateMutex(0, 0, null!);
        try
        {
            var owner = new Thread(() => NativeApi.WaitForSingleObject(mutex, 5000));   // takes it and ends without releasing
            owner.Start(); owner.Join();
            run.OnSignal = _ => run.Handles = [];   // the wait now owns it; waiting again would take it again at once
            run.Handles = [mutex]; run.Wake();
            Assert.True(WaitFor(() => { lock (run.Signaled) return run.Signaled.Count == 1; }));
            lock (run.Signaled) Assert.Equal([0], run.Signaled.ToArray());
        }
        finally { run.Handles = []; run.Wake(); _ = User32.CloseHandle(mutex); }
    }

    [Fact]
    public void QuitRunsOnExitOnTheEngineThreadAndRunReturns()
    {
        var run = new Running();
        int engineThread = -1;
        run.Engine.Post(() => engineThread = Environment.CurrentManagedThreadId);
        Assert.True(WaitFor(() => Volatile.Read(ref engineThread) != -1));
        run.Engine.Quit();
        Assert.True(run.Joined());
        Assert.Equal(engineThread, run.ExitedOn);
        run.Dispose();
    }

    [Fact]
    public void AQuitSentBeforeTheThreadExistsIsNotLostAndNothingStarts()
    {
        using var engine = new EngineThread();
        engine.Quit();   // Run has not been called: there is no thread to post to yet
        bool started = false, exited = false;
        var runner = new Thread(() => engine.Run(init: () => started = true, waitHandles: () => [], signaled: _ => { }, onExit: () => exited = true));
        runner.Start();
        Assert.True(runner.Join(10_000), "Run never returned: the quit was lost");
        Assert.False(started);
        Assert.True(exited);   // the owner still gets to let go of whatever it holds
    }

    [Fact]
    public void AQuitSentWhileInitIsRunningEndsTheLoopRightAfter()
    {
        using var engine = new EngineThread();
        bool exited = false;
        var runner = new Thread(() => engine.Run(init: () => engine.Quit(), waitHandles: () => [], signaled: _ => { }, onExit: () => exited = true));
        runner.Start();
        Assert.True(runner.Join(10_000), "Run never returned");
        Assert.True(exited);
    }

    [Fact]
    public void ThousandPostsFromOtherThreadsAllRunInOrder()
    {
        using var run = new Running();
        var order = new List<int>(); var parallel = 0;
        var poster = new Thread(() => { for (int i = 0; i < 1000; i++) { var n = i; run.Engine.Post(() => order.Add(n)); } });
        var others = Enumerable.Range(0, 4).Select(_ => new Thread(() => { for (int i = 0; i < 250; i++) run.Engine.Post(() => parallel++); })).ToList();
        poster.Start(); others.ForEach(t => t.Start());
        poster.Join(); others.ForEach(t => t.Join());
        var done = false; run.Engine.Post(() => done = true);
        Assert.True(WaitFor(() => Volatile.Read(ref done)));
        Assert.Equal(Enumerable.Range(0, 1000).ToArray(), order.ToArray());
        Assert.Equal(1000, parallel);
    }
}
