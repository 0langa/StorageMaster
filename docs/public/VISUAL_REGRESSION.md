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

## Capturing without a screen — `--capture-screens`

```
StorageMaster.UI.exe --capture-screens <dir> [--language de-DE] [--theme dark|light]
                     [--pages Results,Settings] [--settle 2000]
                     [--width 1440 --height 900]
```

Renders each page to `<page>--<theme>--<language>.png` with the window parked
off-screen. This is the preferred way to review the interface: it does not need
the foreground window, so it cannot be derailed by whatever else is using the
desktop, and it works while the machine is in use.

Run one process per language and theme rather than switching in place. The
localization markup extension resolves when a page is parsed, so a language
applied mid-run would capture a half-translated tree that no user could reach.

```bash
for L in de-DE es-ES en-US; do for T in dark light; do
  StorageMaster.UI.exe --capture-screens shots --language "$L" --theme "$T"
done; done
```

### Checking display scale

`--width`/`--height` set the window's **logical** size, and that — not DPI — is what
reproduces a scaling problem. Display scale does not change layout, which is in
logical units; it changes how many logical units the screen has. This machine's
2880x1920 panel at 200 % leaves an app about **1440x900**, so:

```bash
StorageMaster.UI.exe --capture-screens shots --language de-DE --width 1440 --height 900
```

Without it the capture window is larger than a user's, and it quietly hides the
clipping being looked for. The size is appended to the file name so the two sets
cannot be confused.

Point `STORAGEMASTER_DATA_DIR` at a copy of a populated database to capture pages
with real content; against an empty profile every page shows its empty state.

### Scenarios beyond an idle page

`--scenarios` reaches the states a plain page capture cannot, because they only
exist while something is happening:

```bash
StorageMaster.UI.exe --capture-screens shots --language de-DE     --scenarios dialogs,accents,progress --scan-path "C:\Program Files"
```

| Scenario | What it captures | How |
|---|---|---|
| `dialogs` | Every safety confirmation | Rebuilt from the same resource keys the real call sites use, then rendered. The dialog itself is rendered, not the window — a `ContentDialog` lives in the popup layer, so rendering the window gives the page with no dialog on it. |
| `accents` | Dashboard and Drive Health per accent | Applies each accent in place, which is what the app does when a user picks one, so it also proves the live swap repaints. The starting accent is restored afterwards. |
| `progress` | A scan running, and the same scan complete | Starts a real scan and waits for it to actually be running. |

The dialogs are declared in `ScenarioCatalogue`, because a real confirmation only
appears while someone is deleting something and a capture run must never be the
thing that starts a deletion. `SafetyDialogCoverageTests` fails if a confirmation
exists in a view model but not in that list, so a new one cannot go unreviewed.

`progress` needs a target big enough to still be scanning when the capture is
taken, and one that is not excluded by the scan scope — `C:\Windows` finishes
instantly with nothing because system folders are skipped by default. When no
running state can be captured the run says so and writes no file, rather than
writing a "running" capture of a finished page.

What it still cannot reach: it renders the XAML tree, not the desktop. Tray
notifications, native file pickers and Windows dialogs need an interactive
session and the procedure below. **Error states** — inaccessible file, missing
FFmpeg, corrupt media, partial delete failure — are not yet scriptable either:
each needs a fixture that fails in a specific way, which is the next piece of
work on this harness.

## Current automated behavior

The repository contains a skipped xUnit readiness test that records the desktop
prerequisite for full baseline comparison instead of pretending a headless CI
worker can validate WinUI pixels. `--capture-screens` produces the images; a
future step should diff them against committed baselines.

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
