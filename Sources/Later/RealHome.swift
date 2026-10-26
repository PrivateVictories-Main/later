import Foundation

/// The user's real home folder. Inside the sandbox, `NSHomeDirectory()` is the app's container.
enum RealHome {
    static let path: String = {
        guard let entry = getpwuid(getuid()), let dir = entry.pointee.pw_dir else { return NSHomeDirectory() }
        return String(cString: dir)
    }()
}
