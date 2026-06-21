# AGENTS.md

Repository-level instructions for AI coding agents working on StorageMaster.

## Current context policy

- Do not treat `docs/AIprojectcontext/` as active project context. That folder has been archived.
- Use RECALL for durable project-specific memory, decisions, constraints, and follow-up findings.
- Before making non-trivial changes, retrieve relevant StorageMaster memories from RECALL when available.
- Save new durable project decisions or findings to RECALL instead of creating another agent-only context pack.
- Code remains the source of truth. If documentation and code disagree, inspect the code, fix the current docs, and mention the mismatch in final notes.

## Active documentation

- Human-facing/current docs live in `README.md`, `CHANGELOG.md`, and `docs/public/`.
- Historical planning and archived agent notes live under `archive/project-notes/`.
- Generated output, local AI/tool state, and external helper checkouts belong under ignored archive buckets such as `archive/generated/`, `archive/local/`, and `archive/external/`.

## Project shape

StorageMaster is a Windows disk analyzer, cleaner, duplicate finder, scheduler, tray app, CLI/headless tool, and updater built with .NET 8, WinUI 3, SQLite, and an optional Rust scanner.

- `src/StorageMaster.Core/`: domain models, interfaces, scanner, cleanup rules, deduplication, Smart Cleaner, update service abstractions/logic. Core must remain platform/persistence/UI independent.
- `src/StorageMaster.Storage/`: SQLite schema, migrations, repositories, and database connection lifecycle.
- `src/StorageMaster.Platform.Windows/`: Windows implementations for deletion, drives, elevation, known folders, shell interop, snapshots/identity, installer trust, and Turbo Scanner hosting.
- `src/StorageMaster.UI/`: WinUI 3 app, MVVM pages/viewmodels, navigation, dialogs, CLI runner, tray/notifications, scheduler/startup services, duplicate previews.
- `turbo-scanner/`: Rust `jwalk`-based native file enumerator used by `TurboFileScanner` when present beside the app executable.
- `tests/StorageMaster.Tests/`: xUnit tests for core, storage, platform, dedupe, update, scanner, cleanup, and hardening fixes.
- `installer/`: Inno Setup release installer scripts and optional FFmpeg bundling support.
- `.github/workflows/`: CI and release automation.

## Architecture invariants

- `StorageMaster.Core` must not reference UI, Storage, or Platform projects.
- Interfaces shared across layers belong in Core.
- Storage implements repository interfaces and owns SQLite details only.
- Platform.Windows implements Windows-specific services only.
- UI composes dependencies through `ServiceBootstrapper` and should not contain business logic beyond binding, commands, dialogs, navigation, and presentation state.
- Keep the optional Rust scanner behind `IFileScanner`/`TurboFileScanner`; callers should not need to know whether a managed or native scan backend ran.

## Build, format, and test commands

Use these commands from the repository root on Windows unless a task is scoped more narrowly.

```powershell
dotnet restore StorageMaster.sln
dotnet format StorageMaster.sln --verify-no-changes --no-restore
dotnet build StorageMaster.sln -c Release --no-restore
dotnet test StorageMaster.sln -c Release --no-build

cd turbo-scanner
cargo fmt --check
cargo test
cargo build --release
```

For WinUI/XAML build issues, use Visual Studio/MSBuild on Windows with the Windows application development workload.

Do not claim a command passed unless you actually ran it. If the environment cannot run Windows, WinUI, .NET, Rust, installer, or signing steps, state exactly what was not run.

## Safety rules

StorageMaster is a deletion tool. Safety wins over convenience.

- `ICleanupRule.AnalyzeAsync` must be read-only.
- Deletion must happen only through deletion services after explicit user intent or confirmed CLI flags.
- Default to recoverable deletion paths where supported: Recycle Bin for general cleanup and quarantine/recoverable workflows for duplicates when selected.
- Maintain dry-run behavior and audit logging.
- Never recursively delete through junctions/symlinks/reparse points unless the code explicitly detects and prevents crossing outside the selected target.
- Preserve per-path failure reporting. A partial deletion must not look like full success.

## Documentation maintenance

When changing public behavior, commands, settings, database schema, cleanup rules, duplicate detection, update/release behavior, installer behavior, tests, or supported platforms, update `docs/public/` and any user-facing root docs that apply.

Do not create new agent-only documentation packs. Put durable agent context in RECALL.

## Agent workflow

1. Retrieve relevant project memory from RECALL when it may affect the task.
2. Inspect the relevant source and current public docs.
3. Make the smallest coherent change that satisfies the task while preserving architecture boundaries.
4. Add or update tests when changing behavior.
5. Run the narrowest useful validation first, then broader validation when available.
6. Save durable project decisions/findings back to RECALL when they should persist.
7. In the final response, summarize changed files, validation run, validation not run, and any known risk or follow-up.
