using System.Diagnostics;
using Later.Windows.Engine;
using Later.Windows.Native;
using Xunit;

namespace Later.Windows.Tests;

internal static partial class SendTo
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint Message(nint hwnd, uint message, nuint wParam, nint lParam);
}

public sealed class SessionNotifierTests : IDisposable
{
    const uint WmEndSession = 0x0016, WmPowerBroadcast = 0x0218, WmSessionChange = 0x02B1, WmNull = 0;

    readonly EngineThread engine = new();
    readonly Thread runner;
    SessionNotifier notifier = null!;
    readonly List<string> events = [];

    public SessionNotifierTests()
    {
        using var started = new ManualResetEventSlim();
        runner = new Thread(() => engine.Run(
            init: () =>
            {
                notifier = new SessionNotifier();
                notifier.Locked += () => Record("locked"); notifier.Unlocked += () => Record("unlocked");
                notifier.Suspending += () => Record("suspending"); notifier.Resumed += () => Record("resumed");
                notifier.SessionEnding += () => Record("ending");
                notifier.DisplayChanged += on => Record(on ? "display on" : "display off");
                started.Set();
            },
            waitHandles: () => [],
            signaled: _ => { },
            onExit: () => notifier.Dispose()));
        runner.Start();
        Assert.True(started.Wait(10_000));
    }

    void Record(string name) { lock (events) events.Add(name); }

    string[] Seen() { lock (events) return events.ToArray(); }

    void Send(uint message, nuint wParam) => SendTo.Message(notifier.Hwnd, message, wParam, 0);

    [Fact]
    public void WindowsAcceptedTheSessionRegistration() => Assert.True(notifier.RegisteredForSessionChanges);

    static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    /// A WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE the way the system sends it: a POWERBROADCAST_SETTING behind lParam.
    void SendPowerSetting(Guid setting, uint dataLength, int data)
    {
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<PowerBroadcastSetting>());
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(new PowerBroadcastSetting { PowerSetting = setting, DataLength = dataLength, Data = data }, memory, false);
            SendTo.Message(notifier.Hwnd, WmPowerBroadcast, 0x8013, memory);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void WindowsAcceptedTheDisplayStateRegistration() => Assert.True(notifier.RegisteredForDisplayChanges);

    [Fact]
    public void TheDisplayGoingOffComingOnOrDimmingIsReported()
    {
        SendPowerSetting(ConsoleDisplayState, 4, 0);
        SendPowerSetting(ConsoleDisplayState, 4, 1);
        SendPowerSetting(ConsoleDisplayState, 4, 2);   // dimmed after on: still on, nothing new
        SendPowerSetting(ConsoleDisplayState, 4, 0);
        SendPowerSetting(ConsoleDisplayState, 4, 2);   // dimmed is on
        Assert.Equal(["display off", "display on", "display off", "display on"], Seen());
    }

    [Fact]
    public void TheStateWindowsReportsAtRegistrationIsNotAChange()
    {
        SendPowerSetting(ConsoleDisplayState, 4, 1);   // already on
        Assert.Empty(Seen());
    }

    [Fact]
    public void OtherPowerSettingsAndATruncatedOneRaiseNothing()
    {
        SendPowerSetting(new Guid("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548"), 4, 0);   // GUID_ACDC_POWER_SOURCE
        SendPowerSetting(ConsoleDisplayState, 0, 0);
        SendTo.Message(notifier.Hwnd, WmPowerBroadcast, 0x8013, 0);
        Assert.Empty(Seen());
    }

    [Fact]
    public void EachSystemMessageRaisesItsEventOnce()
    {
        Send(WmSessionChange, 7);
        Assert.Equal(["locked"], Seen());
        Send(WmSessionChange, 8);
        Send(WmPowerBroadcast, 4);
        Send(WmPowerBroadcast, 0x12);
        Send(WmPowerBroadcast, 7);
        Send(WmEndSession, 1);
        Assert.Equal(["locked", "unlocked", "suspending", "resumed", "resumed", "ending"], Seen());
    }

    [Fact]
    public void OtherMessagesAndAnEndSessionThatWasCancelledRaiseNothing()
    {
        Send(WmEndSession, 0);
        Send(WmSessionChange, 1);     // WTS_CONSOLE_CONNECT
        Send(WmPowerBroadcast, 0x0A); // PBT_APMPOWERSTATUSCHANGE
        Send(WmNull, 0);
        Assert.Empty(Seen());
    }

    [Fact]
    public void TheWindowIsATopLevelHiddenWindowOfThisProcess()
    {
        Assert.Equal("Later.Hidden", User32.Class(notifier.Hwnd));
        Assert.Equal(0, User32.IsWindowVisible(notifier.Hwnd));
        Assert.Equal(0, User32.GetWindow(notifier.Hwnd, User32.GwOwner));
        _ = User32.GetWindowThreadProcessId(notifier.Hwnd, out var pid);
        Assert.Equal((uint)Environment.ProcessId, pid);
    }

    public void Dispose()
    {
        engine.Quit(); runner.Join(10_000);
        engine.Dispose();
    }
}
