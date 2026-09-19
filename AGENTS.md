# AGENTS.md

Repository instructions for AI coding agents on StorageMaster.

## Current context policy

- `docs/AIprojectcontext/` is archived, not active project context.
- Use RECALL for durable project memory, decisions, constraints, follow-up findings.
- Retrieve relevant StorageMaster memories from RECALL before non-trivial changes.
- Save new durable decisions/findings to RECALL, not another agent-only context pack.
- Code is source of truth. If docs and code disagree, inspect code, fix current docs, note mismatch in final notes.
- `CLAUDE.md` carries the machine-specific build and runtime detail for this repo. Where the two files disagree on commands or local behavior, `CLAUDE.md` wins; this file owns architecture, safety, and workflow.

## Active documentation

- Human-facing/current docs: `README.md`, `CHANGELOG.md`, `docs/public/`.
- Historical planning and archived agent notes: `archive/project-notes/`.
- Generated output, local AI/tool state, external helper checkouts: ignored archive buckets `archive/generated/`, `archive/local/`, `archive/external/`.

## Project shape

StorageMaster: Windows disk analyzer, cleaner, duplicate finder, scheduler, tray app, CLI/headless tool, updater. Built with .NET 8, WinUI 3, SQLite, optional Rust scanner.

- `src/StorageMaster.Core/`: domain models, interfaces, scanner, cleanup rules, deduplication, Smart Cleaner, update service abstractions/logic. Core stays platform/persistence/UI independent.
- `src/StorageMaster.Storage/`: SQLite schema, migrations, repositories, database connection lifecycle.
- `src/StorageMaster.Platform.Windows/`: Windows implementations for deletion, drives, elevation, known folders, shell interop, snapshots/identity, installer trust, Turbo Scanner hosting.
- `src/StorageMaster.UI/`: WinUI 3 app, MVVM pages/viewmodels, navigation, dialogs, CLI runner, tray/notifications, scheduler/startup services, duplicate previews.
- `turbo-scanner/`: Rust `jwalk`-based native file enumerator used by `TurboFileScanner` when present beside app executable.
- `tests/StorageMaster.Tests/`: xUnit tests for core, storage, platform, dedupe, update, scanner, cleanup, hardening fixes.
- `installer/`: Inno Setup release installer scripts, optional FFmpeg bundling.
- `.github/workflows/`: CI and release automation.

## Architecture invariants

- `StorageMaster.Core` must not reference UI, Storage, or Platform projects.
- Interfaces shared across layers belong in Core.
- Storage implements repository interfaces, owns SQLite details only.
- Platform.Windows implements Windows-specific services only.
- UI composes dependencies through `ServiceBootstrapper`; no business logic beyond binding, commands, dialogs, navigation, presentation state.
- Keep optional Rust scanner behind `IFileScanner`/`TurboFileScanner`; callers must not need to know whether managed or native backend ran.

## Build, format, and test commands

Run from repository root on Windows unless task scoped narrower.

```powershell
dotnet restore StorageMaster.sln
dotnet format StorageMaster.sln --verify-no-changes --no-restore
dotnet build StorageMaster.sln -c Release --no-restore
dotnet test tests/StorageMaster.Tests -c Release

# The solution build does NOT refresh the runnable x64 UI exe. Refresh it with:
dotnet build src/StorageMaster.UI/StorageMaster.UI.csproj -c Release -p:Platform=x64

cd turbo-scanner
cargo fmt --check
cargo test
cargo build --release
```

For WinUI/XAML build issues, use Visual Studio/MSBuild on Windows with Windows application development workload.

Do not claim command passed unless you ran it. If environment cannot run Windows, WinUI, .NET, Rust, installer, or signing steps, state exactly what was not run.

## Safety rules

StorageMaster is deletion tool. Safety over convenience.

- `ICleanupRule.AnalyzeAsync` must be read-only.
- Delete only through deletion services, after explicit user intent or confirmed CLI flags.
- Default to recoverable deletion paths where supported: Recycle Bin for general cleanup, quarantine/recoverable workflows for duplicates when selected.
- Maintain dry-run behavior and audit logging.
- Never recursively delete through junctions/symlinks/reparse points unless code explicitly detects and prevents crossing outside selected target.
- Preserve per-path failure reporting. Partial deletion must not look like full success.

## Documentation maintenance

Changing public behavior, commands, settings, database schema, cleanup rules, duplicate detection, update/release behavior, installer behavior, tests, or supported platforms: update `docs/public/` and applicable user-facing root docs.

Durable agent context goes in RECALL, not in new agent-only doc packs. The existing gitignored working files `PROJECT_STATE.md` (audit + deferrals) and `WORK_STATUS.md` (current pass log) are the one exception — they stay in use; keep them current.

## Agent workflow

1. Retrieve relevant project memory from RECALL when it may affect task.
2. Inspect relevant source and current public docs.
3. Make smallest coherent change satisfying task, preserving architecture boundaries.
4. Add or update tests when changing behavior.
5. Run narrowest useful validation first, then broader validation when available.
6. Save durable decisions/findings back to RECALL when they should persist.
7. In final response, summarize changed files, validation run, validation not run, known risk or follow-up.
