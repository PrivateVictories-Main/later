# Shared test vectors

Table-driven cases that both the Swift suite (`Tests/LaterCoreTests/Vectors.swift`) and the Windows suite load, so both platforms judge pages identically. The macOS results are the reference: the `characterized on macOS` rows record what Foundation's URL parsing does, and the Windows URL code must reproduce it.

## Files

Every file has a `counts` object. Both loaders check it against the real array lengths, so a truncated file fails loudly.

- `link-clean.json`: `{ "counts": {"clean","refuse","distinct","storefront"}, "clean": [{"raw","expected","note"?}], "refuse": [raw], "distinct": [[rawA, rawB]], "storefront": [{"host","name","is"}] }`
  - `clean`: `LinkCleaner.clean(raw)` gives exactly `expected`.
  - `refuse`: `clean(raw)` gives nothing (the page must never be saved).
  - `distinct`: both links clean, and the cleaned links differ.
  - `storefront`: `isStorefront(host, name:)` equals `is`.
- `page-key.json`: `{ "counts": {"same","different","keys"}, "same": [[a,b]], "different": [[a,b]], "keys": [{"raw","key"}] }`
  - `same` / `different`: `pageKey(clean(a)) == / != pageKey(clean(b))`.
  - `keys`: the exact key text for `clean(raw)`.
- `page-rules.json` and `site-rules.json`: `{ "counts": {"cases"}, "cases": [JudgeCase] }`
  - `JudgeCase = {"url","title", "specific"?, "kind"?, "pageType"?, "pageTypeSuffix"?, "notPageTypeSuffix"?, "feeds"?, "corrections"?, "note"?}`
  - `specific`: bool. `kind`: `shopping|posts|school|reading|videos|tools|other`. `pageType`: the exact page type. `feeds`: judge with feed homes saved. `corrections`: `{"<pageType>": {"isSpecific"?: bool, "kind"?: kind}}`.
  - `url` must clean to something; a link the cleaner must refuse belongs in `link-clean.json`.

## Rules

- A case asserts only the keys it has.
- Every URL here is fictional or a well-known public site. Never add a real personal link.
- To add a case: edit the JSON, update `counts`, run `swift test`, push. The Windows suite loads the same file.
