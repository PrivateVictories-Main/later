using System.Text.Json;

namespace Later.Core.Tests;

public static class Vectors
{
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static T Load<T>(string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", name)), Options)
        ?? throw new InvalidOperationException($"Empty vector file {name}");

    public sealed record CleanCase(string Raw, string Expected, string? Note = null);
    public sealed record StorefrontCase(string Host, string Name, bool Is);
    public sealed record LinkCleanFile(Dictionary<string, int> Counts, List<CleanCase> Clean, List<string> Refuse,
                                       List<string[]> Distinct, List<StorefrontCase> Storefront);
    public static LinkCleanFile LoadLinkClean() => Load<LinkCleanFile>("link-clean.json");

    public sealed record KeyCase(string Raw, string Key);
    public sealed record PageKeys(Dictionary<string, int> Counts, List<string[]> Same, List<string[]> Different, List<KeyCase> Keys);
    public sealed record CorrectionVector(bool? IsSpecific, string? Kind);
    public sealed record JudgeCase(string Url, string Title, bool? Specific, string? Kind, string? PageType, string? PageTypeSuffix,
                                   string? NotPageTypeSuffix, bool? Feeds, Dictionary<string, CorrectionVector>? Corrections, string? Note, bool? Place = null);
    public sealed record JudgeFile(Dictionary<string, int> Counts, List<JudgeCase> Cases);

    public sealed record CivilCase(int Year, int Month, int Day, int Number);
    public sealed record DaysCase(int Today, List<int> Visited, int Count, string? Note);
    public sealed record DispositionFix(bool? IsSpecific, bool? Regular, string? Kind);
    public sealed record DispositionCase(bool Place, int Days, DispositionFix? Correction, string Disposition, string? Note);
    public sealed record FamiliarityFile(Dictionary<string, int> Counts, Dictionary<string, int> Constants, List<CivilCase> Civil,
                                         List<DaysCase> Days, List<DispositionCase> Disposition);
}
