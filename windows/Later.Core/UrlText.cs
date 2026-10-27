using System.Globalization;
using System.Text;

namespace Later.Core;

/// Percent-encoding the way Foundation does it. Never `System.Uri`, which lowercases, resolves dot segments and decodes.
internal static class UrlText
{
    /// `removingPercentEncoding`: null when a `%` is not followed by two hex digits or the bytes are not valid UTF-8.
    public static string? PercentDecode(string s)
    {
        if (s.IndexOf('%', StringComparison.Ordinal) < 0) return s;
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (c == '%')
            {
                if (i + 2 >= s.Length) return null;
                if (!Text.IsHexDigit(s[i + 1]) || !Text.IsHexDigit(s[i + 2])) return null;
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 3;
            }
            else
            {
                int len = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(s.AsSpan(i, len).ToString()));
                i += len;
            }
        }
        try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return null; }
    }

    static bool IsEscape(string s, int i) => s[i] == '%' && i + 2 < s.Length && Text.IsHexDigit(s[i + 1]) && Text.IsHexDigit(s[i + 2]);

    /// Percent-encodes what the component may not hold, leaving existing `%XX` alone (like `URLComponents(string:)`, which
    /// encodes invalid characters). A component with a stray `%` is not percent-encoded text at all, so Foundation encodes
    /// every `%` in it, even the ones that start a valid escape (characterized on macOS: `%E0%A4%A` becomes `%25E0%25A4%25A`).
    /// `allowed` says which ASCII characters stay as they are.
    public static string EncodeInvalid(string s, Func<char, bool> allowed)
    {
        bool stray = false;
        for (int i = 0; i < s.Length; i++) if (s[i] == '%' && !IsEscape(s, i)) { stray = true; break; }
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (!stray && IsEscape(s, i)) { sb.Append(s, i, 3); i += 3; continue; }
            if (c < 128 && c != '%' && allowed(c)) { sb.Append(c); i++; continue; }
            int len = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            foreach (byte b in Encoding.UTF8.GetBytes(s.AsSpan(i, len).ToString())) sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            i += len;
        }
        return sb.ToString();
    }

    static bool Unreserved(char c) => Text.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
    static bool SubDelim(char c) => c is '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=';
    public static bool PathChar(char c) => Unreserved(c) || SubDelim(c) || c is ':' or '@' or '/';
    public static bool QueryChar(char c) => PathChar(c) || c == '?';
    public static bool UserChar(char c) => Unreserved(c) || SubDelim(c) || c == ':';
}
