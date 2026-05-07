# StorageMaster — v2.0.1 Release Status

> **Baseline:** v2.0.1 (2026-05-07)
> **Status:** stable release implementation. This replaces the older speculative v2 audit.

## What v2 includes

- Stable release versioning: `StorageMasterVersion=2.0.1` for product/display/update semantics and `StorageMasterAssemblyVersion=2.0.1.0` for Windows assembly/file/manifest versions.
- UI/UX foundation: grouped WinUI shell with Mica fallback, shared style dictionaries and reusable controls, refreshed Dashboard/Drive Health, guided Scan flow, new Scan Workspace, local SettingsCard-style primitives, and dedicated Space Map tile control.
- Drive Health & Storage Sentinel: Windows WMI/storage health snapshots, explicit Unknown/Unsupported fallbacks, SQLite schema v7 persistence, Dashboard warnings, a Drive Health page, tray health alerts, and `--cli health report`.
- Release hardening: stable tags publish as normal GitHub Releases, tags containing `-` publish as prereleases, release tests run in Release configuration, and release artifacts are inspected for installer name, size, and staged runtime prereqs.
- Runtime hardening: the installer checks for .NET Desktop Runtime 8 x64 and still stages Windows App Runtime 1.6 through the smaller framework-dependent deployment path.
- Safety hardening: deep scan starts an elevated CLI worker instead of relaunching the WinUI shell as admin; shell Recycle Bin deletion runs through an STA helper thread; app-created temp recursive deletes are guarded by a direct `%TEMP%` child sentinel.
- v2.0.1 hotfixes: responsive shell/header behavior, top-of-page live scan progress, ETA fallback logic, coalesced Space Map rendering, faster folder-tree expansion, scrollable Duplicates advanced options, Drive Health layout cleanup, and described Dashboard health score.

## Post-release follow-ups

- Finish hardware-lab validation for Drive Health across NVMe, SATA, removable USB, and network drives.
- Run clean-machine install and upgrade smoke tests from v1.9.6 to verify .NET runtime messaging, Windows App Runtime 1.6 installation, Start Menu launch, installer launch, and CLI launch.
- Expand ViewModel/CLI tests around Drive Health, scheduler commands, UI error states, and Scan Workspace once WinUI test-host output copying is solved.
- Keep release signing optional in CI, but verify signed-release behavior when signing secrets are configured.

## Release acceptance

- `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -c Release`, `dotnet test -c Release`, `cargo fmt --check`, `cargo test`, and Rust release build pass.
- The current .NET suite has 144 passing tests, including Drive Health persistence/updater coverage and temp-delete sentinel regression tests.
- WinUI publish and Inno installer build produce `StorageMaster-2.0.1-win-x64-Setup.exe`.
- GitHub release for tag `v2.0.1` is a stable release and includes installer plus `checksums.txt`.
- No new startup crash entry appears in `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log` during install smoke.
