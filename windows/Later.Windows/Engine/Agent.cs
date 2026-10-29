using Later.Core;
using Later.Windows.Platform;

namespace Later.Windows.Engine;

/// The background process: it watches while the app is not open, and hands the engine to the app and takes it back.
/// The mutex says who watches. The agent starts only after onboarding; it holds the engine, waits for the app's request
/// on the handoff event, and, while the app holds it, waits on the mutex. No timers, no polling: both waits sleep inside
/// the message loop.
public static class Agent
{
    public static int Run()
    {
        Footprint.Mark("agent entered");
        var paths = AppPaths.Default;
        if (!SettingsStore.Load(paths.Settings).Onboarded) return 0;   // never watch before onboarding
        using var gate = EngineLock.Open(paths.InstanceSuffix);
        using var thread = new EngineThread();
        EngineHost? host = null;
        bool holding = false, traced = false;
        void Begin()
        {
            gate.ClearHandoff();
            host = EngineHost.Start(paths, new ThreadHost(thread), thread.Quit);
            holding = true;
            Footprint.Mark("engine started");
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
                if (holding)   // the app asked for the engine: save, let go, wait for it to end
                {
                    host!.Stop(flush: true); host.Dispose(); host = null;
                    gate.Release(); holding = false;
                }
                else Begin();   // the app quit or crashed: the mutex is ours, the list is read afresh
            },
            onExit: () =>   // on the engine thread: the mutex belongs to it
            {
                host?.Stop(flush: true); host?.Dispose();
                if (holding) gate.Release();
            });
        return 0;
    }
}
