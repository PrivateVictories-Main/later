using System.Collections.Concurrent;
using System.ComponentModel;
using Later.Windows.Native;

namespace Later.Windows.Engine;

/// The engine thread: STA, with a message loop that also waits on kernel handles, so one thread sleeps on posted work,
/// window messages (hooks and the session window) and the handoff objects without a timer or a poll.
public sealed class EngineThread : IDisposable
{
    readonly ConcurrentQueue<Action> queue = new();
    readonly ManualResetEventSlim ready = new();
    Thread? thread;
    volatile uint threadId;

    public int ThreadId => (int)threadId;
    public bool IsEngineThread => thread is not null && Environment.CurrentManagedThreadId == thread.ManagedThreadId;

    /// Starts the engine thread and blocks the caller until `Quit()`. `init` runs first on the engine thread; `waitHandles` is
    /// asked before every wait (the agent swaps between the handoff event and the mutex); `signaled(i)` runs when handle i is
    /// signaled, also when it was an abandoned mutex, which the wait then owns; `onExit` runs on the engine thread after the loop ends.
    public void Run(Action init, Func<nint[]> waitHandles, Action<int> signaled, Action onExit)
    {
        thread = new Thread(() => Loop(init, waitHandles, signaled, onExit)) { Name = "Later.Engine", IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        thread.Join();
    }

    unsafe void Loop(Action init, Func<nint[]> waitHandles, Action<int> signaled, Action onExit)
    {
        _ = NativeApi.PeekMessage(out _, 0, 0, 0, 0);   // creates this thread's message queue
        threadId = NativeApi.GetCurrentThreadId();
        ready.Set();
        init();
        Drain();   // work posted before the queue existed
        while (true)
        {
            var handles = waitHandles();
            // Sleeps until a handle is signaled or a message arrives. Returns WAIT_OBJECT_0 + n, WAIT_ABANDONED_0 + n,
            // or WAIT_OBJECT_0 + count for "a message is waiting".
            uint r;
            fixed (nint* p = handles) r = NativeApi.MsgWaitForMultipleObjectsEx((uint)handles.Length, p, NativeApi.Infinite, NativeApi.QsAllInput, NativeApi.MwmoInputAvailable);
            if (r == NativeApi.WaitFailed) throw new Win32Exception();
            if (r < handles.Length || (r >= NativeApi.WaitAbandoned0 && r < NativeApi.WaitAbandoned0 + handles.Length)) { signaled((int)(r & 0x7F)); continue; }
            if (!Pump()) break;
        }
        onExit();
    }

    bool Pump()
    {
        while (NativeApi.PeekMessage(out var msg, 0, 0, 0, NativeApi.PmRemove) != 0)
        {
            if (msg.Message == NativeApi.WmQuit) return false;
            if (msg.Message == NativeApi.WmApp && msg.Hwnd == 0) { Drain(); continue; }
            _ = NativeApi.TranslateMessage(in msg); _ = NativeApi.DispatchMessage(in msg);
        }
        return true;
    }

    void Drain() { while (queue.TryDequeue(out var a)) a(); }

    /// Runs `a` on the engine thread, in the order posted.
    public void Post(Action a)
    {
        queue.Enqueue(a);
        if (threadId != 0) _ = NativeApi.PostThreadMessage(threadId, NativeApi.WmApp, 0, 0);
    }

    public void Quit()
    {
        if (threadId != 0) _ = NativeApi.PostThreadMessage(threadId, NativeApi.WmQuit, 0, 0);
    }

    public void Dispose() => ready.Dispose();
}
