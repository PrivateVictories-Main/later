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
