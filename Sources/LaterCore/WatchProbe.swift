import Foundation

public enum WatchProbe {
    public enum Access: Equatable, Sendable {
        case watchable
        /// The browser's data folder doesn't exist yet: it's installed but has never been opened.
        case missing
        /// macOS denies listing the folder or its profile's Sessions folder.
        case denied
    }

    /// A browser's profile folder can be blocked even when its top folder is visible, so `Default/Sessions` is
    /// checked too. A Sessions folder that doesn't exist yet is fine: the watcher sees it once the browser creates it.
    /// Lists names only; never opens a file.
    public static func access(folder: String) -> Access {
        let fm = FileManager.default
        for (path, missing) in [(folder, Access.missing), (folder + "/Default/Sessions", .watchable)] {
            do {
                _ = try fm.contentsOfDirectory(atPath: path)
            } catch CocoaError.fileReadNoSuchFile {
                return missing
            } catch {
                return .denied
            }
        }
        return .watchable
    }

    public static func canWatch(folder: String) -> Bool { access(folder: folder) == .watchable }
}
