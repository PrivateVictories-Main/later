using System.Text.Json;

namespace Later.Core;

public enum AgentState
{
    /// The list is damaged or missing with a backup; the helper leaves it to the window app and watches nothing.
    NeedsRecovery,
    /// The storage folder can't be opened, so nothing could be kept.
    CantOpenStorage,
    /// The last save failed (a full disk, say), so pages it noticed since may be missing.
    CantSave,
}

/// What the background helper tells the window app when it can't do its job: a state and a count, never a link or a title.
/// The same fields as the Mac's `agent-status.json`.
public sealed record AgentStatus(AgentState State, int Count)
{
    static readonly string[] Names = ["needsRecovery", "cantOpenStorage", "cantSave"];

    /// For the window's notice bar.
    public string Notice => State switch
    {
        AgentState.NeedsRecovery => "Later wasn't watching your tabs while your list needed repair.",
        AgentState.CantOpenStorage => "Later couldn't open its storage folder while it was closed, so pages it noticed weren't kept.",
        _ => "Later couldn't save while it was closed, so some pages it noticed may be missing.",
    };

    internal static string Name(AgentState state) => Names[(int)state];
    internal static AgentState? FromName(string? name) => Array.IndexOf(Names, name) is >= 0 and var i ? (AgentState)i : null;
}

/// The helper's status file, beside the list. One writer (the helper), read and cleared by the window app.
public sealed class AgentStatusFile(string path)
{
    public AgentStatus? Read()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (AgentStatus.FromName(root.GetProperty("state").GetString()) is not { } state) return null;
            return new AgentStatus(state, root.GetProperty("count").GetInt32());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    /// Best effort: a status that can't be written is only a missing hint.
    public void Write(AgentStatus status)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("state", AgentStatus.Name(status.State));
                w.WriteNumber("count", status.Count);
                w.WriteEndObject();
            }
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, stream.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Clear()
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Reads the problem and clears it, as the window app does once it has taken over.
    public AgentStatus? Take()
    {
        var status = Read();
        Clear();
        return status;
    }
}
