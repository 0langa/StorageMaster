# StorageMaster 3.0 audit

StorageMaster already contains much of the StorageMaster 3.0 surface: exact SHA-256 duplicate detection, normalized-text review, image pHash, video pHash integration through FFmpeg, keeper policies, quarantine/restore records, SQLite persistence, WinUI duplicate review, CLI/headless workflows, and deletion hardening tests.

## Architecture boundaries

- `StorageMaster.Core` owns duplicate detection strategies, keeper policy, cleanup execution, scanner contracts, and recovery abstractions.
- `StorageMaster.Storage` owns SQLite schema, migrations, repositories, write serialization, and journal persistence.
- `StorageMaster.Platform.Windows` owns filesystem effects, Recycle Bin, quarantine moves, Windows identity/snapshot behavior, and reparse-point-safe deletion.
- `StorageMaster.UI` owns WinUI presentation, review controls, confirmation dialogs, progress, previews, and navigation.

## Safety gaps closed in 3.0

- Duplicate cleanup now writes a recovery journal intent before moving, recycling, or deleting selected duplicate files.
- Duplicate cleanup updates the journal after each filesystem outcome, including failed outcomes.
- Quarantine restore now writes restore intent before moving the quarantined file and records restored/failed outcome afterward.
- The journal is stored in SQLite schema version 8 and survives process restart for audit/recovery inspection.

## Remaining 3.0 work

- Build the desktop visual regression harness described in `VISUAL_REGRESSION.md`.
- Expose recovery journal inspection and retry/repair controls directly in the WinUI duplicate recovery surface.
- Add video fixtures that can run in CI without depending on system codecs beyond the documented FFmpeg path.
- Capture benchmark baselines on representative HDD, SATA SSD, and NVMe machines.
