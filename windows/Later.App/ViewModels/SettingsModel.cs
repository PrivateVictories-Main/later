using System.ComponentModel;
using Later.Core;
using Later.Windows.Engine;
using Later.Windows.Platform;

namespace Later.App.ViewModels;

/// What the window asks of the engine's side for the settings that change what is watched or shown. The implementation keeps the
/// setting in the running engine, or, with none, in the settings file for the next engine to read.
internal interface IWatchControls
{
    void SetPaused(bool value);
    void SetSavesFeedHomes(bool value);
    void SetView(LibraryGrouping grouping, LibrarySort sort, bool reversed);
}

/// How a dot reads: good only while Later is watching; waiting for a browser is quiet, anything to act on a warning.
public enum Tone { Good, Warning, Quiet }

/// The line under Later's name at the top of Settings: a dot and what Later is doing.
public sealed record StatusSummary(string Text, Tone Tone);

/// One browser: its name, how it is doing, and (when installed) the executable its icon comes from.
public sealed record BrowserLine(Browser Browser, string Name, string Status, Tone Tone, string? ExePath);

/// Settings, for its window: the port of the Mac's `SettingsView` logic. UI thread only. Settings are kept by the engine's side
/// (`IWatchControls`); a sample run has none, and then nothing is written anywhere. `Changed` says the window should look again.
internal sealed class SettingsModel : IDisposable
{
    public const string StartupAppsUri = "ms-settings:startupapps";

    /// The orders in the enum's order, by the Mac's titles.
    public static readonly IReadOnlyList<string> SortTitles = Enum.GetValues<LibrarySort>().Select(s => s.Title()).ToArray();

    readonly LibraryViewModel library;
    readonly IWatchControls? engine;
    readonly IStartAtSignIn start;
    readonly IFirewallBlock firewall;
    readonly Dictionary<Browser, string?> exe;
    bool paused, savesFeedHomes, firewallAvailable = true, firewallBlocked, firewallBusy;
    bool? seenEnginePaused;
    string? startFailure, firewallNote;

    public event Action? Changed;

    /// `settingsPath`: where the settings are read from (null in a sample run, which has none). `exePath`: the browser's executable, or null.
    public SettingsModel(LibraryViewModel library, IWatchControls? engine, IStartAtSignIn start, IFirewallBlock firewall,
                         Func<Browser, string?> exePath, string? settingsPath, string version)
    {
        this.library = library; this.engine = engine; this.start = start; this.firewall = firewall;
        Version = version;
        exe = BrowserInfo.All.ToDictionary(b => b, exePath);
        var saved = settingsPath is null ? new Settings() : SettingsStore.Load(settingsPath);
        bool known = library.Engine.Statuses.Count > 0;
        paused = known ? library.Engine.Paused : saved.Paused;
        seenEnginePaused = known ? library.Engine.Paused : null;
        savesFeedHomes = saved.SavesFeedHomes;
        library.PropertyChanged += OnLibraryChanged;
    }

    public void Dispose() => library.PropertyChanged -= OnLibraryChanged;

    public string Version { get; }

    // MARK: Header and browsers

    public bool Onboarded => library.Engine.Onboarded;

    public StatusSummary Summary
    {
        get
        {
            var watching = BrowserInfo.All.Where(b => StatusOf(b) == BrowserStatus.Watching).ToList();
            bool problem = BrowserInfo.All.Any(b => BrowserStatusText.IsProblem(StatusOf(b)));
            var tone = !Onboarded || paused || problem ? Tone.Warning : watching.Count == 0 ? Tone.Quiet : Tone.Good;
            var text = !Onboarded ? "Starts watching after setup"
                : paused ? "Paused"
                : problem ? "Needs your attention below"
                : watching.Count == 0 ? "Waiting for a browser to open"
                : "Watching " + BrowserStatusText.Names(watching);
            return new StatusSummary(text, tone);
        }
    }

    public IReadOnlyList<BrowserLine> Browsers => BrowserInfo.All.Select(b => Line(b)).ToList();

