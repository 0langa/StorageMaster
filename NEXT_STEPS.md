# StorageMaster — Status & Roadmap
_Portfolio audit: 2026-07-11_

## What this is

A Windows disk analyzer and storage cleaner: parallel BFS scanner plus an optional Rust "turbo"
scanner, 17 cleanup rules behind Recycle-Bin/quarantine safety and an append-only audit log,
duplicate detection (exact SHA-256, normalized text, image/video pHash via FFmpeg), interactive
treemap space map, scan-delta insights, drive-health telemetry, Task Scheduler integration,
system tray, a full CLI/headless mode, an Inno Setup installer, and a GitHub auto-updater with
digest/signature verification.

Stack: C# / .NET 8 / WinUI 3 (MVVM, CommunityToolkit, DI), SQLite (WAL, schema v9), Rust (jwalk)
turbo scanner speaking a JSONL contract, GitHub Actions CI plus a tag-triggered release
pipeline.

## Current state

- **Released: v2.1.4** (2026-06-22). 39 tags; `release.yml` builds, verifies, and attaches the
  installer on every `v*.*.*` tag. This is a genuinely shipped, versioned desktop product with
  an update channel.
- **Four commits of substantial, UNRELEASED work sit on `main`** (2026-07-05, `git describe` =
  `v2.1.4-4-gd3c67cc`):
  - `99591d2` — duplicate-operation recovery journal (intent/outcome records around every
    duplicate cleanup) + safety docs;
  - `2761d36` — production-readiness hardening: ~16 fixes including elevation arg-quoting that
    corrupted `C:\` deep scans, UAC-cancel crash, scanner consumer-failure deadlock, silent
    delete failures surfaced, tray icon/startup fixes;
  - `97414ad` — deferred-gap closure: schema v9 (quarantine restore records for generic cleanup
    with one-click UI restore), turbo scanner JSONL contract v2 (`is_hidden`, `--skip-hidden`),
    atomic `ISettingsRepository.UpdateAsync` fixing write races, 200%-display-scale layout fix
    on `DuplicatesPage`.
  - Users on 2.1.4 have none of these safety fixes. This is the project's single biggest loose
    end.
- **Release hygiene has drifted around that unreleased work**: CHANGELOG.md ends at 2.1.4; the
  README already documents unreleased features and calls the recovery journal "StorageMaster
  3.0"; README's "Further reading" links point to `docs/AIprojectcontext/…`, which the 2.1.4
  cleanup retired to `archive/` — the live equivalents are `docs/public/ARCHITECTURE.md`,
  `CODEMAP.md`, `DOCUMENTATION.md`.
- **Working tree is not clean**: modified `tests/visual-baselines/README.md` (one-line metadata
  correction) and untracked `docs/internal/unknown_filetypes.md` in a new, uncommitted
  `docs/internal/` directory.
- **Tests/CI healthy**: 231 passed / 1 intentional skip (visual-regression readiness) across 40
  test files in `tests/StorageMaster.Tests/`; `dotnet format` clean; `cargo fmt/clippy/test`
  clean (5 Rust tests); `ci.yml` + `release.yml`.
- **Verified live on real hardware** (MANUAL_TESTING_HANDOFF.md): v8→v9 migration on the live
  DB, CLI quarantine → 5 restore records → one-click UI restore 5/5, atomic settings saves
  surviving restart, cancelled-scan partial totals persisted.
- **Open quality tails**:
  - visual-baseline matrix partial (31 PNGs; missing quarantine/permanent dialog variants,
    dedupe/delete progress states, error states, tray/toast — listed in
    `tests/visual-baselines/README.md`);
  - benchmark baselines cover only the NVMe dev machine (`docs/public/BENCHMARK_BASELINES.md`);
  - deferred UX finding: Recycle-Bin vs. quarantine toggles are buried in an "Advanced options"
    expander on the Duplicates page — the manual tester couldn't find them;
  - stale remote branch `codex/v2-prerelease`;
  - the ROADMAP.md Windows compatibility matrix is almost entirely "not yet lab-tested" (only
    22H2 dev target and 24H2 CI target exercised).

## Definition of "finished"

The product is shipped and mature; "finished" for the next milestone (v2.2.0) means:

- Everything currently on `main` released through the tag pipeline: CHANGELOG entries for the
  three post-2.1.4 commits, version bumped consistently (README header,
  `installer/StorageMaster.iss`, `Directory.Build.props` if pinned there, docs), installer
  attached to the release, and the in-app updater verified to offer 2.2.0 from a 2.1.4 install.
- README free of drift: no phantom `docs/AIprojectcontext/` links, no "3.0" phrasing for shipped
  features, schema/version references current (schema is v9 now, README table still says the
  journal is v8-era).
- Clean `git status`; stale branch removed.
- ROADMAP.md M-1.1 (structured logging) shipped, and the visual-baseline matrix tail captured or
  explicitly descoped.
- The Windows 10 1809 minimum-support claim validated once on a real 17763 image, or the stated
  minimum raised.

## Roadmap

### Phase 1 — Now (next 1–2 weeks)

1. **Tidy the working tree.** Commit the `tests/visual-baselines/README.md` fix; decide whether
   `docs/internal/unknown_filetypes.md` is repo material or scratch, and either commit or remove
   it.
2. **Ship v2.2.0.** Write CHANGELOG sections for `99591d2` / `2761d36` / `97414ad`; bump the
   version everywhere the release checklist touches; tag `v2.2.0`; confirm `release.yml`
   produces `StorageMaster-2.2.0-win-x64-Setup.exe` with checksums; upgrade a 2.1.4 install
   through the in-app updater as the final gate. Real deletion-safety fixes are sitting
   undelivered — this is the highest-value single action in the repo.
3. **Fix README drift in the same pass**: repoint Further-reading links to `docs/public/`,
   recast "StorageMaster 3.0 adds…" to the released version number, re-verify the schema table
   and the version banner.
4. **Prune `codex/v2-prerelease`** on origin (history is merged; the `v2.0.0-prerelease` tag
   preserves the point-in-time).

### Phase 2 — Next (2–6 weeks) — ROADMAP.md Phase 1 (v2.2.x line)

5. **M-1.1 structured logging**: Serilog rolling file sink at
   `%LOCALAPPDATA%\StorageMaster\logs\sm-{Date}.log` (5 files × 5 MB), wired through the
   existing `Microsoft.Extensions.Logging` DI; unify with the existing `startup-errors.log`.
6. **M-1.4 test expansion**: scheduler CLI tests (`--cli jobs run|list`), Scan Workspace
   integration tests, UI error-state tests; spike the WinUI test-host output-copy blocker that
   keeps ViewModel tests out of CI.
7. **Close the manual-test tail** from MANUAL_TESTING_HANDOFF.md: capture the remaining
   visual-baseline scenarios, and land the deferred UX fix by surfacing the
   Recycle-Bin/quarantine choice directly in the delete-confirmation dialog on `DuplicatesPage`.
8. **Benchmark baselines on HDD and SATA-SSD machines** to complement the NVMe numbers, using
   the fixed `benchmarks/StorageMaster.Benchmarks` project.

### Phase 3 — Later (optional/stretch)

9. **M-1.2 ARM64**: add `aarch64-pc-windows-msvc` to `release.yml` and an ARM64 arch to
   `installer/StorageMaster.iss`.
10. **M-1.3 drive-health hardware lab** (NVMe/SATA/USB/SMB behavior table with explicit
    Unsupported/Unknown per class) and **M-1.5 Windows 10/11 compatibility matrix** (builds
    17763 → 26100).
11. **v2.3.x accessibility pass** per ROADMAP.md: keyboard navigation, Narrator/UIA automation
    names, high contrast, text scaling at 125–200%; plus the in-app what's-new panel (M-2.5).
12. **Remaining 3.0 items** from `docs/public/STORAGEMASTER_3_AUDIT.md`: recovery-journal
    inspection/retry controls in the duplicates UI, and CI-safe video fixtures for the pHash
    pipeline.

## Effort to "finished"

**M.** The v2.2.0 release itself is well under a week (the code is done, tested, and
live-verified); completing the v2.2.x roadmap line — logging, test expansion, baseline tails,
second-machine benchmarks — is roughly a month part-time.
