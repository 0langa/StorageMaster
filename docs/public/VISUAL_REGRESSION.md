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
- Accents: at least the default (`aurora`) and one other, on a page that uses accent for state (Dashboard gauges, Drive Health severity), to confirm the live brush swap repaints without a restart.
- Display scale: every page at 200 %, which is where a star-sized panel starved to zero height or an unbounded list stops virtualizing.

## Current automated behavior

The repository contains a skipped xUnit readiness test that records the required desktop prerequisite instead of pretending a headless CI worker can validate WinUI pixels. A future desktop harness should launch `StorageMaster.UI`, navigate to deterministic fixture-backed states, capture screenshots, and compare them against stable baselines.

## Capture procedure (agent- or human-driven)

An interactive Windows session (unlocked desktop) is mandatory; a locked session captures only the lock screen.

1. Build: `dotnet build src/StorageMaster.UI/StorageMaster.UI.csproj -c Release -p:Platform=x64` and `cargo build --release` in `turbo-scanner/`; copy `turbo-scanner\target\release\turbo-scanner.exe` next to `src\StorageMaster.UI\bin\x64\Release\net8.0-windows10.0.19041.0\StorageMaster.UI.exe`. A solution build does **not** refresh that exe — the solution maps the UI project's `Any CPU` configuration to `x86` — so build the csproj with the platform named.
2. Check the exe's modification time before launching it. A stale binary makes every subsequent observation meaningless, and it looks exactly like a real bug.
3. Launch that `StorageMaster.UI.exe` (the repo build, not the installed app).
4. Walk the scenario list above using the `demo\` fixture pack: scan `demo\` (progress + populated results), run duplicate analysis on `demo\03-duplicates` (group list, keeper, preview), quarantine a demo duplicate and restore it (quarantine states + "All quarantined files" section), open each deletion confirmation variant and cancel, visit pages with a fresh state for empty states, and repeat key pages in light and dark theme.
5. Store captures under `tests\visual-baselines\<date>\` as `<scenario>--<theme>.png`, with a `README.md` recording Windows version/build, display scale, theme, accent, and StorageMaster version.

Only quarantine/restore demo-pack files during capture — never real user data.

## Baseline rules

- Baselines must be committed only when generated from deterministic fixture data.
- Baselines must include the Windows version, display scale, app theme, selected accent, and StorageMaster version.
- A pixel diff is not enough by itself; failures must include the screenshot pair and the state name.
