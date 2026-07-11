# Visual baselines

First captured set: 2026-07-05, StorageMaster v2.1.4 + schema-v9/turbo-v2 changes (later committed on `main`).

## Capture metadata

| Field | Value |
|---|---|
| Windows | 11 25H2 (build 26200) |
| Display scale | 200 % (machine default) for most captures; 100 % where noted in the test log |
| Themes | Dark and Light (per-file suffix / test log) |
| App build | `bin\x64\Release\net8.0-windows10.0.19041.0\StorageMaster.UI.exe`, built 2026-07-05 (02:50 set) and 08:49 (post layout fix) |
| Fixture data | `demo\` pack, scan session of `demo\` (152 files), duplicate run with 3 exact groups |

## Where the images are

- `testing/manual-2026-07-05/` — 31 PNGs, named `<test>--<step>--<label>.png`, with the full step-by-step log in `NOTES.md`. Covers: dashboard, duplicates review, quarantine round trip (CLI → restore records → one-click restore ×5), Run/dialog/overflow retests, dark-theme page sweep (Results, Cleanup, Smart Cleaner, Space Map, Drive Health, Settings/FFmpeg, scan progress), light-theme sweep.
- Post-fix captures (member rows + preview pane visible at 200 % scale after the DuplicatesPage layout fix) were taken during the agent session on 2026-07-05 ~08:51.

## Scenario coverage vs. docs/public/VISUAL_REGRESSION.md

| Scenario | Status |
|---|---|
| Duplicate review: groups, keeper, selected duplicate, preview pane | ✅ (post-fix, 200 % scale) |
| Quarantine page: quarantined items, restore, post-restore | ✅ |
| Confirmation dialog: Recycle Bin variant | ✅ (`T4--01--…`) |
| Confirmation dialog: quarantine + permanent variants | ⬜ open (toggle lives in Advanced options; capture both variants) |
| Progress: scan running | ✅ (`T6--08--…`) |
| Progress: duplicate analysis / delete running | ⬜ open (sub-second on demo fixture; needs a larger fixture) |
| Empty states | ✅ (no-analysis, quarantine-hidden) — fresh-profile variant still open |
| Error states: inaccessible file, corrupt media, partial delete failure | ⬜ open |
| Error state: missing FFmpeg | ⚠️ not reproducible on this machine (FFmpeg auto-detected); needs PATH-stripped run |
| Themes light + dark | ✅ (main pages) |
| Tray icon, low-disk toast | ⬜ open (skipped for safety) |

## Baseline rules (unchanged)

Commit images only when generated from deterministic fixture data; always record Windows version, scale, theme, and app version; a pixel diff alone is not a failure — include the screenshot pair and state name.
