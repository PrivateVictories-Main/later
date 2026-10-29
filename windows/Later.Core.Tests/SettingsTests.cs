using Xunit;

namespace Later.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void DefaultsWhenTheFileIsMissing()
    {
        using var dir = new TempDir();
        Assert.Equal(new Settings(), SettingsStore.Load(dir.At("settings.json")));
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        using var dir = new TempDir();
        var path = dir.At("settings.json");
        foreach (var sort in Enum.GetValues<LibrarySort>())
            foreach (var grouping in Enum.GetValues<LibraryGrouping>())
            {
                var s = new Settings(true, true, true, sort, true, grouping);
                SettingsStore.Save(path, s);
                Assert.Equal(s, SettingsStore.Load(path));
            }
        SettingsStore.Save(path, new Settings());
        Assert.Equal(new Settings(), SettingsStore.Load(path));
    }

    [Fact]
    public void ACorruptFileGivesDefaultsAndDoesNotThrow()
    {
        using var dir = new TempDir();
        var path = dir.At("settings.json");
        foreach (var junk in new[] { "{not json", "", "[1,2]", "\"text\"", "{\"onboarded\":\"yes\",\"sort\":7}" })
        {
            File.WriteAllText(path, junk);
            Assert.Equal(new Settings(), SettingsStore.Load(path));
        }
    }

    [Fact]
    public void AnUnknownSortNameGivesTheDefault()
    {
        using var dir = new TempDir();
        var path = dir.At("settings.json");
        File.WriteAllText(path, "{\"onboarded\":true,\"sort\":\"byMood\",\"grouping\":\"planet\"}");
        Assert.Equal(new Settings(Onboarded: true), SettingsStore.Load(path));
    }

    [Fact]
    public void SaveIsAtomic()
    {
        using var dir = new TempDir();
        var path = dir.At("settings.json");
        SettingsStore.Save(path, new Settings(Onboarded: true));
        Assert.False(File.Exists(path + ".tmp"));
        SettingsStore.Save(path, new Settings(Paused: true));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(new Settings(Paused: true), SettingsStore.Load(path));
    }

    [Fact]
    public void TheEnumOrderIsTheWireOrder()
    {
        Assert.Equal(new List<string> { "TabOrder", "TabOrderReversed", "DateOpened", "DateSaved", "LastSeen", "Title", "Site" }, Enum.GetNames<LibrarySort>().ToList());
        Assert.Equal(new List<string> { "Date", "Site", "Kind" }, Enum.GetNames<LibraryGrouping>().ToList());
    }
}
