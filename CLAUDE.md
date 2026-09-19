# CLAUDE.md — internal working notes for agents

Windows-first storage management app. WinUI 3 UI + .NET 8 + SQLite + Rust turbo-scanner. Free, open source, GitHub-distributed. No scope expansion: no cloud, accounts, telemetry, paid tiers.

## Build & test — exact commands

```bash
dotnet build StorageMaster.sln -c Release          # builds libraries + tests. Does NOT refresh the x64 UI exe!
dotnet build src/StorageMaster.UI/StorageMaster.UI.csproj -c Release -p:Platform=x64   # the ONLY way to refresh the runnable exe
dotnet test tests/StorageMaster.Tests -c Release   # full suite (~232 tests, 1 intentional skip)
dotnet format --verify-no-changes
cd turbo-scanner && cargo fmt --check && cargo clippy --all-targets && cargo test && cargo build --release
```

- Runnable exe: `src\StorageMaster.UI\bin\x64\Release\net8.0-windows10.0.19041.0\StorageMaster.UI.exe`. Copy `turbo-scanner\target\release\turbo-scanner.exe` beside it for turbo scans.
- **Before trusting live-app behavior, verify exe mtime.** Stale exe once burned ~40 min phantom-bug hunting. Cross-check schema: `sqlite3 %LOCALAPPDATA%\StorageMaster\storagemaster.db "SELECT MAX(Version) FROM SchemaVersion;"` (current: 9).
- CLI/headless: `StorageMaster.UI.exe --cli <scan|dedupe|cleanup|health> …` (usage strings in CommandRunner.cs).
- x86 UI exe does not run on this machine (x64-only local dotnet host); x64 is shipped config.

## Hard safety rules

- All deletions flow through `IFileDeleter`. Cleanup rules' `AnalyzeAsync` must stay read-only.
- Prefer quarantine/Recycle Bin/dry-run; permanent deletion gated by policy checks in `CleanupEngine`.
- Every quarantine move must produce restore record (`QuarantinedFiles`; duplicate path via `DuplicateDeletionService`, generic path via `IQuarantineRecorder` in `CleanupEngine`; MemberId NULL + RunId 0 = generic cleanup).
- Filesystem tests use synthetic fixtures / temp dirs only. For live-app testing use `demo\` pack — never real user data.
- Schema changes: new `V<N>Statements` in `DatabaseSchema.cs` + chain entry in `StorageDbContext.MigrateAsync`; each level is one atomic transaction with version stamp. Never edit existing level.

## Architecture boundaries (keep)

Core = logic, no project refs. Storage = SQLite/repos/migrations (WriteLock serialises writes). Platform.Windows = filesystem effects, Recycle Bin, quarantine moves, turbo-scanner host. UI = WinUI presentation + CLI entry.

- Settings: partial mutations go through `ISettingsRepository.UpdateAsync` (atomic load-mutate-save) — never load+`SaveAsync` whole snapshot for one-key change.
- Turbo scanner JSONL contract v2 emits `is_hidden`; C# tolerates v1 binaries (nullable field + per-file attribute fallback).
- UI pages must stay usable at 200 % display scale (this machine's default) — no star-sized panels that can starve to zero height; give lists Min/MaxHeights inside scrollable pages.

## Internal state files

`PROJECT_STATE.md` (audit + deferrals), `WORK_STATUS.md` (current pass log) — both gitignored, keep current. Visual baseline coverage: `tests/visual-baselines/README.md`; capture procedure: `docs/public/VISUAL_REGRESSION.md`.
