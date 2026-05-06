# AGENTS.md

Repository-level instructions for AI coding agents working on StorageMaster. This file applies to the entire repository unless a deeper `AGENTS.md` overrides it.

## First-read context

Before making changes, read the agent-focused docs in `docs/AIprojectcontext/` in this order:

1. `docs/AIprojectcontext/ARCHITECTURE.md`
2. `docs/AIprojectcontext/CODEMAP.md`
3. `docs/AIprojectcontext/DOCUMENTATION.md`
4. `docs/AIprojectcontext/ROADMAP.md`
5. `docs/AIprojectcontext/V2_PLAN.md`

These files are the fast, dense context pack for agents. `docs/public/` contains the longer human-facing documentation. Code is always the source of truth; if docs and code disagree, inspect code, fix the docs, and mention the mismatch in your final notes.

## Documentation maintenance rule

Keep `docs/AIprojectcontext/` and `docs/public/` up to date at all times.

When changing architecture, public behavior, commands, settings, database schema, cleanup rules, duplicate detection, update/release behavior, installer behavior, tests, or supported platforms, update both documentation sets in the same change:

- `docs/AIprojectcontext/`: concise, technical, low-token, optimized for coding agents.
- `docs/public/`: fuller explanations for human readers.

Do not update only one docs tree unless the change is explicitly documentation-only and only applies to that audience. Do not let copied claims drift from implementation. Prefer precise current-state wording over roadmap-style promises.

## Project shape

StorageMaster is a Windows disk analyzer, cleaner, duplicate finder, scheduler, tray app, CLI/headless tool, and updater built with .NET 8, WinUI 3, SQLite, and an optional Rust scanner.

Main components:

- `src/StorageMaster.Core/`: domain models, interfaces, scanner, cleanup rules, deduplication, Smart Cleaner, update service abstractions/logic. Core must remain platform/persistence/UI independent.
- `src/StorageMaster.Storage/`: SQLite schema, migrations, repositories, and database connection lifecycle.
- `src/StorageMaster.Platform.Windows/`: Windows implementations for deletion, drives, elevation, known folders, shell interop, snapshots/identity, installer trust, and Turbo Scanner hosting.
- `src/StorageMaster.UI/`: WinUI 3 app, MVVM pages/viewmodels, navigation, dialogs, CLI runner, tray/notifications, scheduler/startup services, duplicate previews.
- `turbo-scanner/`: Rust `jwalk`-based native file enumerator used by `TurboFileScanner` when present beside the app executable.
- `tests/StorageMaster.Tests/`: xUnit tests for core, storage, platform, dedupe, update, scanner, cleanup, and hardening fixes.
- `installer/`: Inno Setup release installer scripts and optional FFmpeg bundling support.
- `.github/workflows/`: CI and release automation.

## Architecture invariants

Preserve these boundaries:

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

For WinUI/XAML build issues, use Visual Studio/MSBuild on Windows with the Windows application development workload. The UI project targets `net8.0-windows10.0.19041.0` with minimum Windows target `10.0.17763.0` and unpackaged deployment (`WindowsPackageType=None`).

Do not claim a command passed unless you actually ran it. If the environment cannot run Windows, WinUI, .NET, Rust, installer, or signing steps, state exactly what was not run.

## Coding conventions

- Keep nullable reference types clean; do not introduce nullable warnings.
- Prefer small, focused services behind existing interfaces over large UI/viewmodel patches.
- Use `async`/`await` end-to-end. Never block the UI thread with `.Wait()`, `.Result`, synchronous large I/O, hashing, image/video processing, or database work.
- Honor `CancellationToken` in scans, cleanup, dedupe, updater downloads, CLI/headless jobs, and long loops.
- Use `ConfigureAwait(false)` in non-UI library code where appropriate; marshal UI updates through WinUI dispatcher mechanisms.
- Keep XAML pages MVVM-oriented. Code-behind may wire WinUI-specific controls, dialogs, navigation events, and visual behavior, but domain decisions belong in services/viewmodels.
- Prefer `{x:Bind}` for WinUI page bindings unless runtime `Binding` is required.
- Keep accessibility intact: names/help text for interactive controls, keyboard navigation, focus behavior, high contrast, and text scaling.
- Keep UI copy explicit. Settings, destructive actions, cleanup selections, duplicate methods, and filters should explain impact and risk.

## Scanner rules

- Managed scanning uses bounded parallel traversal, batched repository writes, progress reporting, cancellation, and post-scan folder aggregation.
- Do not remove backpressure or batching without replacing it with an equal or better scalability mechanism.
- Preserve scan session status correctness: cancelled/failed scans must not be reported as completed.
- Treat symlinks, junctions, and reparse points as data-loss risks. Follow or delete through them only when explicitly intended and guarded.
- Keep Turbo Scanner fallback transparent. If `turbo-scanner.exe` is unavailable, managed scanning should still work.

## Cleanup and deletion safety

StorageMaster is a deletion tool. Safety wins over convenience.

