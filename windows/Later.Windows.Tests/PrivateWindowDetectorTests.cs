using System.Text;
using Later.Windows.Reader;
using Xunit;

namespace Later.Windows.Tests;

public class PrivateWindowDetectorTests
{
    static readonly string[] Profiles = ["Your Chrome", "Person 1"];
    static bool Normal(string windowText, string rootView, params string[] profiles) =>
        PrivateWindowDetector.IsNormal(windowText, rootView, () => profiles);

    [Theory]
    [InlineData("Fixture 1 - Google Chrome", "Fixture 1 - Google Chrome")]
    [InlineData("Fixture 1 – Helium", "Fixture 1 – Helium")]
    [InlineData("What is Incognito mode? - Google Chrome", "What is Incognito mode? - Google Chrome")]   // a page title that holds the word
    [InlineData("Seite - Google Chrome", "‎Seite - Google Chrome")]                                 // a direction mark on one side
    public void EqualTitlesAreANormalWindow(string windowText, string rootView) => Assert.True(Normal(windowText, rootView));

    [Theory]
    [InlineData("Fixture 9 - Google Chrome (Incognito)")]
    [InlineData("Fixture 9 - Google Chrome (anonym)")]
    [InlineData("Fixture 9 - Google Chrome (incógnito)")]
    [InlineData("Fixture 9 - Google Chrome（シークレット モード）")]
    [InlineData("Fixture 9 - Google Chrome (Navigation privée)")]
    [InlineData("Fixture 9 - Google Chrome (Guest)")]
    [InlineData("Fixture 9 - Google Chrome - Inkognito")]    // even a suffix in the profile style is no profile unless it is one
    [InlineData("Fixture 8 - Google Chrome")]                // a different title: the window moved while it was read
    [InlineData("")]
    public void AnySuffixOrDifferenceIsNotNormal(string rootView) => Assert.False(Normal("Fixture 9 - Google Chrome", rootView, Profiles));

    [Fact]
    public void ASecondProfilesWindowIsNormalOnlyWithARealProfileName()
    {
        Assert.True(Normal("Fixture 2 - Google Chrome", "Fixture 2 - Google Chrome - Person 1", Profiles));
        Assert.True(Normal("Fixture 2 - Google Chrome", "Fixture 2 - Google Chrome - Your Chrome", Profiles));
        Assert.False(Normal("Fixture 2 - Google Chrome", "Fixture 2 - Google Chrome - Person 1"));                 // no profile names known
        Assert.False(Normal("Fixture 2 - Google Chrome", "Fixture 2 - Google Chrome - Someone Else", Profiles));
        Assert.False(Normal("Fixture 2 - Google Chrome", "Fixture 2 - Google Chrome (Person 1)", Profiles));       // the private style never counts
    }

    [Fact]
    public void AnEmptyWindowTextIsNeverNormal() => Assert.False(Normal("", ""));

    [Fact]
    public void ProfileNamesComeFromLocalState()
    {
        const string json = """{"profile":{"info_cache":{"Default":{"name":"Your Chrome","gaia_name":"","shortcut_name":"Ann"},"Profile 2":{"name":"Person 1","gaia_given_name":"Bo","avatar_icon":"x"}}},"other":1}""";
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Equal(["Ann", "Bo", "Person 1", "Your Chrome"], ProfileNames.Parse(s).Order().ToArray());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"profile":3}""")]
    [InlineData("""{"profile":{"info_cache":[1,2]}}""")]
    [InlineData("""{"profile":{"info_cache":{"Default":5}}}""")]
    public void OddLocalStateGivesNoNames(string json)
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Empty(ProfileNames.Parse(s));
    }

    [Fact]
    public void LocalStateIsReadFromTheDataFolderAndReReadWhenItChanges()
    {
        using var dir = new TempDir();
        var names = new ProfileNames(dir.Path);
        Assert.Empty(names.Current());
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "Local State"), """{"profile":{"info_cache":{"Default":{"name":"One"}}}}""");
        Assert.Equal(["One"], names.Current().ToArray());
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "Local State"), """{"profile":{"info_cache":{"Default":{"name":"Other"}}}}""");
        Assert.Equal(["Other"], names.Current().ToArray());
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "Local State"), "not json");
        Assert.Empty(names.Current());
    }
}
