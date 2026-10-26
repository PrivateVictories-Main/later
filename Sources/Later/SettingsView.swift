import AppKit
import LaterCore
import ServiceManagement
import SwiftUI

/// Launch at login. Only the copy installed in /Applications registers, so development builds never
/// add a login item or trigger background-item alerts.
/// One shared state, so Settings shows what the automatic attempts did too.
@MainActor @Observable
final class LoginItem {
    static let shared = LoginItem()
    /// Set once launch at login has been decided, by the first-run default or by the user's own toggle, so it's never forced again.
    static let defaultAppliedKey = "loginDefaultApplied"
    static var isInstalled: Bool { Bundle.main.bundlePath.hasPrefix("/Applications/") }
    private(set) var status = SMAppService.mainApp.status
    /// Why macOS refused the last change; nil once a change goes through.
    private(set) var failure: String?

    func setEnabled(_ on: Bool) {
        guard Self.isInstalled, !Sample.enabled else { return }   // a sample run changes nothing, this included
        UserDefaults.standard.set(true, forKey: Self.defaultAppliedKey)
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            failure = nil
        } catch {
            Perf.log.error("login item change failed")   // the reason shows in Settings; logs carry no payload
            failure = error.localizedDescription
        }
        refresh()
    }

    /// On by default, unless it was already decided.
    func applyDefault() {
        if !UserDefaults.standard.bool(forKey: Self.defaultAppliedKey) { setEnabled(true) }
    }

    func refresh() { status = SMAppService.mainApp.status }
}

/// Once the user has said no to macOS's prompt it never shows again; only System Settings can change it.
@MainActor
func openAutomationSettings() {
    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation")!)
}

struct SettingsView: View {
    let engine: Engine
    @Bindable var model: LibraryModel
    /// Refreshed by every change and whenever Later comes forward, e.g. back from Login Items Settings.
    private let login = LoginItem.shared
    @AppStorage("onboarded") private var onboarded = false

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable()
                        .frame(width: 56, height: 56)
                        .shadow(color: Color.brand.opacity(0.3), radius: 10, y: 4)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Later").font(.system(size: 20, weight: .bold, design: .rounded))
                        StatusSummary(engine: engine, onboarded: onboarded || Sample.enabled)
                    }
                }
                .padding(.vertical, 4)
            }
            Section {
                // Shows what macOS actually did, so a failed registration flips back.
                Toggle(isOn: Binding(get: { login.status == .enabled || login.status == .requiresApproval },
                                     set: { login.setEnabled($0) })) {
                    SettingLabel(symbol: "power", color: .green, title: "Open at login",
                                 detail: "Later starts in the background when you log in, with no window.")
                }
                .disabled(!LoginItem.isInstalled)
                if LoginItem.isInstalled, login.status == .requiresApproval {
                    LabeledContent {
                        Button("Open Login Items Settings…") { SMAppService.openSystemSettingsLoginItems() }
                    } label: {
                        Text("Waiting for your OK in Login Items.").foregroundStyle(Color.secondaryText)
                    }
                }
                Toggle(isOn: Binding(get: { engine.paused }, set: { engine.setPaused($0) })) {
                    SettingLabel(symbol: "pause.fill", color: .orange, title: "Pause watching",
                                 detail: "Later stops keeping new pages until you turn this back on.")
                }
                Toggle(isOn: Binding(get: { engine.savesFeedHomes }, set: { engine.setSavesFeedHomes($0) })) {
                    SettingLabel(symbol: "rectangle.stack.fill", color: .indigo, title: "Also save feed home pages",
                                 detail: "Keeps the YouTube, X, Reddit, Facebook, Instagram and Amazon home pages, which Later skips by default.")
                }
                Picker(selection: $model.sort) {
                    ForEach(LibrarySort.allCases, id: \.self) { Text($0.title).tag($0) }
                } label: {
                    SettingLabel(symbol: "arrow.up.arrow.down", color: .blue, title: "Default order",
                                 detail: "How your pages are ordered. The toolbar's Sort By changes it too.")
                }
            } header: {
                Text("General")
            } footer: {
                Group {
                    if !LoginItem.isInstalled {
                        Text("Open at login is available once Later is in your Applications folder.")
                    } else if let failure = login.failure {
                        Text("macOS didn't accept the login item: \(failure) Try again, or use Login Items in System Settings.")
                    } else if login.status == .notFound {
                        Text("macOS can't find Later's login item right now.")
                    }
                }
                .footnote()
            }
            Section {
                if onboarded || Sample.enabled {
                    ForEach(Browser.allCases, id: \.self) { browser in BrowserRow(engine: engine, browser: browser) }
                } else {
                    Text("Starts watching after setup.").foregroundStyle(Color.secondaryText)
                }
            } header: {
                Text("Browsers")
            } footer: {
                Text("Later reads the titles and links of your open tabs, and Helium's saved site icons and history (read-only, to see when a page was first opened), never private windows. Nothing leaves your Mac.")
                    .footnote()
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .fixedSize(horizontal: false, vertical: true)   // the window fits its rows, including the approval row and footers
        .frame(width: 520)
        .onAppear { login.refresh() }   // reopening Settings never shows a stale status
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            login.refresh()
        }
    }
}

