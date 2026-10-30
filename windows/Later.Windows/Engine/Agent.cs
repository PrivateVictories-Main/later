using System.ComponentModel;
using System.Runtime.InteropServices;
using Later.Core;
using Later.Windows.Native;
using Later.Windows.Platform;

namespace Later.Windows.Engine;

/// The background process: it watches while the app is not open, and hands the engine to the app and takes it back.
/// The mutex says who watches. The agent starts only after onboarding; it holds the engine, waits for the app's request
/// on the handoff event, and, while the app holds it, waits on the mutex. No polling: both waits sleep inside the message
/// loop. Like the Mac's agent it leaves a damaged list to the app (which tells the user) and says in `agent-status.json`
/// what it could not do; a list that is only locked is asked for again later, by a one-shot timer that runs only then.
public static class Agent
{
    static readonly TimeSpan[] LoadRetries = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)];

    public static int Run()
    {
        Footprint.Mark("agent entered");
        Footprint.Listen();
        var paths = AppPaths.Default;
        using var only = SingleInstance.Claim(paths.InstanceSuffix);   // before anything is read or written
        if (only is null) return 0;                                     // another agent watches
        if (!SettingsStore.Load(paths.Settings).Onboarded) return 0;   // never watch before onboarding
        using var gate = EngineLock.Open(paths.InstanceSuffix);
        using var thread = new EngineThread();
        var threadHost = new ThreadHost(thread);
        var status = new AgentStatusFile(paths.AgentStatus);
        EngineHost? host = null;
        IDisposable? retry = null;
        int attempts = 0;
        bool holding = false, traced = false;
        var loadRetries = LoadRetries;
#if DEBUG
        // Tests shorten the wait for a list that is only locked.
        if (double.TryParse(Environment.GetEnvironmentVariable("LATER_TEST_LOAD_RETRY_SECONDS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            loadRetries = [TimeSpan.FromSeconds(seconds)];
#endif

        void Report(AgentState state, int count = 0) => status.Write(new AgentStatus(state, count));

        void TryAgainLater()
        {
            retry = threadHost.After(loadRetries[Math.Min(attempts++, loadRetries.Length - 1)], () => { PerfLog.Log.Wake("loadretry"); retry = null; if (holding && host is null) Watch(); });
        }

        // We hold the lock: start the engine unless the list says not to.
        void Watch()
        {
            try { Directory.CreateDirectory(paths.DataDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report(AgentState.CantOpenStorage); TryAgainLater(); return; }
            switch (new LibraryStore(paths.Library).Check())
            {
                case LibraryStore.Health.NeedsRecovery: Report(AgentState.NeedsRecovery); return;   // the app recovers it and tells the user
                case LibraryStore.Health.Unreadable: TryAgainLater(); return;
            }
            var started = EngineHost.Start(paths, threadHost, thread.Quit, dropsSecretLinks: false);   // the window app tells the user about it
            if (!started.Engine.CanSave)   // locked between the check and the load: it must not stay unwritable
            {
                started.Stop(flush: false);
                ThreadPool.QueueUserWorkItem(_ => started.Dispose());
                TryAgainLater();
                return;
            }
            started.Engine.SaveFailedChanged += failed =>
            {
                if (failed) Report(AgentState.CantSave, started.Engine.Library.Data.Items.Count); else status.Clear();
            };
            host = started;
            attempts = 0;
            status.Clear();   // whatever was reported before has been dealt with
            Footprint.Mark("engine started");
        }

        void Begin()
        {
            gate.ClearHandoff();
            holding = true;
            Watch();
        }

        // The app asked for the engine.
        void Yield()
        {
            retry?.Dispose(); retry = null;
            var yielded = host; host = null;
            LetGo(
                stopAndSave: yielded is null ? null : () =>
                {
                    yielded.Stop(flush: true);   // a failed final save is reported through SaveFailedChanged
                    if (!yielded.Engine.SaveFailed) status.Clear();
                },
                release: () => { gate.Release(); holding = false; },
                takeApart: yielded is null ? null : yielded.Dispose,
                onPool: work => ThreadPool.QueueUserWorkItem(_ => work()));
        }

        thread.Run(
            init: () => { if (gate.TryAcquire(TimeSpan.Zero)) Begin(); },
            waitHandles: () =>
            {
                if (!traced) { traced = true; Footprint.Mark("first wait"); }
                return holding ? [gate.HandoffHandle] : [gate.MutexHandle];
            },
            signaled: _ =>
            {
                if (holding) Yield();   // the app asked for the engine
                else Begin();           // the app quit or crashed: the mutex is ours, the list is read afresh
            },
            onExit: () =>   // on the engine thread: the mutex belongs to it. The process ends next, so nothing is taken apart.
            {
                retry?.Dispose();
                host?.Stop(flush: true);
                if (holding) gate.Release();
            });
        return 0;
    }

    /// The order of a handoff: save, let go of the lock at once, and only then take the engine apart. Taking it apart waits for a
    /// read that may still be running (a browser that does not answer), and the app is waiting for the lock all that time.
    internal static void LetGo(Action? stopAndSave, Action release, Action? takeApart, Action<Action> onPool)
    {
        stopAndSave?.Invoke();
        release();
        if (takeApart is not null) onPool(takeApart);
    }

    /// A named object that lives as long as the process: a second agent finds it there and leaves. Without it, two agents would
    /// both wait on the mutex, and the older waiter would be given it ahead of an app that asked for it later.
    sealed class SingleInstance : IDisposable
    {
        const int ErrorAlreadyExists = 183;
        readonly nint handle;
        SingleInstance(nint handle) => this.handle = handle;

        public static SingleInstance? Claim(string suffix)
        {
            var handle = NativeApi.CreateMutex(0, 0, $@"Local\Later.Agent.{suffix}");
            if (handle == 0) throw new Win32Exception();
            if (Marshal.GetLastPInvokeError() == ErrorAlreadyExists) { _ = User32.CloseHandle(handle); return null; }
            return new SingleInstance(handle);
        }

        public void Dispose() => _ = User32.CloseHandle(handle);
    }
}
