import AppKit
import ImageIO
import LaterCore
import Observation
import SwiftUI

/// Saved sites' icons, from Helium's own icon cache (see FaviconLookup). Each is decoded once and kept per host, in
/// memory and as a PNG in Later's Caches folder, so icons survive restarts and Helium's own cache cleanups.
/// Looked up only for saved pages' hosts, when the window opens or the list gains a page: no timers. A host that leaves
/// the list loses its icon, in memory and on disk.
@MainActor @Observable
final class SiteIcons {
    struct Icon {
        let image: NSImage
        /// Its longer side in pixels, without the clear margin around it: how large it can be drawn before it blurs.
        let pixels: CGFloat
        /// The icon's own color, for its tile; nil when the icon is mostly gray or many-colored.
        let tint: NSColor?
        /// A gray or black mark on a clear ground (Wikipedia's W, Apple's apple): drawn in the label color instead, so
        /// it reads in light and dark alike.
        let isMark: Bool
    }

    private(set) var icons: [String: Icon] = [:]
    @ObservationIgnored private let library: Library
    /// Where the PNGs are kept; nil for a sample list, whose icons stay in memory.
    @ObservationIgnored private let folder: URL?
    /// Helium's data folder, whose profiles' icon caches are read.
    @ObservationIgnored private let helium: String
    /// Hosts with no icon anywhere, and when that was found, so they're asked about at most once an hour.
    @ObservationIgnored private var misses: [String: Date] = [:]
    @ObservationIgnored private var running = false
    @ObservationIgnored private var runAgain = false
    @ObservationIgnored private var pruned = false
    /// The list's size and newest entry when last looked at: a page was added if either changed.
    @ObservationIgnored private var mark: (count: Int, last: UUID?) = (0, nil)
    static let queue = DispatchQueue(label: "later.icons", qos: .utility)
    /// Later's own folder for them, in its Caches.
    nonisolated static var folder: URL? {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first?.appendingPathComponent("Icons")
    }

    init(library: Library, folder: URL?, helium: String = RealHome.path + "/" + Browser.helium.dataFolder) {
        self.library = library
        self.folder = folder
        self.helium = helium
        mark = (library.data.items.count, library.data.items.last?.id)
    }

    /// Called on every change to the list; looks further only when a page was added or removed.
    func libraryChanged() {
        let items = library.data.items
        guard items.count != mark.count || items.last?.id != mark.last else { return }
        mark = (items.count, items.last?.id)
        update()
    }

