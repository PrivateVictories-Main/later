#if DEBUG
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace Later.App;

/// Debug only: what a sample run does with a window of its own. Dark when asked, and, with `--shot` or `--shot-screen`, a picture of the
/// window once it has shown and settled, after which the app quits. The window that is the sample's subject calls `Attach`.
internal sealed class SampleShot
{
    readonly Window window;
    bool activatedOnce, loadedOnce, started;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? timer;

    SampleShot(Window window) => this.window = window;

    public static void Attach(Window window, FrameworkElement root)
    {
        if (!Sample.Enabled) return;
        if (Sample.Dark)
        {
            root.RequestedTheme = ElementTheme.Dark;
            window.AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
            window.AppWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Gray;
        }
        if (Sample.ShotPath is null && Sample.ShotScreenPath is null) return;
        var shot = new SampleShot(window);
        window.Activated += (_, _) => { Mark("activated"); shot.activatedOnce = true; shot.Start(); };
        root.Loaded += (_, _) => { Mark("loaded"); shot.loadedOnce = true; shot.Start(); };
        Mark("wired");
    }

    /// A line for the scene's progress, in a file beside its picture: what the screenshot script prints when a scene does not finish.
    static void Mark(string what)
    {
        if ((Sample.ShotPath ?? Sample.ShotScreenPath) is not { } path) return;
        try { File.AppendAllText(path + ".log", what + "\n"); }
        catch (IOException) { }
    }

    /// Once the window is active and its content loaded: wait for the first frame to settle, take the picture, and quit.
    void Start()
    {
        if (!activatedOnce || !loadedOnce || started) return;
        started = true;
        Mark("timer started");
        var t = timer = window.DispatcherQueue.CreateTimer();   // in a field: a timer nothing refers to can be collected before it ticks
        t.Interval = TimeSpan.FromMilliseconds(Sample.ShotDelayMs);
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            Mark("tick");
            try { ScreenCapture.Save(WinRT.Interop.WindowNative.GetWindowHandle(window), Sample.ShotPath, Sample.ShotScreenPath); Mark("saved"); }
            catch (Exception e) { Mark("failed " + e.GetType().Name); throw; }
            finally { Application.Current.Exit(); }
        };
        t.Start();
    }
}
#endif
