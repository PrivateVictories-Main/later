import AppKit
import LaterCore

enum ReadError: Error, Equatable {
    case notRunning
    case needsPermission
    case timedOut
    case inconsistent
    /// The browser answered in a way this version doesn't understand (e.g. a new window mode).
    case unexpectedReply
    case failed(Int)
}

func fourCC(_ text: String) -> FourCharCode {
    text.utf8.reduce(0) { $0 << 8 | FourCharCode($1) }
}

/// Reads open tabs through the browser's official AppleScript interface, as raw Apple Events.
/// Events per read: one for the window ids, then per window its mode, plus active tab index, URLs and titles for normal windows.
enum BrowserReader {
    private static let queue = DispatchQueue(label: "later.reader", qos: .utility)

    /// The user's main copy of the browser: the earliest-launched process.
    /// shortcut: picks the oldest process when several copies run (e.g. a second test copy); upgrade if someone runs two real copies.
    /// Events are addressed by its bundle ID, so two running copies sharing one bundle ID can't be told apart.
    static func mainInstance(of browser: Browser) -> NSRunningApplication? {
        browser.bundleIDs.flatMap(NSRunningApplication.runningApplications(withBundleIdentifier:))
            .min { ($0.launchDate ?? .distantFuture) < ($1.launchDate ?? .distantFuture) }
    }

    #if DEBUG
    /// Development only: a dry run of the agent (started with `--dev-agent`) sends no Apple Events, so a build that has no
    /// consent from the browser can't raise a prompt on someone's screen.
    private static let sendsNoEvents = CommandLine.arguments.contains("--dev-agent")
    #else
    private static let sendsNoEvents = false
    #endif