    /// Forgets hosts that left the list, then looks up the saved hosts that have no icon yet. One lookup runs at a time; a
    /// call meanwhile runs once it ends.
    func update() {
        let hosts = Set(library.data.items.map(\.host))
        let gone = Set(icons.keys).union(misses.keys).subtracting(hosts)
        if !gone.isEmpty {
            icons = icons.filter { hosts.contains($0.key) }
            misses = misses.filter { hosts.contains($0.key) }
            removeFiles(gone)
        }
        guard !running else {
            runAgain = true
            return
        }
        guard !Sample.enabled else { return }   // a sample run never opens Helium's icon cache (see Sample.iconPNGs)
        let now = Date()
        var pages: [String: [String]] = [:]
        for item in library.data.items.reversed() where icons[item.host] == nil {   // newest saves first
            if let missed = misses[item.host], now.timeIntervalSince(missed) < 3600 { continue }
            if pages[item.host, default: []].count < 3 { pages[item.host, default: []].append(item.url) }
        }
        let keep = pruned || folder == nil ? nil : hosts   // the PNG cache is trimmed to saved hosts once a launch, even to none
        guard !pages.isEmpty || keep != nil else { return }
        running = true
        pruned = true
        let folder = self.folder, helium = self.helium, asked = pages
        let start = ContinuousClock.now
        Self.queue.async { [weak self] in
            let results = Self.fetch(asked, folder: folder, keep: keep, helium: helium)
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.finish(results, asked: asked.count, start: start) }
            }
        }
    }

    private func finish(_ results: [String: Fetched], asked: Int, start: ContinuousClock.Instant) {
        running = false
        let now = Date()
        // Hosts deleted while the lookup ran are left out, and the PNGs it just kept for them go too.
        let hosts = Set(library.data.items.map(\.host))
        let gone = results.keys.filter { !hosts.contains($0) }
        if !gone.isEmpty { removeFiles(gone) }
        var found: [String: Icon] = [:]
        for (host, result) in results where hosts.contains(host) {
            switch result {
            case .icon(let decoded): found[host] = Icon(decoded)
            case .missing: misses[host] = now
            }
        }
        // Hosts in neither list couldn't be read just now; they're asked about again on the next open or save.
        if !found.isEmpty { icons.merge(found) { $1 } }
        Perf.log.info("icons asked=\(asked, privacy: .public) found=\(found.count, privacy: .public) missing=\(results.count - found.count - gone.count, privacy: .public) gone=\(gone.count, privacy: .public) ms=\(Perf.ms(since: start), format: .fixed(precision: 1), privacy: .public)")
        if runAgain {
            runAgain = false
            update()
        }
    }

    /// Removes kept PNGs on the lookup queue, so they go after any lookup still writing them.
    private func removeFiles(_ hosts: some Collection<String> & Sendable) {
        guard let folder else { return }
        Self.queue.async {
            for host in hosts { try? FileManager.default.removeItem(at: Self.file(for: host, in: folder)) }
        }
    }

    // MARK: Off the main thread

    fileprivate enum Fetched: Sendable {
        case icon(Decoded)
        case missing
    }

    /// A decoded icon on its way to the main actor. A CGImage never changes, so passing it along is safe.
    fileprivate struct Decoded: @unchecked Sendable {
        let image: CGImage
        let tint: (red: Double, green: Double, blue: Double)?
        let isMark: Bool
    }

    nonisolated private static func fetch(_ pages: [String: [String]], folder: URL?, keep: Set<String>?,
                                          helium: String) -> [String: Fetched] {
        var results: [String: Fetched] = [:]
        var lookUp = pages
        if let folder {
            if let keep { prune(folder, keeping: keep) }
            // shortcut: a kept PNG wins until its site leaves the list, so a site's new favicon never replaces it; ask Helium
            // again when the PNG is old if that ever matters.
            for host in pages.keys {
                guard let data = try? Data(contentsOf: file(for: host, in: folder)), let decoded = decode(data) else { continue }
                results[host] = .icon(decoded)
                lookUp[host] = nil
            }
        }
        guard !lookUp.isEmpty else { return results }
        let databases = FaviconLookup.databases(in: helium)
        // Each bitmap is decoded once; one that's blank or not an image is passed over for the next one Helium has.
        var decodedBitmaps: [Data: Decoded] = [:]
        let outcomes = FaviconLookup.icons(for: lookUp, in: databases) { data in
            guard let decoded = decode(data) else { return false }
            decodedBitmaps[data] = decoded
            return true
        }
        for (host, outcome) in outcomes {
            switch outcome {
            case .found(let data):
                guard let decoded = decodedBitmaps[data] else { continue }
                results[host] = .icon(decoded)
                if let folder {
                    try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                    try? data.write(to: file(for: host, in: folder), options: .atomic)
                }
            case .missing:
                results[host] = .missing
            case .retryLater:
                continue
            }
        }
        return results
    }

    nonisolated static func file(for host: String, in folder: URL) -> URL {
        let name = host.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: ".-"))) ?? "_"
        return folder.appendingPathComponent(name + ".png")
    }

    nonisolated private static func prune(_ folder: URL, keeping hosts: Set<String>) {
        let kept = Set(hosts.map { file(for: $0, in: folder).lastPathComponent })
        for name in (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? [] where !kept.contains(name) {
            try? FileManager.default.removeItem(at: folder.appendingPathComponent(name))
        }
    }

    /// Decodes at most 64 px, once, trims the clear margin around what shows, and finds the icon's color from its
    /// visible pixels. Nil for an image that can't be read or shows nothing at all.
    nonisolated private static func decode(_ data: Data) -> Decoded? {
        let options = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 64,
                       kCGImageSourceShouldCacheImmediately: true] as CFDictionary
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, options),
              let sRGB = CGColorSpace(name: CGColorSpace.sRGB),
              let image = trimmed(thumbnail, in: sRGB)
        else { return nil }
        let side = 16
        var pixels = [UInt8](repeating: 0, count: side * side * 4)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: side, height: side, bitsPerComponent: 8,
                                          bytesPerRow: side * 4, space: sRGB,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: side, height: side))
            return true
        }
        guard drawn else { return nil }
        var sum = (red: 0.0, green: 0.0, blue: 0.0), count = 0.0, colorful = 0.0, light = 0.0, dark = 0.0
        for i in stride(from: 0, to: pixels.count, by: 4) where pixels[i + 3] >= 128 {
            let alpha = Double(pixels[i + 3])
            let (red, green, blue) = (Double(pixels[i]) / alpha, Double(pixels[i + 1]) / alpha, Double(pixels[i + 2]) / alpha)
            sum.red += red
            sum.green += green
            sum.blue += blue
            count += 1
            if max(red, green, blue) - min(red, green, blue) > 0.2 { colorful += 1 }
            let luma = 0.3 * red + 0.59 * green + 0.11 * blue
            if luma > 0.6 { light += 1 } else if luma < 0.4 { dark += 1 }
        }
        guard count > 0 else { return nil }
        let average = (red: sum.red / count, green: sum.green / count, blue: sum.blue / count)
        let color = NSColor(srgbRed: average.red, green: average.green, blue: average.blue, alpha: 1)
        // Many colors average out to gray, so only a clearly colored icon tints its tile.
        let colored = color.saturationComponent >= 0.3 && color.brightnessComponent >= 0.25
        // No color and one tone, not light and dark together: its shape is all there is to it.
        let isMark = colorful <= count * 0.05 && min(light, dark) <= count * 0.05
        return Decoded(image: image, tint: colored ? average : nil, isMark: isMark)
    }

    /// The image without its clear margin: many favicons draw a small mark in the middle of their square, and it's the
    /// mark whose size matters on a tile. Nil when nothing in it shows.
    nonisolated private static func trimmed(_ image: CGImage, in space: CGColorSpace) -> CGImage? {
        let (width, height) = (image.width, image.height)
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                          bytesPerRow: width * 4, space: space,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return nil }
        // Rows run from the top, as cropping counts them.
        var (left, top, right, bottom) = (width, height, -1, -1)
        for y in 0..<height {
            for x in 0..<width where pixels[(y * width + x) * 4 + 3] >= 24 {
                (left, right, top, bottom) = (min(left, x), max(right, x), min(top, y), max(bottom, y))
            }
        }
        guard right >= left else { return nil }
        if left == 0, top == 0, right == width - 1, bottom == height - 1 { return image }
        return image.cropping(to: CGRect(x: left, y: top, width: right - left + 1, height: bottom - top + 1))
    }

    #if DEBUG
    /// A sample run's icons (see Sample.iconPNGs), read like any other.
    func show(samples: [String: Data]) {
        icons = samples.compactMapValues { Self.decode($0).map(Icon.init) }
    }
    #endif
}

private extension SiteIcons.Icon {
    init(_ decoded: SiteIcons.Decoded) {
        image = NSImage(cgImage: decoded.image, size: NSSize(width: decoded.image.width / 2, height: decoded.image.height / 2))
        pixels = CGFloat(max(decoded.image.width, decoded.image.height))
        tint = decoded.tint.map { NSColor(srgbRed: $0.red, green: $0.green, blue: $0.blue, alpha: 1) }
        isMark = decoded.isMark
    }
}
