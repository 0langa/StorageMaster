# Presentation Hardening Design

Date: 2026-06-21

## Objective

Make StorageMaster safer and more presentation-ready by resolving the verified critical audit findings and the tractable warnings without destabilizing the WinUI application immediately before the presentation.

## Scope

### Dependency security

- Upgrade the `Microsoft.Data.Sqlite` dependency chain to the smallest compatible patched version that removes `GHSA-2m69-gcr7-jv3q` from `dotnet list package --vulnerable --include-transitive`.
- Keep the application on .NET 8 and preserve the existing SQLite schema and repository APIs.

### Deletion safety

- Change destructive reparse-point classification from fail-open to fail-closed.
- If file attributes cannot be read for a directory considered for permanent recursive deletion, refuse the operation and return a per-path failure.
- Keep Recycle Bin, quarantine, root guards, partial-result reporting, and cancellation behavior intact.
- Add deterministic regression coverage by injecting an attribute-reader delegate into an internal deletion path used only for testing.

### Smart Cleaner cancellation

- Preserve `OperationCanceledException` through recursive analysis instead of swallowing it in a best-effort filesystem catch.
- Keep inaccessible-directory handling best-effort for non-cancellation failures.
- Add a focused test around the recursive scan helper rather than a timing-dependent end-to-end test.

### Test coverage

- Add Rust unit tests for argument defaults and record serialization, covering the executable's CLI/JSONL contract without invoking a real large scan.
- Do not introduce a direct test-project reference to the WinUI project in this pass; the current repository deliberately removed that dependency because it prevented CI completion.
- Record CLI/ViewModel/scheduler test-host extraction as follow-up work rather than attempting an unsafe architectural move before the presentation.

### Documentation truth sync

- Align README, `docs/AIprojectcontext/`, and `docs/public/` with version 2.1.3 and verified current behavior.
- Remove contradictions concerning schema v5/v7, accessibility annotations, folder aggregation, size-estimation bounds, and installer privilege level.
- Update both documentation trees together as required by `AGENTS.md`.

## Explicit non-goals

- No large ViewModel decomposition.
- No UI redesign or new feature work.
- No CLI architecture extraction.
- No schema migration, installer redesign, signing change, or release publication.
- No edits to unrelated user-owned files.

## Verification

Run narrow tests first, then the repository gates:

```powershell
dotnet test tests/StorageMaster.Tests/StorageMaster.Tests.csproj -c Release --filter "DeletionSafetyHardeningTests|SmartCleanerServiceTests"
dotnet list StorageMaster.sln package --vulnerable --include-transitive
dotnet format StorageMaster.sln --verify-no-changes --no-restore
dotnet build StorageMaster.sln -c Release --no-restore
dotnet test StorageMaster.sln -c Release --no-build

cd turbo-scanner
cargo fmt --check
cargo test
cargo build --release
```

## Success criteria

- The SQLite advisory no longer appears.
- Permanent deletion never recurses when reparse-point status is unknown.
- Smart Cleaner cancellation propagates promptly from recursive enumeration.
- New regression tests pass and Rust no longer reports zero tests.
- Source, tests, README, and both documentation trees agree on the current release and behavior.
- The full Release build remains warning-free.
