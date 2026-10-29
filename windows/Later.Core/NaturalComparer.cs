using System.Globalization;

namespace Later.Core;

/// Finder-style ordering (Swift's `localizedStandardCompare`): runs of digits compare by value, everything else by the
/// current culture, ignoring case and accents. Returns -1, 0 or 1.
public static class NaturalComparer
{
    static bool IsDigit(char c) => Text.IsAsciiDigit(c);

    public static int Compare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (IsDigit(a[i]) && IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && IsDigit(a[i])) i++;
                while (j < b.Length && IsDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0'); var y = b[sj..j].TrimStart('0');
                int byValue = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                if (byValue != 0) return Math.Sign(byValue);
            }
            else
            {
                int si = i, sj = j;
                while (i < a.Length && !IsDigit(a[i])) i++;
                while (j < b.Length && !IsDigit(b[j])) j++;
                int c = CultureInfo.CurrentCulture.CompareInfo.Compare(a[si..i], b[sj..j], CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                if (c != 0) return Math.Sign(c);
            }
        }
        return Math.Sign((a.Length - i).CompareTo(b.Length - j));
    }
}
