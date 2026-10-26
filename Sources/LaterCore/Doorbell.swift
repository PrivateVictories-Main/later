import CoreServices
import Foundation

/// Rings when a browser writes to any profile's `Sessions/` folder, on whichever signal comes first:
/// FSEvents on the data folder, or kqueue on each Sessions folder and its newest `Session_*` and `Tabs_*` files.
/// It only learns *that* something changed; it lists names but never reads those files.
@MainActor
public final class Doorbell {
    private var stream: FSEventStreamRef?
    private var folder = ""
    private var watches: [String: DispatchSourceFileSystemObject] = [:]
    private let onRing: @MainActor () -> Void

    public init(onRing: @escaping @MainActor () -> Void) {
        self.onRing = onRing
    }

    /// Returns false when macOS won't let the app watch the folder.
    public func start(folder: String) -> Bool {
        guard stream == nil else { return true }
        guard WatchProbe.canWatch(folder: folder) else { return false }
        var context = FSEventStreamContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(),
                                           retain: nil, release: nil, copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, _, paths, _, _ in
            guard let info else { return }
            let bell = Unmanaged<Doorbell>.fromOpaque(info).takeUnretainedValue()
            let list = (Unmanaged<CFArray>.fromOpaque(paths).takeUnretainedValue() as NSArray) as? [String] ?? []
            // "…/Sessions" alone is a Sessions folder appearing, e.g. in a new profile.
            guard list.contains(where: { $0.contains("/Sessions/") || $0.hasSuffix("/Sessions") }) else { return }
            MainActor.assumeIsolated {
                bell.attach()
                bell.onRing()
            }
        }
        let flags = FSEventStreamCreateFlags(kFSEventStreamCreateFlagUseCFTypes | kFSEventStreamCreateFlagFileEvents)
        // 0.5 s latency lets FSEvents merge a burst of writes into one ring. No app timer involved.
        guard let created = FSEventStreamCreate(nil, callback, &context, [folder] as CFArray,
                                                FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.5, flags)
        else { return false }
        FSEventStreamSetDispatchQueue(created, .main)
        guard FSEventStreamStart(created) else {
            FSEventStreamInvalidate(created)
            FSEventStreamRelease(created)
            return false
        }
        stream = created
        self.folder = folder
        attach()
        return true
    }

    public func stop() {
        watches.values.forEach { $0.cancel() }
        watches = [:]
        guard let stream else { return }
        FSEventStreamStop(stream)
        FSEventStreamInvalidate(stream)
        FSEventStreamRelease(stream)
        self.stream = nil
    }

    /// Watches every profile's Sessions folder and its newest files; drops watches on paths no longer current.
    private func attach() {
        guard stream != nil else { return }
        let fm = FileManager.default
        var wanted: [String: Bool] = [:]  // path → is a Sessions folder
        for profile in (try? fm.contentsOfDirectory(atPath: folder)) ?? [] {
            let sessions = folder + "/" + profile + "/Sessions"
            guard let names = try? fm.contentsOfDirectory(atPath: sessions) else { continue }
            wanted[sessions] = true
            // Chromium names them <prefix><timestamp>, so the largest name is the newest.
            for prefix in ["Session_", "Tabs_"] {
                if let newest = names.filter({ $0.hasPrefix(prefix) }).max() { wanted[sessions + "/" + newest] = false }
            }
        }
        for (path, source) in watches where wanted[path] == nil {
            source.cancel()
            watches[path] = nil
        }
        for (path, isFolder) in wanted where watches[path] == nil {
            watches[path] = watch(path, mask: isFolder ? .write : [.write, .extend]) { [weak self] in
                if isFolder { self?.attach() }
                self?.onRing()
            }
        }
    }

    private func watch(_ path: String, mask: DispatchSource.FileSystemEvent,
                       handler: @escaping @MainActor () -> Void) -> DispatchSourceFileSystemObject? {
        // O_EVTONLY handles only receive change notices; no file contents are read.
        let fd = open(path, O_EVTONLY)
        guard fd >= 0 else { return nil }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: mask, queue: .main)
        source.setEventHandler { MainActor.assumeIsolated { handler() } }
        source.setCancelHandler { close(fd) }
        source.resume()
        return source
    }
}
