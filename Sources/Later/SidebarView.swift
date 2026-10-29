import AppKit
import LaterCore
import SwiftUI

/// The window's sidebar, on the system's glass: the library by kind, then Older and Done, each with its own colored
/// badge and count, and what Later is doing at the foot.
struct SidebarPane: View {
    @Bindable var model: LibraryModel
    let openSettings: () -> Void

    var body: some View {
        let counts = model.counts
        // The selection's own type: List(selection:) matches LibrarySection tags, never Optional ones. Clicking empty
        // sidebar space sends nil, which keeps the section.
        List(selection: Binding<LibrarySection?> { model.section } set: { if let new = $0 { model.section = new } }) {
            Section("Library") {
                row(.all, counts)
                ForEach(Kind.allCases) { kind in row(.kind(kind), counts) }
            }
            Section {
                row(.older, counts)
                row(.done, counts)
            }
            Section {
                row(.regulars, counts)
            }
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .bottom, spacing: 0) { StatusLine(engine: model.engine, openSettings: openSettings) }
    }

    private func row(_ section: LibrarySection, _ counts: [LibrarySection: Int]) -> some View {
        Label {
            Text(section.title)
        } icon: {
            SectionBadge(section: section, size: 22)
        }
        .badge(counts[section] ?? 0)
        .tag(section)
    }
}

/// The sidebar's foot: what Later is doing, and anything the user needs to fix, with the button that fixes it.
struct StatusLine: View {
    let engine: Engine
    let openSettings: () -> Void

    var body: some View {
        let watching = Browser.allCases.filter { engine.status[$0] == .watching }
        let problems = Browser.allCases.compactMap { browser -> String? in
            guard let status = engine.status[browser], status.isProblem else { return nil }
            return status.text(for: browser)
        }
        VStack(alignment: .leading, spacing: 10) {
            if !problems.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(problems, id: \.self) { text in
                        Label {
                            Text(text).foregroundStyle(.primary)
                        } icon: {
                            WarningSymbol()
                        }
                    }
                    Button("Open Settings…", action: openSettings)
                        .buttonStyle(.glass)
                        .controlSize(.small)
                }
            }
            if engine.paused {
                HStack(spacing: 8) {
                    Image(systemName: "pause.circle.fill").foregroundStyle(.orange)
                    Text("Paused")
                    Spacer()
                    Button("Resume") { engine.setPaused(false) }
                        .buttonStyle(.glass)
                        .controlSize(.small)
                }
            } else if !watching.isEmpty {
                HStack(spacing: 8) {
                    Circle()
                        .fill(.green.gradient)
                        .frame(width: 8, height: 8)
                        .shadow(color: .green.opacity(0.6), radius: 3)
                        .accessibilityHidden(true)
                    Text("Watching " + BrowserStatus.names(watching))
                }
            }
        }
        .font(.callout)
        .foregroundStyle(Color.secondaryText)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 18)
        .padding(.vertical, 14)
    }
}
