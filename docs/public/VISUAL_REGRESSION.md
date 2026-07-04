# WinUI visual regression testing

StorageMaster's visual regression suite is intentionally desktop-gated. The app is a WinUI 3 desktop application, so reliable screenshots require an interactive Windows session with stable display scaling, theme, font rendering, and Windows App Runtime availability.

## Required scenarios

- Duplicate review page: populated group list, selected keeper, selected duplicate, preview pane.
- Quarantine/recovery page: quarantined item, restored item, restore failure.
- Confirmation dialog: recoverable quarantine, Recycle Bin, and permanent delete confirmation.
- Progress states: scan running, duplicate analysis running, delete/quarantine running.
- Empty states: no scan, no duplicates, no quarantine records.
- Error states: inaccessible file, missing FFmpeg, corrupt media, partial delete failure.
- Themes: light and dark when enabled by the host.

## Current automated behavior

The repository contains a skipped xUnit readiness test that records the required desktop prerequisite instead of pretending a headless CI worker can validate WinUI pixels. A future desktop harness should launch `StorageMaster.UI`, navigate to deterministic fixture-backed states, capture screenshots, and compare them against stable baselines.

## Baseline rules

- Baselines must be committed only when generated from deterministic fixture data.
- Baselines must include the Windows version, display scale, app theme, and StorageMaster version.
- A pixel diff is not enough by itself; failures must include the screenshot pair and the state name.
