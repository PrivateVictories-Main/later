namespace Later.Windows.Platform;

/// Whether Later starts when the user signs in. `DisabledByUser`: the setting is on, but the user turned it off in Windows' Startup apps,
/// and only they can turn it back on there.
public enum StartState { Off, On, DisabledByUser }

/// The start-at-sign-in setting. The real one (an HKCU Run value) comes with the sign-in work; Settings and the first-run window bind to this.
public interface IStartAtSignIn
{
    StartState State { get; }

    /// Turns it on or off. Throws `IOException`, `UnauthorizedAccessException` or `Win32Exception` when Windows refuses; the message is shown.
    /// Turning it on never overrides the user's own choice in Startup apps: a `DisabledByUser` state stays.
    void Set(bool on);
}

/// A setting that lives in memory only: the stand-in until the real one exists, and what tests and sample runs use.
public sealed class InMemoryStartAtSignIn(StartState initial = StartState.Off) : IStartAtSignIn
{
    public StartState State { get; private set; } = initial;

    /// Tests: the next `Set` throws this.
    public Exception? FailWith { get; set; }

    /// How many `Set` calls took effect.
    public int Writes { get; private set; }

    public void Set(bool on)
    {
        if (FailWith is { } failure) throw failure;
        Writes++;
        State = !on ? StartState.Off : State == StartState.DisabledByUser ? StartState.DisabledByUser : StartState.On;
    }
}
