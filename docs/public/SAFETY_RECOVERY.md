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
- legacy `BytesFreed` outcome field (logical processed size for older move workflows; not guaranteed physical allocation reclaimed);
- error message;
- metadata.

Intent is recorded before filesystem changes. Outcome is recorded afterward. This makes crash and partial-failure states inspectable instead of ambiguous.

Duplicate removal is available only through the dedicated Duplicates workflow. Before confirmation it freezes the exact page, keeper/member set, and deletion method; editors remain disabled through execution. Immediately before mutation it locks and revalidates the keeper, recomputes the selected strategy signature, snapshots each selected member, writes journal intent, and then deletes members sequentially with per-path outcomes. Quarantine completion writes the terminal journal state and restore catalog row in one SQLite transaction. If that transaction fails after the move, the service attempts a terminal journal-only fallback and surfaces a warning instead of retrying the move. If the fallback also fails, execution stops with an exception containing the exact source and destination for manual recovery. Cached signatures are reused only when the live file still matches stored size, timestamp, attributes, and filesystem identity.

General cleanup and Smart Cleaner remain confirmation-gated. Schema-v12 scan rows carry stable volume/file identity; historical or unavailable identity fails closed and requires a fresh scan before scan-backed deletion. Medium/high-risk suggestions never start selected. Supported file suggestions carry analysis-time identity snapshots; Smart Cleaner additionally restricts every explicit file to the canonical allow-listed root for its source, reports weak analysis guards as partial, and requires a strong no-follow ancestry lease for execution validation. Audit/recovery-write failures are returned as warnings/partial status rather than being hidden or causing filesystem work to repeat.

Cleanup dry runs authorize no filesystem follow-up unless every selected suggestion returns a complete successful preview; partial, failed, skipped, missing, or audit-warning results leave deletion disabled. Permanent follow-up repeats an irreversible-action confirmation. Enabled scheduled cleanup has a separate confirmation that freezes target/rules/schedule semantics into a versioned consent field; the headless execution policy denies legacy, missing, outdated, or plan-mismatched consent and permits only Safe/Low Recycle-Bin suggestions. Scheduler task/settings mutations are preflighted; on failure the service attempts compensating rollback of both sides and explicitly surfaces any incomplete rollback.

Recycle Bin and quarantine operations move logical file data but do not normally reclaim allocation. UI wording reports bytes moved/processed; permanent-deletion figures are logical file sizes and can differ from physical allocation because of sparse files, compression, hard links, filesystem metadata, and delayed reclamation.

## Known limits

- The current journal is inspectable through repository APIs and tests, but the WinUI recovery page still needs a first-class journal view.
- A process or power failure in the narrow interval after a quarantine move but before its transactional/fallback journal write can still leave a moved file requiring manual quarantine-directory reconciliation.
- Files changed after scan are skipped by duplicate cleanup rather than force-deleted.
- Permanent directory traversal is handle-bound and no-follow. Recycle Bin operations and individual file deletion still cross a final path-based Windows API boundary; snapshots and postchecks reduce risk but cannot mathematically eliminate a malicious last-instruction path swap.
- The program-leftover rule is heuristic. It is disabled, unselected, high-risk, and Recycle-Bin-only by default, but a reviewed directory can still receive new content between analysis and execution.
- Managed scanning resolves and checks reparse targets before enumeration, but Core-only metadata checks cannot close every adversarial junction swap. Turbo uses strong no-follow root-ancestor locks where ACL/sharing permits; protected ancestors and queued descendants retain a documented same-privilege swap window. Persisted identity and downstream deletion validation fail closed on changed files.
