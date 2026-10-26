# Privacy

Later works only on your Mac. This page says exactly what it reads, what it stores, and what it never does. It describes version 1.2.0, and everything here can be checked in the source.

## No network

Later runs in the macOS App Sandbox **without any network entitlement**, so macOS itself stops it from making connections. There are no accounts, analytics, crash reports, telemetry or update checks. Continuous integration fails the build if a network entitlement ever appears (see `.github/workflows`).

When you open a saved page, Later hands the link to your browser, and the browser loads it like any other link. Later itself sends nothing.

## What Later reads

- **Open tabs, from Helium and Chrome.** The address and title of each tab in normal windows, through the browser's AppleScript interface. macOS asks you to allow this once per browser, from the **Allow…** button in setup or in Settings → Browsers. Tabs in private (incognito) windows are never requested.
- **A browser's `Sessions` folder, as a doorbell.** Later watches it for changes and lists file names so it knows *when* to read the tabs. It never opens or reads those files. Where macOS does not let Later watch a browser's folder (Chrome's is protected), Later reads that browser's tabs when you switch to it instead.
- **Helium's `History` file, for saved pages only.** To show when a page was first opened and how many times you have visited it, Later asks Helium's history about the pages it has already saved. The file is opened read-only, in one short batch, and is never listed or copied. Chrome's history is not read.
- **Helium's `Favicons` file, for saved sites only.** Later fetches the icon for each saved site from Helium's icon cache, read-only, and keeps a small PNG per site in its own cache folder. Chrome's icons are not read.

Later has read-only access to exactly two folders for this: Helium's and Chrome's data folders under `~/Library/Application Support`. It can send Apple Events only to Helium and Chrome.

## What Later stores

Everything is in Later's own sandbox container, where other apps cannot read it:

- **Your list** (`library.json`, with `library.json.bak` and one-time safety copies of older lists): for each saved page its cleaned link, title, kind, browser, when it was saved and last seen, visit counts, and whether you marked it Done. Also your corrections (for example, moving a type of page to another kind).
- **Fingerprints, not links.** To avoid saving a page you deleted or rejected, Later keeps SHA-256 hashes of links, never the links themselves, along with a short list of on-screen times (numbers only).
- **Site icons**, as small PNG files in its cache folder.
- **Your settings**, such as sort order, launch at login and pause.

Links are cleaned before they are saved: tracking parameters are removed, and a link that carries something secret-looking (a token, code, session, key or password) is never saved at all. Links with a username or password in them, local addresses and ports are never saved either.

**Logs.** Later writes a few lines to the macOS unified log: counts and timings, such as how many tabs were read and how long that took. They never contain links, titles or page contents.

## What Later never does

- Connect to the internet.
- Read page contents, cookies, passwords, bookmarks or your browsing history beyond the saved-pages lookups above.
- Read tabs in private windows.
- Send, sync or upload anything.
- Run on a timer: it only wakes when a browser changes or you switch to one.

## Your control

- **Delete** a page, or **Empty Done**, in the app. Undo is available.
- **Pause** watching in Settings (Later also starts at login by default; you can turn that off there), or revoke access in **System Settings → Privacy & Security → Automation**.
- Delete Later's data by removing the app and its container (`~/Library/Containers/com.ryan.later`).

Questions or concerns: see [SECURITY.md](SECURITY.md).
