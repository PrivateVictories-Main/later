import AppKit
import LaterCore
import SwiftUI

struct OnboardingView: View {
    let engine: Engine
    let finish: @MainActor () -> Void
    @State private var allowed: Set<Browser> = []
    @State private var denied: Set<Browser> = []
    @State private var asking: Browser?
    @State private var recheck = 0
    @Environment(\.colorScheme) private var scheme

    private var installed: [Browser] { Browser.allCases.filter(\.isInstalled) }

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 10) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 116, height: 116)
                    .shadow(color: Color.brand.opacity(0.45), radius: 26, y: 12)
                    .accessibilityHidden(true)
                Text("Welcome to Later")
                    .font(.system(size: 32, weight: .bold, design: .rounded))
                    .padding(.top, 8)
                Text("Close tabs freely. Later keeps the ones worth coming back to.")
                    .font(.title3)
                    .foregroundStyle(Color.secondaryText)
                    .multilineTextAlignment(.center)
            }
            VStack(alignment: .leading, spacing: 16) {
                point("bookmark.fill", .brand, "Saves what you linger on",
                      "A product, a post or an assignment you spend a while on is saved, sorted by kind.")
                point("hand.raised.fill", .orange, "Never touches your tabs",
                      "It never opens or closes a tab on its own. Close any tab, anytime.")
                point("lock.fill", .green, "Stays on this Mac",
                      "No internet access at all, so your list never leaves this Mac.")
            }
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .leading)
            .glassEffect(.regular, in: RoundedRectangle(cornerRadius: 22, style: .continuous))
            .padding(.top, 30)
            VStack(alignment: .leading, spacing: 10) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Let Later read your open tabs").font(.headline)
                    Text("macOS asks once per browser. Later reads the titles and links of your open tabs, and Helium's saved site icons, never private windows.")
                        .font(.callout)
                        .foregroundStyle(Color.secondaryText)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, 4)
                VStack(spacing: 0) {
                    ForEach(Array(installed.enumerated()), id: \.element) { index, browser in
                        if index > 0 { Divider().padding(.leading, 56) }
                        row(browser)
                    }
                    if installed.isEmpty {
                        Text("Later works with Helium and Chrome. Install one, then open Later again.")
                            .foregroundStyle(Color.secondaryText)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(14)
                    }
                }
                .glassEffect(.regular, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
                .id(recheck)
            }
            .padding(.top, 26)
            // The one way on: full width, the default button (Return), in Later's color while the window is key.
            VStack(spacing: 10) {
                Button { finish() } label: {
                    Text("Start Using Later").fontWeight(.semibold).frame(maxWidth: .infinity)
                }
                .buttonStyle(.glassProminent)
                .controlSize(.extraLarge)
                .tint(.brand)
                .keyboardShortcut(.defaultAction)
                Text("You can change this anytime in Settings.")
                    .font(.callout)
                    .foregroundStyle(Color.secondaryText)
            }
            .padding(.top, 28)
        }
        .padding(.horizontal, 40)
        .padding(.top, 44)
        .padding(.bottom, 30)
        .frame(width: 600)
        .background(alignment: .top) {
            // Later's own colors behind the icon: its indigo and the violet it shades into, fading into the window.
            ZStack {
                RadialGradient(colors: [Color.brand.opacity(scheme == .dark ? 0.45 : 0.28), .clear],
                               center: UnitPoint(x: 0.35, y: 0), startRadius: 0, endRadius: 360)
                RadialGradient(colors: [Color.purple.opacity(scheme == .dark ? 0.26 : 0.16), .clear],
                               center: UnitPoint(x: 0.8, y: 0.05), startRadius: 0, endRadius: 260)
            }
            .frame(height: 420)
            .ignoresSafeArea()
            .allowsHitTesting(false)
        }
    }

    @ViewBuilder private func row(_ browser: Browser) -> some View {
        HStack(spacing: 14) {
            BrowserIcon(browser: browser, size: 30)
            Text(browser.displayName).font(.body.weight(.medium))
            Spacer()
            if allowed.contains(browser) {
                Label { Text("Allowed") } icon: { Image(systemName: "checkmark.circle.fill").foregroundStyle(.green) }
                    .fontWeight(.medium)
            } else if BrowserReader.mainInstance(of: browser) == nil {
                Text("Open \(browser.displayName) first").foregroundStyle(Color.secondaryText)
                Button("Check Again") { recheck += 1 }.buttonStyle(.glass)
            } else if denied.contains(browser) {
                Button("Open Privacy Settings…") { openAutomationSettings() }.buttonStyle(.glass)
            } else {
                Button(asking == browser ? "Waiting…" : "Allow…") { ask(browser) }
                    .buttonStyle(.glassProminent)
                    .tint(.brand)
                    .disabled(asking != nil)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
    }

    private func ask(_ browser: Browser) {
        asking = browser
        Task {
            switch await BrowserReader.requestPermission(browser) {
            case nil: allowed.insert(browser)
            case .needsPermission: denied.insert(browser)
            default: break
            }
            asking = nil
        }
    }

    private func point(_ symbol: String, _ color: Color, _ title: String, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: symbol)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 34, height: 34)
                .background(color.gradient, in: Circle())
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(text).foregroundStyle(Color.secondaryText).fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}
