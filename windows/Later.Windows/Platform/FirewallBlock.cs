namespace Later.Windows.Platform;

public enum FirewallResult { Done, Cancelled, Failed }

/// The optional Windows Firewall rule that blocks Later's outgoing connections. The real one comes with the firewall work; Settings binds
/// to this. Both questions and `Set` may take a moment (and `Set` shows Windows' own permission prompt), so callers keep them off the UI thread.
public interface IFirewallBlock
{
    /// Whether the firewall answers at all.
    bool Available();

    bool IsBlocked();

    /// Adds or removes the rule. The result is what was verified afterwards, never what was hoped for.
    FirewallResult Set(bool block);
}

/// A rule that lives in memory only: the stand-in until the real one exists, and what tests and sample runs use.
public sealed class InMemoryFirewallBlock(bool available = true, bool startsBlocked = false) : IFirewallBlock
{
    bool blocked = startsBlocked;

    /// Tests: what the next `Set` answers instead of changing the rule.
    public FirewallResult? Answer { get; set; }

    public bool Available() => available;

    public bool IsBlocked() => blocked;

    public FirewallResult Set(bool block)
    {
        if (Answer is { } answer) return answer;
        blocked = block;
        return FirewallResult.Done;
    }
}
