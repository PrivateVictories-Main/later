using Xunit;

namespace Later.Core.Tests;

public class FamiliarityTests
{
    static Vectors.FamiliarityFile Table() => Vectors.Load<Vectors.FamiliarityFile>("familiarity.json");

    static void NoFailures(List<string> failures) => Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));

    [Fact]
    public void CountsAndConstantsMatchTheTable()
    {
        var f = Table();
        Assert.Equal(f.Counts["civil"], f.Civil.Count);
        Assert.Equal(f.Counts["days"], f.Days.Count);
        Assert.Equal(f.Counts["disposition"], f.Disposition.Count);
        Assert.Equal(Familiarity.RegularDays, f.Constants["regularDays"]);
        Assert.Equal(Familiarity.Window, f.Constants["window"]);
    }

    [Fact]
    public void CivilDaysCountFromTheEpoch() =>
        NoFailures(Table().Civil.Where(c => Familiarity.DayNumber(c.Year, c.Month, c.Day) != c.Number)
            .Select(c => $"{c.Year}-{c.Month}-{c.Day} expected {c.Number}").ToList());

    [Fact]
    public void ADateIsItsLocalCalendarDay() =>
        Assert.Equal(20755, Familiarity.DayNumber(new DateTime(2026, 10, 29, 23, 30, 0, DateTimeKind.Local)));

    [Fact]
    public void DistinctDaysInTheWindowCountWithoutToday() =>
        NoFailures(Table().Days.Where(d => Familiarity.Count(d.Visited, d.Today) != d.Count)
            .Select(d => $"{string.Join(",", d.Visited)} on {d.Today} expected {d.Count}").ToList());

    [Fact]
    public void TheDispositionTable()
    {
        var failures = new List<string>();
        foreach (var c in Table().Disposition)
        {
            var correction = c.Correction is null ? null
                : new Correction(c.Correction.IsSpecific, c.Correction.Kind is null ? null : KindInfo.FromWire(c.Correction.Kind), c.Correction.Regular);
            var verdict = new Verdict(true, Kind.Tools, "a.com/~", c.Place);
            var expected = c.Disposition == "regular" ? PlaceDisposition.Regular : PlaceDisposition.Save;
            var actual = Familiarity.Disposition(verdict, c.Days, correction);
            if (actual != expected) failures.Add($"place={c.Place} days={c.Days} {c.Note}: expected {expected} actual {actual}");
        }
        NoFailures(failures);
    }

    [Theory]
    [InlineData(null, 0, "New site")]
    [InlineData(false, 2, "New site")]
    [InlineData(true, 18, "Regular · visited 18 days this month")]
    [InlineData(true, 0, "Regular")]
    [InlineData(null, 9, "A specific page on a regular site")]
    public void TheInspectorSaysWhy(bool? regular, int days, string line) => Assert.Equal(line, Familiarity.Why(regular, days, null));

    [Fact]
    public void ACorrectionIsTheReason()
    {
        Assert.Equal("Kept because you said so", Familiarity.Why(false, 9, new Correction(IsSpecific: true)));
        Assert.Equal("A regular because you said so", Familiarity.Why(true, 1, new Correction(Regular: true)));
    }
}