- `ICleanupRule.AnalyzeAsync` must be read-only. It must never delete, mutate, or repair files.
- Deletion must happen only through deletion services after explicit user intent or confirmed CLI flags.
- Default to recoverable deletion paths where supported: Recycle Bin for general cleanup and quarantine/recoverable workflows for duplicates when selected.
- Maintain dry-run behavior and audit logging.
- Never recursively delete through junctions/symlinks/reparse points unless the code explicitly detects and prevents crossing outside the selected target.
- Avoid broad path matching that could include system roots, user profile roots, or application install directories accidentally.
- Preserve per-path failure reporting. A partial deletion must not look like full success.

## Deduplication rules

- Keep dedupe pluggable through `IDuplicateDetectionStrategy` and related repository/service interfaces.
- Exact SHA-256, normalized text, image pHash, and optional video pHash have different costs and false-positive risks; do not merge their semantics.
- Avoid hashing or decoding entire large files unnecessarily. Use candidate filtering, size thresholds, extension filters, cancellation, and progress reporting.
- Fuzzy duplicate results require review-oriented UX and safe deletion defaults. Do not auto-delete fuzzy matches.
- Keep keeper policy, quarantine, restore, audit, and failed-path handling coherent when changing duplicate deletion.

## Storage and migrations

- SQLite schema lives in `StorageMaster.Storage/Schema/DatabaseSchema.cs`; repository behavior lives in `StorageMaster.Storage/Repositories/`.
- Add schema changes through explicit migrations and increment the schema version. Do not patch live tables ad hoc from UI or services.
- Keep migrations atomic and idempotent for existing databases.
- Preserve WAL-friendly read/write behavior and repository write locking where required.
- Use parameters for all SQL. Never concatenate user paths, filters, or CLI values into SQL.
- Add or adjust indexes when adding query patterns that affect scan results, cleanup, dedupe, or history at scale.

## CLI, scheduler, tray, and updater

- CLI/headless behavior is part of the product surface. Keep arguments, exit codes, JSON/CSV output, and `--confirm` safety semantics stable unless docs and tests are updated.
- Scheduled tasks must invoke safe headless commands and must not bypass confirmation semantics for destructive operations.
- Tray/background behavior must not hide active destructive work or swallow errors silently.
- The updater targets GitHub Releases. Do not weaken digest/signature/trust checks, installer validation, or failure classification.

## Release and installer rules

- Keep release artifacts aligned: app version, installer version, README/docs, changelog, release workflow, and updater expectations.
- Optional code signing must remain optional in CI, but signed release verification must stay strict when signing secrets are present.
- Optional FFmpeg bundling belongs under installer/release flow; the app must still handle missing FFmpeg gracefully.
- Do not commit secrets, PFX files, signing passwords, private keys, generated installers, build output, local databases, or user logs.

## Testing expectations

Add or update tests when changing:

- scanner traversal, progress, cancellation, aggregation, or Turbo Scanner hosting;
- cleanup rule analysis or deletion behavior;
- duplicate candidate selection, signatures, grouping, keeper policy, deletion, quarantine, or restore;
- repository SQL, schema, migrations, settings, or history retention;
- CLI commands, exit codes, scheduler jobs, update checks, or installer trust behavior;
- ViewModel state transitions that previously caused freezes, stale state, or unsafe UX.

Prefer focused tests close to the changed subsystem. For bugs, add regression tests that fail on the old behavior.

## Performance expectations

- Assume users may scan hundreds of thousands of files.
- Keep results, duplicate groups, cleanup suggestions, and previews paged/lazy where practical.
- Do not load all heavy file metadata, hashes, thumbnails, video frames, or full text bodies into memory unless bounded.
- Use progress and cancellation for work that may exceed a few hundred milliseconds.
- Avoid repeated synchronous DB queries from UI-bound collection updates.

## UI/UX expectations

- Every selectable item, toggle, cleanup category, duplicate method, and risky action should have clear explanatory text.
- Destructive actions need confirmation, risk copy, and recoverability details.
- Empty, loading, success, cancelled, partial-success, and error states should be explicit.
- Full-screen, high-DPI, high-contrast, and large-text layouts must remain aligned and usable.
- Avoid shallow placeholder pages. Dashboard, Settings, Cleanup, Duplicates, Results, Scan, and Smart Cleaner should provide direct next actions.

## Agent workflow

1. Read `docs/AIprojectcontext/` first, then inspect the relevant code. Do not rely on memory or old docs.
2. Make the smallest coherent change that satisfies the task while preserving architecture boundaries.
3. Update tests and both documentation trees when behavior or structure changes.
4. Run the narrowest useful validation first, then broader validation when available.
5. In the final response, summarize changed files, validation run, validation not run, and any known risk or follow-up.

## When uncertain

Prefer source inspection over assumptions. If a claim affects safety, deletion, persistence, installer trust, updates, or public CLI behavior, verify it in code before changing it. If you discover stale documentation, fix it as part of the same patch.
