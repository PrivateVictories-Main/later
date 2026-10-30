using System.Runtime.CompilerServices;

namespace Later.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // These branches run before any WinUI type is touched, so a headless process never loads WinUI.
        if (args.Contains("--self-test", StringComparer.Ordinal)) return RunSelfTest();
        if (args.Contains("--agent", StringComparer.Ordinal)) return RunAgent();
#if DEBUG
        if (args.Contains("--test-hold-lock", StringComparer.Ordinal)) return HoldLock(crash: true);
        if (args.Contains("--test-hold-lock-clean", StringComparer.Ordinal)) return HoldLock(crash: false);
        if (Sample.NotBuilt) return 3;   // a scene whose window a later task builds (settings, onboarding, a selection)
#endif
        return RunApp();
    }

    /// Core and the agent's types, run from the published folder: `self-test ok` and 0, or the exception type and 1.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunSelfTest() => Later.Windows.Engine.SelfTest.Run(Console.Out);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunAgent() => Later.Windows.Engine.Agent.Run();

#if DEBUG
    /// Test hook: acts as the app. Asks the agent for the engine, takes the lock, says so in a marker file, then dies without
    /// releasing it (a crash) or lets go after three seconds (a clean exit).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int HoldLock(bool crash)
    {
        var paths = Later.Windows.Platform.AppPaths.Default;
        using var gate = Later.Windows.Engine.EngineLock.Open(paths.InstanceSuffix);
        if (!gate.AcquireForApp(TimeSpan.FromSeconds(10))) return 2;
        File.WriteAllText(Path.Combine(paths.DataDir, "held.txt"), "held");
        Console.Out.WriteLine("held");
        if (crash) Environment.FailFast("test crash");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        gate.Release();
        return 0;
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunApp()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