    static func read(_ browser: Browser) async throws -> Snapshot {
        if sendsNoEvents { throw ReadError.needsPermission }
        guard !Sample.enabled, let app = mainInstance(of: browser), let bundleID = app.bundleIdentifier else { throw ReadError.notRunning }
        let pid = app.processIdentifier
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result { try readSync(browser: browser, bundleID: bundleID, pid: pid) })
            }
        }
    }

    /// Shows macOS's one-time "Allow Later to control …" prompt if needed. Nil means allowed.
    static func requestPermission(_ browser: Browser) async -> ReadError? { await permission(browser, ask: true) }

    /// Whether Later may control the browser right now, never showing the prompt: an answer that is still undetermined
    /// comes back as `.needsPermission`. Nil means allowed. Sends nothing to a browser that isn't running.
    static func checkPermission(_ browser: Browser) async -> ReadError? { await permission(browser, ask: false) }

    private static func permission(_ browser: Browser, ask: Bool) async -> ReadError? {
        guard !Sample.enabled, !sendsNoEvents else { return .needsPermission }   // sample runs send no Apple Events
        guard let bundleID = mainInstance(of: browser)?.bundleIdentifier else { return .notRunning }
        return await withCheckedContinuation { continuation in
            queue.async {
                let target = NSAppleEventDescriptor(bundleIdentifier: bundleID)
                let status = AEDeterminePermissionToAutomateTarget(target.aeDesc, fourCC("****"), fourCC("****"), ask)
                continuation.resume(returning: status == noErr ? nil : map(status))
            }
        }
    }

    private static func readSync(browser: Browser, bundleID: String, pid: pid_t) throws -> Snapshot {
        // By bundle ID, not pid: from the sandbox, pid-addressed events failed with -600 (the Apple Events
        // exception is keyed by bundle ID). Measured 2026-10-24 (macOS 27, unsandboxed, faceless System Events
        // as target): a bundle-addressed getd to an app that isn't running fails with -600 and doesn't launch it.
        // Not verified from the sandbox or with a browser, so every event first checks the process read() found
        // is still alive (a browser quitting mid-read). A race remains: the browser can quit between that
        // check and the send, since scheduling can pause the thread there.
        let target = NSAppleEventDescriptor(bundleIdentifier: bundleID)
        func get(_ spec: NSAppleEventDescriptor, from target: NSAppleEventDescriptor) throws -> NSAppleEventDescriptor {
            guard kill(pid, 0) == 0 || errno == EPERM else { throw ReadError.notRunning }
            return try BrowserReader.get(spec, from: target)
        }
        // Per window by its id, passed back exactly as the browser sent it (Helium sends text, not integers):
        // position-paired lists could give a private window another's "normal" mode if windows reorder
        // mid-read, and a private window's tabs are never even requested.
        let idReply = try get(property("ID  ", of: every("cwin", in: .null())), from: target)
        let idItems = idReply.items
        var idShapeLogged = false
        // Diagnostic, once per read: the id reply's shape only, never the id values (they're whatever the browser sends).
        func logIDShape() {
            guard !idShapeLogged else { return }
            idShapeLogged = true
            let itemTypes = Set(idItems.map { $0.descriptorType.text }).sorted().joined(separator: ",")
            Perf.log.info("window ids type=\(idReply.descriptorType.text, privacy: .public) count=\(idReply.numberOfItems, privacy: .public) itemTypes=\(itemTypes, privacy: .public)")
        }
        do {
            return try Snapshot.collect(
                browser: browser, windowIDTexts: idItems.map { $0.stringValue ?? String($0.int32Value) },
                mode: { id in
                    let reply: NSAppleEventDescriptor
                    do { reply = try get(property("mode", of: window(idItems[id])), from: target) } catch { logIDShape(); throw error }
                    let mode = reply.stringValue ?? ""
                    if mode != "normal" && mode != "incognito" {
                        // Diagnostic: the value only if it looks like a mode word, never anything that could be a URL or title.
                        let plain = mode.count <= 24 && mode.allSatisfy { $0.isASCII && ($0.isLetter || " _-".contains($0)) }
                        let value = plain ? "\"\(mode)\"" : "length \(mode.count)"
                        Perf.log.info("window mode unrecognized type=\(reply.descriptorType.text, privacy: .public) value=\(value, privacy: .public)")
                        logIDShape()
                    }
                    return mode
                },
                tabs: { id in
                    let window = window(idItems[id])
                    let tabs = every("CrTb", in: window)
                    return WindowTabs(
                        activeIndex: Int(try get(property("acTI", of: window), from: target).int32Value),
                        urls: try get(property("URL ", of: tabs), from: target).strings(),
                        titles: try get(property("pnam", of: tabs), from: target).strings())
                })
        } catch Snapshot.AssembleError.inconsistent {
            throw ReadError.inconsistent   // a window changed mid-read; the next ring catches up
        } catch Snapshot.AssembleError.unrecognizedMode {
            throw ReadError.unexpectedReply
        }
    }

    private static func window(_ id: NSAppleEventDescriptor) -> NSAppleEventDescriptor {
        let spec = NSAppleEventDescriptor.record()
        spec.setDescriptor(NSAppleEventDescriptor(typeCode: fourCC("cwin")), forKeyword: fourCC("want"))
        spec.setDescriptor(.null(), forKeyword: fourCC("from"))
        spec.setDescriptor(NSAppleEventDescriptor(enumCode: fourCC("ID  ")), forKeyword: fourCC("form"))
        spec.setDescriptor(id, forKeyword: fourCC("seld"))
        return spec.coerce(toDescriptorType: fourCC("obj "))!
    }

    private static func every(_ cls: String, in container: NSAppleEventDescriptor) -> NSAppleEventDescriptor {
        var all = fourCC("all ")
        let spec = NSAppleEventDescriptor.record()
        spec.setDescriptor(NSAppleEventDescriptor(typeCode: fourCC(cls)), forKeyword: fourCC("want"))
        spec.setDescriptor(container, forKeyword: fourCC("from"))
        spec.setDescriptor(NSAppleEventDescriptor(enumCode: fourCC("indx")), forKeyword: fourCC("form"))
        spec.setDescriptor(NSAppleEventDescriptor(descriptorType: fourCC("abso"), bytes: &all, length: 4)!,
                           forKeyword: fourCC("seld"))
        return spec.coerce(toDescriptorType: fourCC("obj "))!
    }

    private static func property(_ code: String, of container: NSAppleEventDescriptor) -> NSAppleEventDescriptor {
        let spec = NSAppleEventDescriptor.record()
        spec.setDescriptor(NSAppleEventDescriptor(typeCode: fourCC("prop")), forKeyword: fourCC("want"))
        spec.setDescriptor(container, forKeyword: fourCC("from"))
        spec.setDescriptor(NSAppleEventDescriptor(enumCode: fourCC("prop")), forKeyword: fourCC("form"))
        spec.setDescriptor(NSAppleEventDescriptor(typeCode: fourCC(code)), forKeyword: fourCC("seld"))
        return spec.coerce(toDescriptorType: fourCC("obj "))!
    }

    private static func get(_ spec: NSAppleEventDescriptor, from target: NSAppleEventDescriptor) throws -> NSAppleEventDescriptor {
        let event = NSAppleEventDescriptor.appleEvent(
            withEventClass: fourCC("core"), eventID: fourCC("getd"), targetDescriptor: target,
            returnID: AEReturnID(kAutoGenerateReturnID), transactionID: AETransactionID(kAnyTransactionID))
        event.setParam(spec, forKeyword: fourCC("----"))
        do {
            let reply = try event.sendEvent(options: [.waitForReply], timeout: 2)
            // sendEvent doesn't throw when the browser's reply carries an error (e.g. -1728 no such object).
            if let errn = reply.paramDescriptor(forKeyword: fourCC("errn")), errn.int32Value != 0 {
                throw map(OSStatus(errn.int32Value))
            }
            return reply.paramDescriptor(forKeyword: fourCC("----")) ?? .list()
        } catch let error as ReadError {
            throw error
        } catch let error as NSError {
            throw map(OSStatus(truncatingIfNeeded: error.code))
        }
    }

    private static func map(_ status: OSStatus) -> ReadError {
        Perf.log.info("apple event status=\(status, privacy: .public)")
        return switch Int(status) {
        case -600: .notRunning                // procNotFound
        case -1743, -1744: .needsPermission   // not permitted / would need consent
        case -1712: .timedOut                 // errAETimeout
        case -1728: .inconsistent             // errAENoSuchObject: a window closed mid-read
        default: .failed(Int(status))
        }
    }
}

private extension NSAppleEventDescriptor {
    var items: [NSAppleEventDescriptor] {
        guard descriptorType == fourCC("list") else { return [self] }
        guard numberOfItems > 0 else { return [] }
        return (1...numberOfItems).compactMap { atIndex($0) }
    }
    func strings() -> [String] { items.map { $0.stringValue ?? "" } }
}

private extension FourCharCode {
    var text: String { String(decoding: withUnsafeBytes(of: bigEndian) { Array($0) }, as: UTF8.self) }
}

extension Browser {
    var isInstalled: Bool {
        bundleIDs.contains { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) != nil }
    }

    /// The running main copy's app, else the first installed copy in `bundleIDs` order.
    var appURL: URL? {
        BrowserReader.mainInstance(of: self)?.bundleURL
            ?? bundleIDs.lazy.compactMap { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) }.first
    }
}
