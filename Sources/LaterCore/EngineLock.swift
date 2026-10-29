import Foundation

/// The one exclusive claim on watching and writing the library: whoever holds it runs the engine. An advisory `flock`
/// on a file, so the kernel frees it the moment the holder quits or is killed.
public final class EngineLock: @unchecked Sendable {
    private let fd: Int32

    public init?(path: String) {
        fd = open(path, O_CREAT | O_RDWR | O_CLOEXEC, 0o600)
        if fd < 0 { return nil }
    }

    deinit { close(fd) }

    /// Waits up to `timeout` seconds. shortcut: polls every 10 ms, which only the short-lived app does while it waits for the
    /// agent to hand over; the agent blocks in `acquire()` instead.
    public func acquire(timeout: TimeInterval) -> Bool {
        let end = Date.now.addingTimeInterval(timeout)
        while flock(fd, LOCK_EX | LOCK_NB) != 0 {
            guard errno == EWOULDBLOCK || errno == EINTR, Date.now < end else { return false }
            usleep(10_000)
        }
        return true
    }

    /// Like `acquire(timeout:)`, but calls `nudge` at once and every `interval` seconds meanwhile. The app uses it to ask the
    /// agent to hand over: a request that reaches the agent just before it knows it holds the lock is ignored, so it is sent again.
    public func acquire(timeout: TimeInterval, nudgeEvery interval: TimeInterval, nudge: () -> Void) -> Bool {
        let end = Date.now.addingTimeInterval(timeout)
        repeat {
            nudge()
            if acquire(timeout: min(interval, max(end.timeIntervalSinceNow, 0))) { return true }
        } while Date.now < end
        return false
    }

    /// Blocks until the lock is free; the kernel wakes the caller, so nothing polls.
    public func acquire() -> Bool {
        while flock(fd, LOCK_EX) != 0 { guard errno == EINTR else { return false } }
        return true
    }

    public func release() { flock(fd, LOCK_UN) }
}

/// The agent's side of the handoff: a thread that sleeps in the kernel until the lock is free, tells its owner it now holds
/// it, and sits out until `release()`. After releasing it waits `settle` seconds before queuing again, so the app that asked
/// for the lock (already waiting) is certain to get it first.
public final class AgentTenure: @unchecked Sendable {
    private let lock: EngineLock
    private let settle: TimeInterval
    private let onAcquire: @Sendable () -> Void
    private let state = NSLock()
    private var holding = false
    private let released = DispatchSemaphore(value: 0)

    public init(lock: EngineLock, settle: TimeInterval = 0.25, onAcquire: @escaping @Sendable () -> Void) {
        self.lock = lock
        self.settle = settle
        self.onAcquire = onAcquire
    }

    public var isHolding: Bool { state.withLock { holding } }

    public func start() {
        let thread = Thread { [self] in
            while lock.acquire() {
                state.withLock { holding = true }
                onAcquire()
                released.wait()
                Thread.sleep(forTimeInterval: settle)
            }
        }
        thread.name = "later.tenure"
        thread.qualityOfService = .utility
        thread.start()
    }

    /// Gives the lock up. Does nothing when not holding it, so a stale request can't end the next tenure early.
    public func release() {
        let wasHolding = state.withLock { () -> Bool in
            defer { holding = false }
            return holding
        }
        guard wasHolding else { return }
        lock.release()
        released.signal()
    }
}
