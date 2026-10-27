# How Later is built

A short tour of the code, for anyone reading or building it.

## Shape

A Swift 6 Swift Package for macOS 26 and later, with two targets and two test targets:

| Target | What it holds |
|---|---|
| `LaterCore` | Everything that can be tested without a window: the rules, the list, its storage, and the readers of browser files. No UI, no browser contact. |
| `Later` | The app: an accessory app that shows a Dock icon only while a window is showing, the SwiftUI and AppKit window, the one piece that talks to browsers (`BrowserReader`), and the same executable's background role (`Later --agent`, below). |
| `LaterCoreTests`, `LaterTests` | Swift Testing suites for each. |

`scripts/build-app.sh` bundles the product into `build/Later.app`, compiles the Liquid Glass icon with `actool`, and signs it with the sandbox entitlements in `Resources/Later.entitlements`.

## The flow

```
browser writes session ─▶ Doorbell ─▶ BrowserReader ─▶ Judge ─▶ Library ─▶ LibraryStore
  or you switch to it     (rings)     (Apple Events)   (rules)   (the list)   (JSON file)
```

1. **Doorbell** (`Doorbell.swift`) rings when a browser writes to a `Sessions` folder, using FSEvents and kqueue. It learns *that* something changed and never reads the files. There is no timer. For a browser macOS will not let Later watch, the app reads when you switch to that browser instead (`WatchProbe`).
2. **BrowserReader** sends a handful of Apple Events through the browser's own scripting interface: window ids, each window's mode, and the active tab, links and titles of normal windows. Tabs of private windows are never requested.
3. **Judge** receives each snapshot. It cleans every link (`LinkCleaner`: tracking parameters out, secret-looking links dropped), decides with `PageRules` and `SiteRules` whether the page is worth keeping and what kind it is, and tracks how long each page is on screen or left open. A page is saved after enough time on screen (two minutes at first, then adapted to your habits within one to five minutes), a minute open in the background, or a return visit.
4. **Library** is the list. A page is identified by a *page key*, so one page opened as `www` and bare, or with noise in its query, is one item. It also holds your corrections and powers Undo (`LibraryUndo`).
5. **LibraryStore** writes one JSON file in the app's sandbox container. Every save is complete-or-nothing (temp file, fsync, atomic rename), the previous version is kept as `library.json.bak`, and a file it cannot read is set aside, never overwritten.

Two read-only helpers add detail to saved pages from Helium's own files: `HistoryLookup` (when a page was first opened and how often) and `FaviconLookup` (site icons). Each opens its SQLite file read-only and immutable, asks only about saved pages, and closes it straight away.

## One binary, two roles

Watching must not depend on the window, so the executable has two roles. The **app** is a normal launch: the library window, Settings and onboarding. The **agent** is `Later --agent`, started by launchd through `SMAppService.agent` from the plist in `Contents/Library/LaunchAgents/`. Its `BundleProgram` is the same `Contents/MacOS/Later`, so it has the same code signature, sandbox container and Automation consent. It has no windows, menu or Dock icon, and `KeepAlive` restarts it after a crash or kill but not after a clean exit. The Settings toggle **Keep watching when Later is closed** (`LoginItem`) registers and unregisters it, and moves 1.2's old "Open at login" item over on the first launch of 1.3.

Only one process runs the `Engine` and writes the list. They take turns with an exclusive `flock` on `engine.lock` in the container (`EngineLock`):

- The agent sleeps in a blocking `flock` on a background thread (`AgentTenure`), so the kernel wakes it and no timer is involved. Holding the lock, it runs the engine.
- The app, at launch, tries the lock. If the agent has it, the app posts the Darwin notification `com.ryan.later.handoff` and waits. The agent stops its engine (`Engine.stop` drops its observers and doorbells, writes the list and never writes again), releases the lock and queues for it again. The app then takes the lock, loads the list and runs its own engine.
- When the app quits or dies, the kernel releases the lock, the agent wakes, loads the list from disk and resumes. With no agent registered, the app simply takes the lock at once.
- A development build has its own agent label (`com.ryan.later.dev.agent`) and notification, so it never touches the installed app's.

A handoff does not carry the judge's in-memory timers: a page that was 40 seconds into its minute starts counting again.

The agent never creates an `NSApplication`, so LaunchServices does not see it as a running copy of Later: opening Later while the agent runs starts the window app instead of being sent to the agent (both have the bundle ID `com.ryan.later`). `Later --agent-on` and `--agent-off` change the agent's registration from a shell and exit.

macOS pins the agent to the code signature it was registered with, so an agent registered by one build cannot be started by launchd from a replaced app (`OS_REASON_CODESIGNING`) until it is registered again. When installing over an existing copy, run the new app once (`--agent-off` is enough), then `--agent-on`.

## The rules

`PageRules` judges by site rules first, then by the shape of the link: a page is kept unless it is a search, an inbox, a sign-in provider, a local address, a one-time link or an error page. Pages sort into Shopping, Posts, School, Reading, Videos, Tools or Other. When you correct a type of page, the correction applies to every page of that type, now and later.

## Privacy by construction

The sandbox has no network entitlement, so the app cannot open a connection. The only temporary exceptions are the Apple Events targets (Helium and Chrome) and read-only access to the two browsers' data folders. See [PRIVACY.md](../PRIVACY.md). CI fails any build whose signed app carries a `com.apple.security.network.*` entitlement.

## UI

The library window is one `NSSplitViewController` with three SwiftUI panes (sidebar, list, inspector) inside a unified toolbar, built once and kept when closed so it reopens instantly. `Engine` ties the watching, judging and saving together and publishes status to the window. A debug-only `--sample` mode shows a made-up list and reads no browser, which is how the screenshots in the README are made.

## Testing

`swift test` runs the core and app suites. The browser layer is faked (`FakeBrowsers`, `Harness`) so the judge, library and engine run without any real browser.
