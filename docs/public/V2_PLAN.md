# StorageMaster — v2.0.0-prerelease Status And Release Plan

> **Baseline:** v2.0.0-prerelease (2026-05-07)
> **Status:** prerelease branch implementation. This replaces the older speculative v2 audit.

## What v2 prerelease includes

- Prerelease-safe versioning: `StorageMasterVersion=2.0.0-prerelease` for product/display/update semantics and `StorageMasterAssemblyVersion=2.0.0.0` for Windows assembly/file/manifest versions.
- Drive Health & Storage Sentinel: Windows WMI/storage health snapshots, explicit Unknown/Unsupported fallbacks, SQLite schema v7 persistence, Dashboard warnings, a Drive Health page, tray health alerts, and `--cli health report`.
- Release hardening: tags containing `-` publish as GitHub prereleases, release tests run in Release configuration, and release artifacts are inspected for installer name, size, and staged runtime prereqs.
- Runtime hardening: the installer checks for .NET Desktop Runtime 8 x64 and still stages Windows App Runtime 1.6 through the smaller framework-dependent deployment path.
- Safety hardening: deep scan starts an elevated CLI worker instead of relaunching the WinUI shell as admin; shell Recycle Bin deletion runs through an STA helper thread; app-created temp recursive deletes are guarded by a direct `%TEMP%` child sentinel.

## Remaining prerelease blockers

- Finish hardware-lab validation for Drive Health across NVMe, SATA, removable USB, and network drives.
- Run clean-machine install and upgrade smoke tests from v1.9.6 to verify .NET runtime messaging, Windows App Runtime 1.6 installation, Start Menu launch, installer launch, and CLI launch.
- Expand ViewModel/CLI tests around Drive Health, scheduler commands, and UI error states.
- Keep release signing optional in CI, but verify signed-release behavior when signing secrets are configured.

## Release acceptance

- `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -c Release`, `dotnet test -c Release`, `cargo fmt --check`, `cargo test`, and Rust release build pass.
- The current .NET suite has 144 passing tests, including Drive Health persistence/updater prerelease coverage and temp-delete sentinel regression tests.
- WinUI publish and Inno installer build produce `StorageMaster-2.0.0-prerelease-win-x64-Setup.exe`.
- GitHub release for tag `v2.0.0-prerelease` is marked prerelease and includes installer plus `checksums.txt`.
- No new startup crash entry appears in `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log` during install smoke.
