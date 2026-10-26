import AppKit
import LaterCore
import SwiftUI

/// One color per kind, shared by its sidebar badge, its header, its selection and its empty state, so a kind reads the
/// same everywhere. System colors adapt to dark mode and Increase Contrast by themselves.
extension Kind {
    var color: Color {
        switch self {
        case .shopping: .orange
        case .posts: .blue
        case .school: .purple
        case .reading: .teal
        case .videos: .pink
        case .tools: .yellow
        case .other: .gray
        }
    }

    /// The color for words and thin symbols: the kind's own, except yellow, which on white needs a deep amber to read.
    var labelColor: Color { self == .tools ? Self.toolsLabel : color }

    private static let toolsLabel = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            ? .systemYellow : NSColor(srgbRed: 0.55, green: 0.38, blue: 0, alpha: 1)
    })

    /// The solid symbol, for white-on-color badges.
    var badgeSymbol: String {
        switch self {
        case .shopping: "bag.fill"
        case .posts: "bubble.left.and.bubble.right.fill"
        case .school: "graduationcap.fill"
        case .reading: "book.fill"
        case .videos: "play.rectangle.fill"
        case .tools: "wrench.and.screwdriver.fill"
        case .other: "square.grid.2x2.fill"
        }
    }
}

extension Color {
    /// Later's own color, the indigo of its icon's frosted cards: the All section and the main buttons.
    static let brand = Color(nsColor: .systemIndigo)

    /// Secondary text, a step stronger than the system's: its 50% gray measures about 4:1 on white and on Settings'
    /// gray, under the 4.5:1 small text needs, and reads faint in dark mode. Increase Contrast keeps the system's own.
    static let secondaryText = Color(nsColor: NSColor(name: nil) { appearance in
        switch appearance.bestMatch(from: [.aqua, .darkAqua, .accessibilityHighContrastAqua, .accessibilityHighContrastDarkAqua]) {
        case .aqua?: NSColor(white: 0, alpha: 0.62)
        case .darkAqua?: NSColor(white: 1, alpha: 0.7)
        default: .secondaryLabelColor
        }
    })

    /// The list's ground, also under its floating headings.
    static let listGround = Color(nsColor: .textBackgroundColor)

    /// Delete's red: the system red reads dim on dark glass, so dark mode gets a lighter one.
    static let destructive = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            ? NSColor(srgbRed: 1, green: 0.45, blue: 0.43, alpha: 1) : .systemRed
    })
}

extension LibrarySection {
    var title: String {
        switch self {
        case .all: "All"
        case .kind(let kind): kind.title
        case .older: "Older"
        case .done: "Done"
        }
    }

    var symbol: String {
        switch self {
        case .all: "tray.full"
        case .kind(let kind): kind.symbol
        case .older: "clock"
        case .done: "checkmark.circle"
        }
    }

    var badgeSymbol: String {
        switch self {
        case .all: "tray.full.fill"
        case .kind(let kind): kind.badgeSymbol
        case .older: "clock.fill"
        case .done: "checkmark"
        }
    }

    /// The section's color: its badge, its title, and the selection in its list.
    var tint: Color {
        switch self {
        case .all: .brand
        case .kind(let kind): kind.color
        case .older: Color(nsColor: .systemGray)
        case .done: .green
        }
    }

    /// The sidebar's order, also the order of ⌘1 to ⌘9.
    static let ordered: [LibrarySection] = [.all] + Kind.allCases.map(LibrarySection.kind) + [.older, .done]
}

extension LibraryGrouping {
    var title: String {
        switch self {
        case .date: "Date"
        case .site: "Site"
        case .kind: "Kind"
        }
    }
}

extension LibrarySort {
    var title: String {
        switch self {
        case .tabOrder: "Tab Order (Left to Right)"
        case .tabOrderReversed: "Tab Order (Right to Left)"
        case .dateOpened: "Date Opened"
        case .dateSaved: "Date Saved"
        case .lastSeen: "Last Seen"
        case .title: "Title"
        case .site: "Site"
        }
    }

    /// The two directions' names, newest or A first: dates run newest first by default and text A to Z. The tab orders
    /// are their own directions.
    var directionTitles: (forward: String, reversed: String)? {
        switch self {
        case .tabOrder, .tabOrderReversed: nil
        case .title, .site: ("A to Z", "Z to A")
        case .dateOpened, .dateSaved, .lastSeen: ("Newest First", "Oldest First")
        }
    }
}

/// A section's symbol, white on a disc of its color with a soft top light: the sidebar's icons and the list's header.
struct SectionBadge: View {
    let section: LibrarySection
    var size: CGFloat = 20

    var body: some View {
        Image(systemName: section.badgeSymbol)
            .font(.system(size: size * 0.48, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(section.tint.gradient, in: Circle())
            .overlay(Circle().strokeBorder(.white.opacity(0.22), lineWidth: 0.5))
            .accessibilityHidden(true)
    }
}

extension Item {
    /// Titles can be empty while a page loads; the link stands in.
    var displayTitle: String { title.isEmpty ? url : title }
}

extension NSColor {
    /// A color of its own for a site with no icon, the same on every launch (an FNV-1a hash of the host picks it).
    static func site(_ host: String) -> NSColor {
        let palette: [NSColor] = [.systemBlue, .systemIndigo, .systemPurple, .systemPink, .systemRed, .systemOrange,
                                  .systemGreen, .systemTeal, .systemCyan, .systemBrown]
        let hash = host.utf8.reduce(UInt64(0xcbf2_9ce4_8422_2325)) { ($0 ^ UInt64($1)) &* 0x100_0000_01b3 }
        return palette[Int(hash % UInt64(palette.count))]
    }

    /// This color, a shade deeper in light mode and a shade lighter in dark mode, so a letter on its own soft tint
    /// stays easy to read.
    var legibleOnTint: NSColor {
        NSColor(name: nil) { appearance in
            var resolved = self
            appearance.performAsCurrentDrawingAppearance { resolved = self.usingColorSpace(.sRGB) ?? self }
            let light = appearance.bestMatch(from: [.aqua, .darkAqua]) == .aqua
            return (light ? resolved.blended(withFraction: 0.35, of: .black) : resolved.blended(withFraction: 0.25, of: .white))
                ?? resolved
        }
    }
}
