import AppKit
import Foundation
import Testing
@testable import Later
@testable import LaterCore

/// Site icons kept per saved host, in memory and as PNGs, with no Helium cache to read (its folder doesn't exist).
@Suite @MainActor struct SiteIconsTests {
    private let fm = FileManager.default
    private let t0 = Date(timeIntervalSince1970: 1_800_000_000)

    /// Runs `body` with a fresh icon folder, always removed afterwards.
    private func withFolder(_ body: (URL) async throws -> Void) async throws {
        let folder = fm.temporaryDirectory.appendingPathComponent("LaterIcons-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: folder) }
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        try await body(folder)
    }

    private func icons(_ library: Library, _ folder: URL) -> SiteIcons {
        SiteIcons(library: library, folder: folder, helium: folder.appendingPathComponent("no-helium").path)
    }

    private func library(_ hosts: String...) -> Library {
        let library = Library()
        for host in hosts {
            library.save(url: "https://\(host)/page", title: host, verdict: Verdict(isSpecific: true, kind: .reading, pageType: host),
                         browser: .helium, now: t0)
        }
        return library
    }

    /// A kept 16 px PNG: a solid square, so it decodes to something visible.
    private func keepPNG(_ host: String, in folder: URL) throws {
        let context = try #require(CGContext(data: nil, width: 16, height: 16, bitsPerComponent: 8, bytesPerRow: 0,
                                             space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                             bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(red: 0.9, green: 0.3, blue: 0.1, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: 16, height: 16))
        let image = try #require(context.makeImage())
        let png = try #require(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
        try png.write(to: SiteIcons.file(for: host, in: folder))
    }

    /// A kept 32 px PNG with a 12 × 6 px black bar off to one side, and nothing else.
    private func keepMarkPNG(_ host: String, in folder: URL) throws {
        let context = try #require(CGContext(data: nil, width: 32, height: 32, bitsPerComponent: 8, bytesPerRow: 0,
                                             space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                             bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(red: 0, green: 0, blue: 0, alpha: 1)
        context.fill(CGRect(x: 3, y: 20, width: 12, height: 6))
        let image = try #require(context.makeImage())
        let png = try #require(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
        try png.write(to: SiteIcons.file(for: host, in: folder))
    }

    /// Every pixel of an icon's image is solid: it was cut to what shows.
    private func isSolid(_ icon: SiteIcons.Icon) throws -> Bool {
        let image = try #require(icon.image.cgImage(forProposedRect: nil, context: nil, hints: nil))
        var pixels = [UInt8](repeating: 0, count: image.width * image.height * 4)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: image.width, height: image.height, bitsPerComponent: 8,
                                          bytesPerRow: image.width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
            return true
        }
        #expect(drawn)
        return stride(from: 3, to: pixels.count, by: 4).allSatisfy { pixels[$0] == 255 }
    }

    private func isKept(_ host: String, in folder: URL) -> Bool {
        fm.fileExists(atPath: SiteIcons.file(for: host, in: folder).path)
    }

    /// Waits for the lookup queue's work so far, and the main-thread work it hands back.
    private func settle() async {
        await withCheckedContinuation { done in
            SiteIcons.queue.async { DispatchQueue.main.async { done.resume() } }
        }
    }

    @Test func aDeletedHostLosesItsIconAndItsPNG() async throws {
        try await withFolder { folder in
            let library = library("a.test", "b.test")
            for host in ["a.test", "b.test", "stale.test"] { try keepPNG(host, in: folder) }
            let icons = icons(library, folder)
            icons.update()
            await settle()
            #expect(Set(icons.icons.keys) == ["a.test", "b.test"])
            #expect(!isKept("stale.test", in: folder))   // trimmed to saved hosts on the first look
            library.delete(library.data.items[0].id, now: t0)
            icons.libraryChanged()
            #expect(Set(icons.icons.keys) == ["b.test"])
            await settle()
            #expect(!isKept("a.test", in: folder))
            #expect(isKept("b.test", in: folder))
        }
    }

    /// A small black bar in a wide clear margin is cut to the bar, so tiles size it by what shows, and it's drawn as a
    /// mark (in the label color); a solid colored square stays whole and keeps its color.
    @Test func iconsAreCutToWhatShows() async throws {
        try await withFolder { folder in
            let library = library("mark.test", "square.test")
            try keepMarkPNG("mark.test", in: folder)
            try keepPNG("square.test", in: folder)
            let icons = icons(library, folder)
            icons.update()
            await settle()
            let mark = try #require(icons.icons["mark.test"])
            #expect(mark.pixels == 12)
            #expect(try isSolid(mark))
            #expect(mark.isMark && mark.tint == nil)
            let square = try #require(icons.icons["square.test"])
            #expect(square.pixels == 16)
            #expect(!square.isMark && square.tint != nil)
        }
    }

    @Test func anEmptyListStillTrimsTheCache() async throws {
        try await withFolder { folder in
            try keepPNG("stale.test", in: folder)
            let icons = icons(library(), folder)
            icons.update()
            await settle()
            #expect(!isKept("stale.test", in: folder))
        }
    }

    @Test func aHostDeletedDuringTheLookupIsLeftOut() async throws {
        try await withFolder { folder in
            let library = library("a.test")
            try keepPNG("a.test", in: folder)
            let icons = icons(library, folder)
            icons.update()   // the lookup is on its way
            library.delete(library.data.items[0].id, now: t0)
            icons.libraryChanged()
            await settle()
            await settle()
            #expect(icons.icons.isEmpty)
            #expect(!isKept("a.test", in: folder))
        }
    }
}
