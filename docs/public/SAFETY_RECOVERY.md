# Safety and recovery model

StorageMaster is a storage-management tool. Recoverability and auditability take priority over convenience.

## Deletion modes

- Quarantine: preferred for duplicate cleanup. Files are moved into the app quarantine and can be restored.
- Recycle Bin: preferred for general cleanup when Windows supports recycling the target path.
- Permanent delete: allowed only after explicit user intent and confirmation.

## Recovery journal

Duplicate cleanup and restore operations write to `DuplicateOperationJournal`.

Each entry records:

- operation id;
- operation kind;
- status;
- run/group/member/quarantine ids where available;
- deletion method;
- source path;
- destination/quarantine path;
- source size and modification time;
- planned and completed timestamps;
- bytes freed;
- error message;
- metadata.

Intent is recorded before filesystem changes. Outcome is recorded afterward. This makes crash and partial-failure states inspectable instead of ambiguous.

## Known limits

- The current journal is inspectable through repository APIs and tests, but the WinUI recovery page still needs a first-class journal view.
- Files changed after scan are skipped by duplicate cleanup rather than force-deleted.
- Reparse-point safety depends on platform deletion guards and scanner metadata; tests cover known junction-safe behavior.
