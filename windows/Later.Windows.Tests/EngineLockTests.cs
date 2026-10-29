using Later.Windows.Engine;
using Later.Windows.Native;
using Xunit;

namespace Later.Windows.Tests;

public class EngineLockTests
{
    static string Suffix() => Guid.NewGuid().ToString("N")[..12];

    /// Mutexes belong to the thread that took them, so each step of a test runs on a thread of its own.
    static T OnThread<T>(Func<T> work)
    {
        T result = default!; Exception? error = null;
        var thread = new Thread(() => { try { result = work(); } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join();
        if (error is not null) throw new InvalidOperationException("the thread failed", error);
        return result;
    }

    [Fact]
    public void AcquireAndRelease()
    {
        var suffix = Suffix();
        Assert.True(OnThread(() =>
        {
            using var gate = EngineLock.Open(suffix);
            if (!gate.TryAcquire(TimeSpan.Zero)) return false;
            gate.Release();
            return gate.TryAcquire(TimeSpan.Zero);   // free again
        }));
    }

    [Fact]
    public void ASecondThreadCannotAcquireWhileTheFirstHolds()
    {
        var suffix = Suffix();
        using var held = new ManualResetEventSlim(); using var letGo = new ManualResetEventSlim();
        bool firstGot = false;
        var holder = new Thread(() =>
        {
            using var gate = EngineLock.Open(suffix);
            firstGot = gate.TryAcquire(TimeSpan.Zero);
            held.Set(); letGo.Wait();
            gate.Release();
        });
        holder.Start();
        Assert.True(held.Wait(10_000));
        Assert.True(firstGot);
        Assert.False(OnThread(() => { using var other = EngineLock.Open(suffix); return other.TryAcquire(TimeSpan.FromMilliseconds(200)); }));
        letGo.Set(); holder.Join();
        Assert.True(OnThread(() => { using var other = EngineLock.Open(suffix); return other.TryAcquire(TimeSpan.FromSeconds(5)); }));
    }

    [Fact]
    public void ASignalledHandoffIsVisibleToTheWaitAndClears()
    {
        var suffix = Suffix();
        using var agent = EngineLock.Open(suffix); using var app = EngineLock.Open(suffix);
        Assert.Equal(NativeApi.WaitTimeout, NativeApi.WaitForSingleObject(agent.HandoffHandle, 0));
        app.SignalHandoff();
        Assert.Equal(NativeApi.WaitObject0, NativeApi.WaitForSingleObject(agent.HandoffHandle, 0));
        Assert.Equal(NativeApi.WaitObject0, NativeApi.WaitForSingleObject(agent.HandoffHandle, 0));   // manual reset: it stays
        agent.ClearHandoff();
        Assert.Equal(NativeApi.WaitTimeout, NativeApi.WaitForSingleObject(app.HandoffHandle, 0));
    }

    // AcquireForApp: how the app takes the engine from a running agent.

    [Fact]
    public void AcquireForAppTakesAFreeLockAtOnceWithoutAsking()
    {
        var suffix = Suffix();
        Assert.True(OnThread(() =>
        {
            using var app = EngineLock.Open(suffix);
            bool got = app.AcquireForApp(TimeSpan.FromSeconds(5));
            return got && NativeApi.WaitForSingleObject(app.HandoffHandle, 0) == NativeApi.WaitTimeout;
        }));
    }

    [Fact]
    public void AcquireForAppAsksAndGetsTheLockTheMomentTheHolderLetsGo()
    {
        var suffix = Suffix();
        using var held = new ManualResetEventSlim();
        var agent = new Thread(() =>
        {
            using var gate = EngineLock.Open(suffix);
            gate.TryAcquire(TimeSpan.Zero);
            held.Set();
            if (NativeApi.WaitForSingleObject(gate.HandoffHandle, 10_000) == NativeApi.WaitObject0) gate.Release();   // asked: let go
        });
        agent.Start();
        Assert.True(held.Wait(10_000));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(OnThread(() => { using var app = EngineLock.Open(suffix); return app.AcquireForApp(TimeSpan.FromSeconds(5)); }));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"took {timer.ElapsedMilliseconds} ms");
        agent.Join();
    }

    [Fact]
    public void AcquireForAppGivesUpAfterTheTimeoutAndLeavesTheRequestStanding()
    {
        var suffix = Suffix();
        using var held = new ManualResetEventSlim(); using var letGo = new ManualResetEventSlim();
        var holder = new Thread(() => { using var gate = EngineLock.Open(suffix); gate.TryAcquire(TimeSpan.Zero); held.Set(); letGo.Wait(); gate.Release(); });
        holder.Start();
        Assert.True(held.Wait(10_000));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var app = EngineLock.Open(suffix);
        Assert.False(OnThread(() => app.AcquireForApp(TimeSpan.FromMilliseconds(600))));
        Assert.InRange(timer.ElapsedMilliseconds, 500, 3_000);
        Assert.Equal(NativeApi.WaitObject0, NativeApi.WaitForSingleObject(app.HandoffHandle, 0));
        letGo.Set(); holder.Join();
    }

    // The race the one-shot request lost: the agent has just been handed the lock and has not yet cleared the request, so the
    // app's request is erased. The app asks again, and the agent still hears it.
    [Fact]
    public void ARequestErasedJustAfterTheAgentTookTheLockIsMadeAgain()
    {
        var suffix = Suffix();
        using var held = new ManualResetEventSlim();
        bool heardAgain = false;
        var agent = new Thread(() =>
        {
            using var gate = EngineLock.Open(suffix);
            gate.TryAcquire(TimeSpan.Zero);
            held.Set();
            NativeApi.WaitForSingleObject(gate.HandoffHandle, 10_000);   // the app's first request...
            gate.ClearHandoff();                                         // ...is erased, as `Agent.Begin` does on taking the lock
            heardAgain = NativeApi.WaitForSingleObject(gate.HandoffHandle, 5_000) == NativeApi.WaitObject0;
            gate.Release();
        });
        agent.Start();
        Assert.True(held.Wait(10_000));
        bool got = OnThread(() => { using var app = EngineLock.Open(suffix); return app.AcquireForApp(TimeSpan.FromSeconds(5)); });
        agent.Join();
        Assert.True(heardAgain, "the request was not made again");
        Assert.True(got, "the app did not get the lock within 5 s");
    }

    [Fact]
    public void AMutexLeftByAThreadThatEndedIsOursToTake()
    {
        var suffix = Suffix();
        Assert.True(OnThread(() => { using var gate = EngineLock.Open(suffix); return gate.TryAcquire(TimeSpan.Zero); }));   // ends without releasing
        Assert.True(OnThread(() => { using var gate = EngineLock.Open(suffix); return gate.TryAcquire(TimeSpan.FromSeconds(5)); }));
    }

    [Fact]
    public void DifferentSuffixesDoNotShareAnything()
    {
        using var held = new ManualResetEventSlim(); using var letGo = new ManualResetEventSlim();
        var a = Suffix(); var b = Suffix();
        var holder = new Thread(() => { using var gate = EngineLock.Open(a); gate.TryAcquire(TimeSpan.Zero); held.Set(); letGo.Wait(); gate.Release(); });
        holder.Start();
        Assert.True(held.Wait(10_000));
        Assert.True(OnThread(() => { using var other = EngineLock.Open(b); return other.TryAcquire(TimeSpan.Zero); }));
        letGo.Set(); holder.Join();
    }
}
