using System.Runtime.CompilerServices;

namespace Later.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The agent branch runs before any WinUI type is touched, so the headless process never loads WinUI.
        if (args.Contains("--agent", StringComparer.Ordinal)) return RunAgent();
        return RunApp();
    }

    /// Task 4: the agent exists only as an entry point; it exits at once with code 0. Task 6 replaces this body.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunAgent() => 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunApp()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