    BrowserLine Line(Browser b)
    {
        var (status, tone) = StatusOf(b) switch
        {
            BrowserStatus.Watching => ("Watching", Tone.Good),
            BrowserStatus.ReadFailed => ("Can't read tabs", Tone.Warning),
            BrowserStatus.NotRunning => ("Not open right now", Tone.Quiet),
            _ => ("Not installed", Tone.Quiet),
        };
        return new BrowserLine(b, b.DisplayName(), status, tone, exe[b]);
    }

    /// The engine's word on the browser; with no engine to ask (a list shown read-only), whether it is installed.
    BrowserStatus StatusOf(Browser b) =>
        library.Engine.Statuses.TryGetValue(b, out var s) ? s : exe[b] is null ? BrowserStatus.NotInstalled : BrowserStatus.NotRunning;

    // MARK: General

    public StartState StartState => start.State;

    /// On only when Windows will really start Later: a setting the user turned off in Startup apps reads as off, with a note.
    public bool StartIsOn => start.State == StartState.On;

    /// The line under the start setting: why it is off, or why Windows did not take the change.
    public string? StartNote => startFailure is { } reason
        ? (reason.Length == 0 ? "Windows didn't accept the change." : $"Windows didn't accept the change: {reason}.") + " Try again, or use Startup apps in Settings."
        : start.State == StartState.DisabledByUser ? "Turned off in Startup apps." : null;

    public void SetStart(bool on)
    {
        try
        {
            start.Set(on);
            startFailure = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException)
        {
            startFailure = e.Message.Trim().TrimEnd('.', ' ');
        }
        Changed?.Invoke();
    }

    public bool Paused => paused;

    public void SetPaused(bool value)
    {
        paused = value;
        engine?.SetPaused(value);
        Changed?.Invoke();
    }

    public bool SavesFeedHomes => savesFeedHomes;

    public void SetSavesFeedHomes(bool value)
    {
        savesFeedHomes = value;
        engine?.SetSavesFeedHomes(value);
        Changed?.Invoke();
    }

    public LibrarySort Sort => library.Sort;

    /// The window's own order and the toolbar's are one: a change of either shows in both.
    public void SetSort(LibrarySort sort)
    {
        library.Sort = sort;
        engine?.SetView(library.Grouping, sort, library.SortReversed);
    }

    // MARK: Privacy

    public bool FirewallAvailable => firewallAvailable;
    public bool FirewallBlocked => firewallBlocked;

    /// True while Windows' own permission prompt may be up.
    public bool FirewallBusy => firewallBusy;

    public string? FirewallNote => !firewallAvailable ? "Windows Firewall isn't available on this PC." : firewallNote;

    /// What the firewall says now, off the UI thread (it asks the system).
    public async Task LoadFirewallAsync()
    {
        var (available, blocked) = await Task.Run(() =>
        {
            try { bool a = firewall.Available(); return (a, a && firewall.IsBlocked()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { return (false, false); }
        });
        firewallAvailable = available;
        firewallBlocked = blocked;
        Changed?.Invoke();
    }

    /// Asks Windows for the change and then looks at what it did: the switch shows the rule, never the hope.
    public async Task SetFirewallAsync(bool block)
    {
        if (firewallBusy) return;
        firewallBusy = true;
        firewallNote = null;
        Changed?.Invoke();
        FirewallResult result;
        try { result = await Task.Run(() => firewall.Set(block)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { result = FirewallResult.Failed; }
        firewallBlocked = await Task.Run(() => firewall.IsBlocked());
        firewallBusy = false;
        firewallNote = result switch
        {
            FirewallResult.Cancelled => "Windows asked for your OK and it was declined.",
            FirewallResult.Failed => "Windows didn't change the firewall. Try again.",
            _ => null,
        };
        Changed?.Invoke();
    }

    /// The window came forward (say, back from Startup apps): what Windows says about starting is read again.
    public void Refresh() => Changed?.Invoke();

    void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.Engine))
        {
            // Paused can also change from the sidebar's Resume: follow the engine when it says something new, not when it repeats itself.
            var state = library.Engine;
            if (state.Statuses.Count > 0 && state.Paused != seenEnginePaused) { seenEnginePaused = state.Paused; paused = state.Paused; }
            Changed?.Invoke();
        }
        else if (e.PropertyName == nameof(LibraryViewModel.Sort)) Changed?.Invoke();
    }
}
