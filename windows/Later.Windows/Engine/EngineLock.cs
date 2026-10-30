using System.ComponentModel;
using System.Diagnostics;
using Later.Windows.Native;

namespace Later.Windows.Engine;

/// Who watches the browsers: a named mutex says which process owns the engine, a manual-reset event is the app asking
/// the agent for it. Win32 mutexes belong to the thread that took them, so `TryAcquire` and `Release` are always called on
/// the engine thread of the process (a wait for `MutexHandle` inside the message loop takes it on that same thread).
/// The app clears nothing: the agent calls `ClearHandoff` once it owns the engine again.
public sealed class EngineLock : IDisposable
{
    readonly nint mutex, handoff;

    EngineLock(nint mutex, nint handoff) { this.mutex = mutex; this.handoff = handoff; }

    public static EngineLock Open(string suffix)
    {
        var mutex = NativeApi.CreateMutex(0, 0, $@"Local\Later.Engine.{suffix}");
        if (mutex == 0) throw new Win32Exception();
        var handoff = NativeApi.CreateEvent(0, 1, 0, $@"Local\Later.Handoff.{suffix}");
        if (handoff == 0) { var error = new Win32Exception(); _ = User32.CloseHandle(mutex); throw error; }
        return new EngineLock(mutex, handoff);
    }

    public nint MutexHandle => mutex;
    public nint HandoffHandle => handoff;

    /// True when the mutex is owned now, also when its previous owner ended without releasing it.
    public bool TryAcquire(TimeSpan timeout) => TryAcquire(timeout, out _);

    /// `abandoned` tells a clean hand-over from the previous owner ending without releasing (a crash, or a kill).
    public bool TryAcquire(TimeSpan timeout, out bool abandoned)
    {
        uint ms = timeout == Timeout.InfiniteTimeSpan ? NativeApi.Infinite : (uint)Math.Clamp(timeout.TotalMilliseconds, 0, NativeApi.Infinite - 1);
        var result = NativeApi.WaitForSingleObject(mutex, ms);
        abandoned = result == NativeApi.WaitAbandoned0;
        return result switch
        {
            NativeApi.WaitObject0 or NativeApi.WaitAbandoned0 => true,
            NativeApi.WaitTimeout => false,
            _ => throw new Win32Exception(),
        };
    }

    /// The app's way in: takes the engine now, or asks the agent for it and waits, up to `timeout`. The request is made again every
    /// 250 ms: an agent that has just been handed the lock clears the request before it starts watching, and one request that
    /// lands in that moment would be lost. Each step waits on the mutex itself, so the wait ends the moment it is free. `cancelled`
    /// is asked between steps (every 250 ms at most) and ends the wait early with false. Engine thread.
    public bool AcquireForApp(TimeSpan timeout, Func<bool>? cancelled = null)
    {
        if (TryAcquire(TimeSpan.Zero)) return true;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (cancelled?.Invoke() == true) return false;
            SignalHandoff();
            var left = timeout - elapsed.Elapsed;
            if (left <= TimeSpan.Zero) return false;
            if (TryAcquire(left < NudgeEvery ? left : NudgeEvery)) return true;
        }
    }

    static readonly TimeSpan NudgeEvery = TimeSpan.FromMilliseconds(250);

    public void Release()
    {
        if (NativeApi.ReleaseMutex(mutex) == 0) throw new Win32Exception();
    }

    public void SignalHandoff() { if (NativeApi.SetEvent(handoff) == 0) throw new Win32Exception(); }

    public void ClearHandoff() { if (NativeApi.ResetEvent(handoff) == 0) throw new Win32Exception(); }

    public void Dispose()
    {
        _ = User32.CloseHandle(handoff);
        _ = User32.CloseHandle(mutex);
    }
}
