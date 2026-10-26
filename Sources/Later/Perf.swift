import Foundation
import os

/// Timing logs. Numbers and browser names only: never URLs or titles.
enum Perf {
    static let log = Logger(subsystem: "com.ryan.later", category: "perf")

    static func ms(since start: ContinuousClock.Instant) -> Double {
        let elapsed = ContinuousClock.now - start
        return Double(elapsed.components.seconds) * 1000 + Double(elapsed.components.attoseconds) / 1e15
    }
}

#if DEBUG
extension Perf {
    /// Development only: for half a second (or `seconds`) after a change, logs each run-loop turn's main-thread work
    /// (milliseconds since the change, then the turn's CPU milliseconds, millions of instructions and the clock it ran
    /// at, which tells a performance core, at 4 GHz or so, from an efficiency core), so work SwiftUI puts off to later
    /// turns and every frame of an animation shows too; or, with `summary`, just how the turns spread.
    /// Instructions are the steady measure: the same for the same work on any core, however busy the Mac is.
    @MainActor static func frame(_ label: String, for seconds: Double = 0.5, summary: Bool = false) {
        let start = ContinuousClock.now
        var turnCPU = clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID), turnCounts = counters()
        var turns: [(at: Double, cpu: Double, millions: Double, gigahertz: Double)] = []
        var observer: CFRunLoopObserver?
        let activities = CFRunLoopActivity.beforeWaiting.rawValue | CFRunLoopActivity.afterWaiting.rawValue
        observer = CFRunLoopObserverCreateWithHandler(nil, activities, true, CFIndex.max) { _, activity in
            if activity == .afterWaiting {
                turnCPU = clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID)
                turnCounts = counters()
                return
            }
            let now = counters(), cpu = Double(clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID) - turnCPU) / 1e6
            let millions = Double(now.instructions - turnCounts.instructions) / 1e6
            if millions >= 1 || turns.isEmpty {
                turns.append((ms(since: start), cpu, millions, cpu > 0 ? Double(now.cycles - turnCounts.cycles) / cpu / 1e6 : 0))
            }
            guard ms(since: start) > seconds * 1000 else { return }
            CFRunLoopRemoveObserver(CFRunLoopGetMain(), observer, .commonModes)
            if summary {
                let sorted = turns.map(\.millions).sorted()
                let line = String(format: "turns=%d avg=%.0fM p95=%.0fM max=%.0fM over66M=%d over133M=%d cpuMax=%.1fms", turns.count,
                                  sorted.reduce(0, +) / Double(max(sorted.count, 1)), sorted[Int(Double(sorted.count - 1) * 0.95)],
                                  sorted.last ?? 0, sorted.count(where: { $0 > 66 }), sorted.count(where: { $0 > 133 }),
                                  turns.map(\.cpu).max() ?? 0)
                log.info("frame \(label, privacy: .public) \(line, privacy: .public)")
            } else {
                let line = turns.map { String(format: "%.0f:%.1fms/%.0fM@%.1f", $0.at, $0.cpu, $0.millions, $0.gigahertz) }
                    .joined(separator: " ")
                log.info("frame \(label, privacy: .public) \(line, privacy: .public)")
            }
        }
        CFRunLoopAddObserver(CFRunLoopGetMain(), observer, .commonModes)
    }

    /// Page faults and page-ins (pages read back from disk) the process has taken so far.
    static func faults() -> (faults: Int32, pageins: Int32) {
        var info = task_events_info()
        var count = mach_msg_type_number_t(MemoryLayout<task_events_info>.size / MemoryLayout<natural_t>.size)
        let ok = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { task_info(mach_task_self_, task_flavor_t(TASK_EVENTS_INFO), $0, &count) }
        }
        return ok == KERN_SUCCESS ? (info.faults, info.pageins) : (0, 0)
    }

    /// Instructions and cycles the calling thread (the main thread, in `frame`) has run. Not the whole process's: other
    /// threads' work would be counted against the frame too.
    static func counters() -> (instructions: UInt64, cycles: UInt64) {
        var counts: (instructions: UInt64, cycles: UInt64) = (0, 0)
        let ok = withUnsafeMutablePointer(to: &counts) { threadSelfCounts(1, $0, MemoryLayout<(UInt64, UInt64)>.size) }
        return ok == 0 ? counts : (0, 0)
    }
}

/// libsystem_kernel's own per-thread counters (type 1: instructions, then cycles); it has no header.
@_silgen_name("thread_selfcounts")
private func threadSelfCounts(_ type: Int32, _ counts: UnsafeMutableRawPointer, _ size: Int) -> Int32
#endif
