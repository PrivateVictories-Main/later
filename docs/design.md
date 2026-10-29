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
- The app, at launch, tries the lock. If the agent has it, the app posts the Darwin notification `com.ryan.later.handoff` and waits, posting it again every 0.25 s (a request that reaches the agent just before it knows it holds the lock is ignored). The agent stops its engine (`Engine.stop` drops its observers and doorbells, writes the list and never writes again), releases the lock and queues for it again. The app then takes the lock, loads the list and runs its own engine. If the agent has not let go after 5 s, the app never runs an engine without the lock: it shows the list as it is on disk, read-only, with a notice, and does not watch or save. Editing actions are disabled while it is read-only, and the app asks again (briefly, with the same nudge) each time its window is shown or activated; when the lock is free it loads the list properly and takes over (`WindowHandoff`, `Engine.becomeWritable`). No timer is involved.
- The agent never recovers a damaged list. If `library.json` cannot be read (or is missing but has a backup), it touches nothing and stays idle until the app opens, loads the list and tells the user what was recovered.
- The agent has no window, so trouble goes into `agent-status.json` beside the list (`AgentStatus`: a state, `needsRecovery`, `cantOpenStorage` or `cantSave`, and a count; no links or titles). The app reads and removes it at launch and shows it in the notice bar; the agent removes it itself when the trouble is over. No user notification is used, since that would prompt.
- If the app takes the lock at once while watching in the background is wanted, no agent was running (for example after an update that was never followed by opening Later, then a reboot). The app logs it, registers the agent again and, if macOS still doesn't have it enabled, Settings says the helper couldn't be restarted. Measured result of the update-then-reboot path itself: not run.
- When the app quits or dies, the kernel releases the lock, the agent wakes, loads the list from disk and resumes. With no agent registered, the app simply takes the lock at once.
- A development build has its own agent label (`com.ryan.later.dev.agent`) and notification, so it never touches the installed app's.

A handoff does not carry the judge's in-memory timers: a page that was 40 seconds into its minute starts counting again. Time asleep or locked is neither reading time nor time left open: the engine tells the judge when the Mac sleeps, its display sleeps or it locks, and when it comes back (`Judge.setAway`).

The doorbell (`Doorbell`) uses kqueue watches only: each profile's `Sessions` folder and its newest session files, the data folder (a new profile) and a profile that has no `Sessions` folder yet. Nothing else in the browser's data folder wakes Later.

The agent's main thread runs a plain run loop (`RunLoop.main.run()`): `dispatchMain()` was measured not to deliver NSWorkspace events (app switches, launches, quits, sleep), which the Chrome reads depend on. It also logs one count-only line per wake reason (`wake appSwitch`, `wake sleep`, `wake lock`). The agent never creates an `NSApplication` or an `NSImage` (creating one registers the process with LaunchServices, measured; `SiteIcons.Icon` makes its image only when a window draws it), so LaunchServices does not see it as a running copy of Later: opening Later while the agent runs starts the window app instead of failing with -600 (both have the bundle ID `com.ryan.later`). A process that is registered can't be undone and, without an `NSApplication`, can't answer the open either, so `scripts/measure.sh checkin` checks the running agent. `Later --agent-on` and `--agent-off` change the agent's registration from a shell and exit.

launchd pins the agent to the code hash (cdhash) of the program it was registered with, so after an update the old registration cannot start the new app (`OS_REASON_CODESIGNING`). A launcher with a fixed hash cannot work around it: the program must be sandboxed because the app is, and a sandboxed program cannot start the sandboxed app (the second sandbox set-up aborts). launchd refreshes the pin only after it has seen a failed start, about ten seconds later, and registering before then keeps the old pin. So `LoginItem` records the hash of the build that registered the agent. A first registration (nothing registered before) pins the running build already, so it records itself and does nothing more. A different build, finding an existing item, registers it again at once and once more 15 s later (the same item, which should not raise a banner; no banner was observed, but none was looked for on a real update) and records itself; if Later quits in between, the next launch repeats it. The cost of an update is one agent restart.

## The rules

`PageRules` judges by site rules first, then by the shape of the link: a page is kept unless it is a search, an inbox, a sign-in provider, a local address, a one-time link or an error page. Pages sort into Shopping, Posts, School, Reading, Videos, Tools or Other. When you correct a type of page, the correction applies to every page of that type, now and later.

## Privacy by construction

The sandbox has no network entitlement, so the app cannot open a connection. The only temporary exceptions are the Apple Events targets (Helium and Chrome) and read-only access to the two browsers' data folders. See [PRIVACY.md](../PRIVACY.md). CI fails any build whose signed app carries a `com.apple.security.network.*` entitlement.

## UI

The library window is one `NSSplitViewController` with three SwiftUI panes (sidebar, list, inspector) inside a unified toolbar, built once and kept when closed so it reopens instantly. `Engine` ties the watching, judging and saving together and publishes status to the window. A debug-only `--sample` mode shows a made-up list and reads no browser, which is how the screenshots in the README are made.

## Testing

`swift test` runs the core and app suites. The browser layer is faked (`FakeBrowsers`, `Harness`) so the judge, library and engine run without any real browser.
