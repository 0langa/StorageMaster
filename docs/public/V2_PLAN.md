# StorageMaster — Historical v2.1.4 Repository Status

> Snapshot retained for release history. It is not the current implementation plan; see `ROADMAP.md` and `RELIABILITY_AUDIT_2026-08-18.md`.

> **Baseline:** version metadata 2.1.4
> **Status at snapshot:** repository state recorded for v2.1.4 plus the hardening known then. It must not be read as a claim about the current working tree or current release gates.

## What v2.1.x includes

- **Settings page fix:** `ContentTemplateSelector` was only evaluated once because `Content` was always the same ViewModel reference. Code-behind now calls `SelectTemplate` manually on `SelectedCategory` / `IsEditorOpen` change, so every category tile shows its correct settings template.
- **Settings duplicate removed:** Scan history retention slider was in both the Scanning and Results & History categories. Removed from Scanning (it belongs in Results & History).
- **Settings missing binding added:** `ClearEntireDownloads` was defined in the ViewModel and persisted to settings but had no UI control. Added toggle to the Cleanup settings category.
- **DuplicatesViewModel event subscription fix:** `LoadGroupsPageAsync` subscribed `PropertyChanged` handlers via anonymous lambdas on every page load without unsubscribing. Replaced with a named handler and explicit unsubscription before each page rebuild.
- **StorageDbContext schema version logging:** Silent `catch { return 0; }` on the schema version query now logs a Warning with the exception before returning, making DB corruption visible in logs.
- **TurboFileScanner JSON parse logging:** Bare `catch { continue; }` on JSONL deserialization now logs a Debug entry with the malformed line, aiding diagnosis of Rust/C# output contract mismatches.
- **ROADMAP.md rewrite:** Replaced stale v1.x milestone planning with a clean v2.x roadmap covering structured logging, ARM64, Drive Health hardware-lab validation, accessibility, and future phases.
- **GitHub infrastructure:** Added bug report and feature request issue templates, and a pull request template.

## What v2.0.x included

- Stable release versioning: `StorageMasterVersion` for product/display/update semantics, `StorageMasterAssemblyVersion` for Windows assembly/file/manifest versions.
- UI/UX foundation: grouped WinUI shell with Mica fallback, shared style dictionaries and reusable controls, refreshed Dashboard/Drive Health, guided Scan flow, new Scan Workspace, local SettingsCard-style primitives, and dedicated Space Map tile control.
- Drive Health & Storage Sentinel: Windows WMI/storage health snapshots, explicit Unknown/Unsupported fallbacks, SQLite schema v7 persistence, Dashboard warnings, a Drive Health page, tray health alerts, and `--cli health report`.
- Release hardening: stable tags publish as normal GitHub Releases, tags containing `-` publish as prereleases, release tests run in Release configuration, and release artifacts are inspected for installer name, size, and staged runtime prereqs.
- Runtime hardening: the installer checks for .NET Desktop Runtime 8 x64 and still stages Windows App Runtime 1.6 through the smaller framework-dependent deployment path.
- Safety hardening: deep scan starts an elevated CLI worker instead of relaunching the WinUI shell as admin; shell Recycle Bin deletion runs through an STA helper thread; app-created temp recursive deletes are guarded by a direct `%TEMP%` child sentinel.
- v2.0.1 hotfixes: responsive shell/header behavior, top-of-page live scan progress, ETA fallback logic, coalesced Space Map rendering, faster folder-tree expansion, scrollable Duplicates advanced options, Drive Health layout cleanup, and described Dashboard health score.

## Follow-ups recorded at the v2.1.4 snapshot

- Finish hardware-lab validation for Drive Health across NVMe, SATA, removable USB, and network drives.
- Run clean-machine install and upgrade smoke tests from v1.9.6 to verify .NET runtime messaging, Windows App Runtime 1.6 installation, Start Menu launch, installer launch, and CLI launch.
- Extract ViewModels, CLI orchestration, and scheduler logic from the WinUI runtime assembly before restoring their direct tests; loading `StorageMaster.UI.dll` in the current test host requires WinRT bootstrap initialization and previously prevented CI completion.
- Keep release signing optional in CI, but verify signed-release behavior when signing secrets are configured.

## Release acceptance recorded at the snapshot

- The snapshot recorded `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -c Release`, `dotnet test -c Release`, `cargo fmt --check`, `cargo test`, and the Rust release build as passing.
- At this snapshot, 196 .NET tests and three Rust CLI/JSONL contract tests were discovered.
- The configured WinUI publish and Inno installer output is `StorageMaster-2.1.4-win-x64-Setup.exe`; installer construction still requires an explicit release build.
- GitHub release publication, signing, and clean-machine installation remain separate release checks and are not implied by local unit-test success.
- No new startup crash entry appears in `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log` during install smoke.
