import Foundation

/// How well a person knows a site, from their own visits, and what that means for a place page (a site's home, a dashboard,
/// a feed): on a site they know, it is a regular and stays out of the list; on a new one it is a tool they found.
/// A page that is a thing (a video, a post, an article) is always kept.
public enum Familiarity {
    /// A site is a regular when it was visited on at least this many different days...
    public static let regularDays = 4
    /// ...of the last this many, not counting today.
    public static let window = 28

    public enum Disposition: Equatable, Sendable { case save, regular }

    /// Calendar days count from 1970-01-01 in the user's own time zone, so "a different day" means a different local date.
    public static func dayNumber(of date: Date, calendar: Calendar = .current) -> Int {
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return dayNumber(year: c.year ?? 1970, month: c.month ?? 1, day: c.day ?? 1)
    }

    /// Days from 1970-01-01 to a civil date (proleptic Gregorian), with no time zone in it.
    public static func dayNumber(year: Int, month: Int, day: Int) -> Int {
        let y = month <= 2 ? year - 1 : year
        let era = (y >= 0 ? y : y - 399) / 400
        let yoe = y - era * 400
        let doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146_097 + doe - 719_468
    }

    /// How many different days of the window a site was visited on: the 28 days before `today`, never today.
    public static func count(_ days: Set<Int>, today: Int) -> Int {
        days.filter { (today - window)..<today ~= $0 }.count
    }

    /// Whether to keep a page in the list or leave it to the regulars. `days` is `count(...)` for the page's site. A
    /// correction always wins: "This is a regular" (`regular`) and "Keep in my list" (`isSpecific`) for the page type.
    public static func disposition(of verdict: Verdict, days: Int, correction: Correction?) -> Disposition {
        guard verdict.isPlace else { return .save }
        if let regular = correction?.regular { return regular ? .regular : .save }
        if correction?.isSpecific == true { return .save }
        return days >= regularDays ? .regular : .save
    }

    /// The one line the inspector gives for a page, from what is stored for it and its page type's correction.
    public static func why(regular: Bool?, days: Int, correction: Correction?) -> String {
        if regular == true {
            if correction?.regular == true { return "A regular because you said so" }
            return days > 0 ? "Regular · visited \(days) days this month" : "Regular"
        }
        if correction?.isSpecific == true || correction?.regular == false { return "Kept because you said so" }
        // A place (it has a flag) that is kept, on a site that has since become a regular, was kept for a reason.
        if regular == false && days >= regularDays { return "Kept in your list" }
        return days >= regularDays ? "A specific page on a regular site" : "New site"
    }
}
