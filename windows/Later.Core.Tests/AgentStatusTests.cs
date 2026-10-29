using Xunit;

namespace Later.Core.Tests;

public class AgentStatusTests
{
    [Theory]
    [InlineData(AgentState.NeedsRecovery, "needsRecovery")]
    [InlineData(AgentState.CantOpenStorage, "cantOpenStorage")]
    [InlineData(AgentState.CantSave, "cantSave")]
    public void TheFileUsesTheMacsWordsAndRoundTrips(AgentState state, string word)
    {
        using var dir = new TempDir();
        var file = new AgentStatusFile(dir.At("agent-status.json"));
        Assert.Null(file.Read());
        file.Write(new AgentStatus(state, 7));
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(dir.At("agent-status.json"))))
        {
            Assert.Equal(word, doc.RootElement.GetProperty("state").GetString());
            Assert.Equal(7, doc.RootElement.GetProperty("count").GetInt32());
        }
        Assert.Equal(new AgentStatus(state, 7), file.Read());
        Assert.Equal([ "agent-status.json" ], Directory.GetFiles(dir.Path).Select(p => Path.GetFileName(p)).ToArray());   // no temp file left
    }

    [Fact]
    public void TakeReadsAndClears()
    {
        using var dir = new TempDir();
        var file = new AgentStatusFile(dir.At("agent-status.json"));
        file.Write(new AgentStatus(AgentState.CantSave, 3));
        Assert.Equal(new AgentStatus(AgentState.CantSave, 3), file.Take());
        Assert.Null(file.Take());
        file.Clear();   // clearing nothing is fine
    }

    [Fact]
    public void AFileTheMacWroteReadsAndGarbageReadsAsNothing()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.At("a.json"), """{"count":0,"state":"needsRecovery"}""");
        Assert.Equal(new AgentStatus(AgentState.NeedsRecovery, 0), new AgentStatusFile(dir.At("a.json")).Read());
        File.WriteAllText(dir.At("b.json"), """{"count":0,"state":"somethingNew"}""");
        Assert.Null(new AgentStatusFile(dir.At("b.json")).Read());
        File.WriteAllText(dir.At("c.json"), "{garbage");
        Assert.Null(new AgentStatusFile(dir.At("c.json")).Read());
    }

    [Fact]
    public void WritingWhereThereIsNoFolderIsOnlyAMissingHint() =>
        new AgentStatusFile(Path.Combine(Path.GetTempPath(), "later-missing-" + Guid.NewGuid().ToString("N"), "s.json")).Write(new AgentStatus(AgentState.CantSave, 1));

    [Fact]
    public void TheNoticesAreTheMacsWords()
    {
        Assert.Equal("Later wasn't watching your tabs while your list needed repair.", new AgentStatus(AgentState.NeedsRecovery, 0).Notice);
        Assert.Equal("Later couldn't save while it was closed, so some pages it noticed may be missing.", new AgentStatus(AgentState.CantSave, 0).Notice);
    }
}
