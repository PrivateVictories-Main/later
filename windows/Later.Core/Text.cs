using System.Globalization;
using System.Text;

namespace Later.Core;

/// Swift `Character`/`String` behavior that .NET does not give by default.
internal static class Text
{
    /// Grapheme-cluster count, like Swift's String.count.
    public static int Count(string s) => s.Length == 0 ? 0 : new StringInfo(s).LengthInTextElements;

    public static bool IsLetter(Rune r) => Rune.IsLetter(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.LetterNumber;
    public static bool IsNumber(Rune r) => Rune.IsNumber(r);
    public static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
    public static bool IsAsciiLetterOrDigit(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
    public static bool IsHexDigit(char c) => Uri.IsHexDigit(c);

    /// `s.contains(where: \.isLetter)` etc. over scalars.
    public static bool Any(string s, Func<Rune, bool> test) { foreach (var r in s.EnumerateRunes()) if (test(r)) return true; return false; }
    public static bool All(string s, Func<Rune, bool> test) { foreach (var r in s.EnumerateRunes()) if (!test(r)) return false; return true; }

    /// Swift `split(whereSeparator:)`: omits empty pieces.
    public static List<string> SplitWhere(string s, Func<Rune, bool> isSeparator)
    {
        var pieces = new List<string>(); var current = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            if (isSeparator(r)) { if (current.Length > 0) { pieces.Add(current.ToString()); current.Clear(); } }
            else current.Append(r.ToString());
        }
        if (current.Length > 0) pieces.Add(current.ToString());
        return pieces;
    }

    /// Swift `split(separator: "/")`: omits empty pieces.
    public static List<string> SplitOmitEmpty(string s, char separator) =>
        s.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// Stable sort (List.Sort is not): Swift's sorted keeps equal items in order and the list orders rely on it.
    /// `compare` is three-way (negative, zero, positive); ties fall back to the original position.
    public static List<T> StableSort<T>(IEnumerable<T> source, Comparison<T> compare)
    {
        var indexed = source.Select((item, index) => (item, index)).ToList();
        indexed.Sort((a, b) => { int c = compare(a.item, b.item); return c != 0 ? c : a.index.CompareTo(b.index); });
        return indexed.Select(x => x.item).ToList();
    }

    /// Test helper: ICU puts U+202F (narrow no-break space) before AM/PM.
    public static string NormalizeSpaces(string s) => s.Replace('\u202f', ' ').Replace('\u00a0', ' ');
}
