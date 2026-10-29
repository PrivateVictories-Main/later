import Foundation
import Testing
@testable import LaterCore

struct CivilVector: Decodable, Sendable, CustomTestStringConvertible {
    let year: Int, month: Int, day: Int, number: Int
    var testDescription: String { "\(year)-\(month)-\(day)" }
}

struct DaysVector: Decodable, Sendable, CustomTestStringConvertible {
    let today: Int
    let visited: [Int]
    let count: Int
    let note: String?
    var testDescription: String { "\(visited) on \(today)" }
}

struct DispositionVector: Decodable, Sendable, CustomTestStringConvertible {
    struct Fix: Decodable, Sendable { let isSpecific: Bool?, regular: Bool?, kind: String? }
    let place: Bool
    let days: Int
    let correction: Fix?
    let disposition: String
    let note: String?
    var testDescription: String { "place=\(place) days=\(days) \(correction.map { "\($0)" } ?? "") -> \(disposition)" }
}

struct FamiliarityVectors: Decodable, Sendable {
    let counts: [String: Int]
    let constants: [String: Int]
    let civil: [CivilVector]
    let days: [DaysVector]
    let disposition: [DispositionVector]
    static let shared: FamiliarityVectors = Vectors.load("familiarity.json")
}

@Suite struct FamiliarityTests {
    @Test func vectorCountsAndConstantsMatch() {
        let v = FamiliarityVectors.shared
        #expect(v.counts == ["civil": v.civil.count, "days": v.days.count, "disposition": v.disposition.count])
        #expect(v.constants == ["regularDays": Familiarity.regularDays, "window": Familiarity.window])
    }

    @Test(arguments: FamiliarityVectors.shared.civil)
    func civilDaysCountFromTheEpoch(_ v: CivilVector) {
        #expect(Familiarity.dayNumber(year: v.year, month: v.month, day: v.day) == v.number)
    }

    @Test func aDateIsItsLocalCalendarDay() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try #require(TimeZone(identifier: "Pacific/Auckland"))
        // 2026-10-29 23:30 in Auckland is still the 29th there though it is already the 29th 10:30 UTC.
        let late = try #require(calendar.date(from: DateComponents(year: 2026, month: 10, day: 29, hour: 23, minute: 30)))
        let early = try #require(calendar.date(from: DateComponents(year: 2026, month: 10, day: 30, hour: 0, minute: 10)))
        #expect(Familiarity.dayNumber(of: late, calendar: calendar) == 20755)
        #expect(Familiarity.dayNumber(of: early, calendar: calendar) == 20756)
    }

    @Test(arguments: FamiliarityVectors.shared.days)
    func countsDistinctDaysInTheWindowWithoutToday(_ v: DaysVector) {
        #expect(Familiarity.count(Set(v.visited), today: v.today) == v.count)
    }

    @Test(arguments: FamiliarityVectors.shared.disposition)
    func dispositionTable(_ v: DispositionVector) throws {
        let correction = try v.correction.map {
            Correction(isSpecific: $0.isSpecific, kind: try $0.kind.map { try #require(Kind(rawValue: $0)) }, regular: $0.regular)
        }
        let verdict = Verdict(isSpecific: true, kind: .tools, pageType: "a.com/~", isPlace: v.place)
        let expected: Familiarity.Disposition = v.disposition == "regular" ? .regular : .save
        #expect(Familiarity.disposition(of: verdict, days: v.days, correction: correction) == expected)
    }
}
