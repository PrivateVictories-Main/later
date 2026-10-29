using System.Globalization;

namespace Later.Core;

public enum PlaceDisposition { Save, Regular }

/// How well a person knows a site, from their own visits, and what that means for a place page (a site's home, a dashboard,
/// a feed): on a site they know, it is a regular and stays out of the list; on a new one it is a tool they found.
/// A page that is a thing (a video, a post, an article) is always kept.
public static class Familiarity
{
    /// A site is a regular when it was visited on at least this many different days...
    public const int RegularDays = 4;
    /// ...of the last this many, not counting today.
    public const int Window = 28;

    /// Days count from 1970-01-01 in the user's own time zone, so "a different day" means a different local date.
    public static int DayNumber(DateTime local) => DayNumber(local.Year, local.Month, local.Day);

    /// Days from 1970-01-01 to a civil date (proleptic Gregorian), with no time zone in it.
    public static int DayNumber(int year, int month, int day)
    {
        int y = month <= 2 ? year - 1 : year;
        int era = (y >= 0 ? y : y - 399) / 400;
        int yoe = y - era * 400;
        int doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146_097 + doe - 719_468;
    }

    /// How many different days of the window a site was visited on: the 28 days before `today`, never today.
    public static int Count(IEnumerable<int> days, int today) => days.Distinct().Count(d => d >= today - Window && d < today);

    /// Whether to keep a page in the list or leave it to the regulars. `days` is `Count(...)` for the page's site. A
    /// correction always wins: "This is a regular" (`Regular`) and "Keep in my list" (`IsSpecific`) for the page type.
    public static PlaceDisposition Disposition(Verdict verdict, int days, Correction? correction)
    {
        if (!verdict.IsPlace) return PlaceDisposition.Save;
        if (correction?.Regular is { } regular) return regular ? PlaceDisposition.Regular : PlaceDisposition.Save;
        if (correction?.IsSpecific == true) return PlaceDisposition.Save;
        return days >= RegularDays ? PlaceDisposition.Regular : PlaceDisposition.Save;
    }

    /// The one line the inspector gives for a page, from what is stored for it and its page type's correction.
    public static string Why(bool? regular, int days, Correction? correction)
    {
        if (regular == true)
        {
            if (correction?.Regular == true) return "A regular because you said so";
            return days > 0 ? "Regular · visited " + days.ToString(CultureInfo.InvariantCulture) + " days this month" : "Regular";
        }
        if (correction?.IsSpecific == true || correction?.Regular == false) return "Kept because you said so";
        return days >= RegularDays ? "A specific page on a regular site" : "New site";
    }
}