/// A setting's name and what it does, beside its symbol on a tile of its color, as in System Settings.
struct SettingLabel: View {
    let symbol: String
    let color: Color
    let title: String
    let detail: String

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 26, height: 26)
                .background(color.gradient, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(detail).font(.callout).foregroundStyle(Color.secondaryText)
            }
        }
    }
}

/// What Later is doing right now, under its name at the top of Settings.
struct StatusSummary: View {
    let engine: Engine
    let onboarded: Bool

    var body: some View {
        let watching = Browser.allCases.filter { engine.status[$0] == .watching }
        let problem = Browser.allCases.contains { engine.status[$0]?.isProblem == true }
        HStack(spacing: 6) {
            // Green only while it's watching; waiting for a browser is gray, anything to act on orange.
            Circle()
                .fill(!onboarded || engine.paused || problem ? Color.orange.gradient
                      : watching.isEmpty ? Color.gray.gradient : Color.green.gradient)
                .frame(width: 8, height: 8)
                .accessibilityHidden(true)
            Text(!onboarded ? "Starts watching after setup"
                 : engine.paused ? "Paused"
                 : problem ? "Needs your attention below"
                 : watching.isEmpty ? "Waiting for a browser to open"
                 : "Watching " + BrowserStatus.names(watching))
        }
        .font(.callout)
        .foregroundStyle(Color.secondaryText)
    }
}

struct BrowserRow: View {
    let engine: Engine
    let browser: Browser
    @State private var asking = false
    @State private var denied = false

    var body: some View {
        LabeledContent {
            switch engine.status[browser] ?? .notInstalled {
            case .watching:
                Label { Text("Watching") } icon: { Image(systemName: "checkmark.circle.fill").foregroundStyle(.green) }
            case .needsPermission where denied:
                Button("Open Privacy Settings…") { openAutomationSettings() }
            case .needsPermission:
                HStack(spacing: 10) {
                    Text("Needs permission").foregroundStyle(Color.secondaryText)
                    Button(asking ? "Waiting…" : "Allow…") {
                        asking = true
                        Task {
                            await engine.requestPermission(browser)
                            denied = engine.status[browser] == .needsPermission
                            asking = false
                        }
                    }
                    .disabled(asking)
                }
            case .notRunning:
                Text("Not open right now").foregroundStyle(Color.secondaryText)
            case .notInstalled:
                Text("Not installed").foregroundStyle(Color.secondaryText)
            case .readFailed:
                Label { Text("Can't read tabs") } icon: { WarningSymbol() }
            }
        } label: {
            HStack(spacing: 12) {
                BrowserIcon(browser: browser, size: 26)
                Text(browser.displayName)
            }
        }
    }
}

private extension View {
    /// Form footers align trailing, for buttons; explanations read from the leading edge, quietly.
    func footnote() -> some View {
        font(.callout).foregroundStyle(Color.secondaryText).multilineTextAlignment(.leading)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// The browser's own app icon from this Mac, or a plain globe when it isn't installed.
struct BrowserIcon: View {
    let browser: Browser
    var size: CGFloat = 20

    var body: some View {
        Group {
            if let app = browser.appURL {
                Image(nsImage: NSWorkspace.shared.icon(forFile: app.path)).resizable()
            } else {
                Image(systemName: "globe").font(.system(size: size * 0.8)).foregroundStyle(.secondary)
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
