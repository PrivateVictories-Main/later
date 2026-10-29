import Foundation

/// Rings when a browser writes to any profile's `Sessions/` folder, on kqueue signals only: each Sessions folder and its
/// newest `Session_*` and `Tabs_*` files, plus the data folder (a new profile) and any profile that has no Sessions
/// folder yet. Nothing else in the browser's data folder (cache, cookies, storage) wakes Later.
/// It only learns *that* something changed; it lists names but never reads those files.
@MainActor
public final class Doorbell {
    private var started = false
    private var folder = ""
    private var watches: [String: DispatchSourceFileSystemObject] = [:]
    private let onRing: @MainActor () -> Void

    public init(onRing: @escaping @MainActor () -> Void) {
        self.onRing = onRing
    }

    /// Returns false when macOS won't let the app watch the folder.
    public func start(folder: String) -> Bool {
        guard !started else { return true }
        guard WatchProbe.canWatch(folder: folder) else { return false }
        self.folder = folder
        started = true
        attach()
        guard watches[folder] != nil else {
            stop()
            return false
        }
        return true
    }

    public func stop() {
        watches.values.forEach { $0.cancel() }
        watches = [:]
        started = false
    }

    /// Watches the data folder, every profile's Sessions folder and its newest files; drops watches on paths no longer current.
    /// A profile that has no Sessions folder yet is watched until it gets one.
    private func attach() {
        guard started else { return }
        let fm = FileManager.default
        var wanted: [String: Bool] = [folder: true]  // path → is a folder (its changes attach again)
        for profile in (try? fm.contentsOfDirectory(atPath: folder)) ?? [] {
            let profilePath = folder + "/" + profile
            let sessions = profilePath + "/Sessions"
            guard let names = try? fm.contentsOfDirectory(atPath: sessions) else {
                var isFolder: ObjCBool = false
                if fm.fileExists(atPath: profilePath, isDirectory: &isFolder), isFolder.boolValue { wanted[profilePath] = true }
                continue
            }
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
