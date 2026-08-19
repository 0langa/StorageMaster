namespace StorageMaster.Storage.Schema;

/// <summary>
/// Single source of truth for the SQLite schema.
/// All migrations are additive — columns are only added, never renamed or removed
/// without a corresponding migration version bump.
/// </summary>
internal static class DatabaseSchema
{
    internal const int CurrentVersion = 13;

    /// <summary>SQL executed once at version 1 creation.</summary>
    internal static readonly string[] V1Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS SchemaVersion (
            Version     INTEGER NOT NULL,
            AppliedUtc  TEXT    NOT NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS ScanSessions (
            Id                INTEGER PRIMARY KEY AUTOINCREMENT,
            RootPath          TEXT    NOT NULL,
            StartedUtc        TEXT    NOT NULL,
            CompletedUtc      TEXT,
            Status            TEXT    NOT NULL DEFAULT 'Running',
            TotalSizeBytes    INTEGER NOT NULL DEFAULT 0,
            TotalFiles        INTEGER NOT NULL DEFAULT 0,
            TotalFolders      INTEGER NOT NULL DEFAULT 0,
            AccessDeniedCount INTEGER NOT NULL DEFAULT 0,
            ErrorMessage      TEXT
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS FileEntries (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId     INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
            FullPath      TEXT    NOT NULL,
            FileName      TEXT    NOT NULL,
            Extension     TEXT    NOT NULL DEFAULT '',
            SizeBytes     INTEGER NOT NULL DEFAULT 0,
            CreatedUtc    TEXT    NOT NULL,
            ModifiedUtc   TEXT    NOT NULL,
            AccessedUtc   TEXT    NOT NULL,
            Attributes    INTEGER NOT NULL DEFAULT 0,
            Category      TEXT    NOT NULL DEFAULT 'Unknown',
            IsReparsePoint INTEGER NOT NULL DEFAULT 0
        );
        """,

        // Composite index: most queries filter by session and sort by size.
        "CREATE INDEX IF NOT EXISTS IX_FileEntries_Session_Size ON FileEntries (SessionId, SizeBytes DESC);",
        "CREATE INDEX IF NOT EXISTS IX_FileEntries_Extension    ON FileEntries (SessionId, Extension);",

        """
        CREATE TABLE IF NOT EXISTS FolderEntries (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId       INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
            FullPath        TEXT    NOT NULL,
            FolderName      TEXT    NOT NULL,
            DirectSizeBytes INTEGER NOT NULL DEFAULT 0,
            TotalSizeBytes  INTEGER NOT NULL DEFAULT 0,
            FileCount       INTEGER NOT NULL DEFAULT 0,
            SubFolderCount  INTEGER NOT NULL DEFAULT 0,
            IsReparsePoint  INTEGER NOT NULL DEFAULT 0,
            WasAccessDenied INTEGER NOT NULL DEFAULT 0,
            UNIQUE (SessionId, FullPath)
        );
        """,

        "CREATE INDEX IF NOT EXISTS IX_FolderEntries_Session_Size ON FolderEntries (SessionId, TotalSizeBytes DESC);",

        """
        CREATE TABLE IF NOT EXISTS CleanupLog (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            SuggestionId TEXT    NOT NULL,
            RuleId       TEXT    NOT NULL,
            Title        TEXT    NOT NULL,
            BytesFreed   INTEGER NOT NULL DEFAULT 0,
            WasDryRun    INTEGER NOT NULL DEFAULT 0,
            Status       TEXT    NOT NULL,
            ExecutedUtc  TEXT    NOT NULL,
            ErrorMessage TEXT
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS Settings (
            Key   TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """,
    ];

    /// <summary>SQL executed once at version 2: scan error logging.</summary>
    internal static readonly string[] V2Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS ScanErrors (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId   INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
            Path        TEXT    NOT NULL,
            ErrorType   TEXT    NOT NULL,
            Message     TEXT    NOT NULL,
            OccurredAt  TEXT    NOT NULL
        );
        """,

        "CREATE INDEX IF NOT EXISTS IX_ScanErrors_SessionId ON ScanErrors (SessionId);",
    ];

    internal static readonly string[] V3Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS DuplicateRuns (
            Id               INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId        INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
            StartedUtc       TEXT    NOT NULL,
            CompletedUtc     TEXT,
            Status           TEXT    NOT NULL,
            ConfigJson       TEXT    NOT NULL,
            CandidateCount   INTEGER NOT NULL DEFAULT 0,
            GroupCount       INTEGER NOT NULL DEFAULT 0,
            ExactBytes       INTEGER NOT NULL DEFAULT 0,
            ReclaimableBytes INTEGER NOT NULL DEFAULT 0,
            ErrorCount       INTEGER NOT NULL DEFAULT 0,
            ErrorMessage     TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS DuplicateSignatures (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId    INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
            FileEntryId  INTEGER NOT NULL REFERENCES FileEntries(Id) ON DELETE CASCADE,
            Method       TEXT    NOT NULL,
            Algorithm    TEXT    NOT NULL,
            SignatureBlob BLOB,
            SignatureText TEXT,
            MetadataJson  TEXT,
            ComputedUtc   TEXT    NOT NULL,
            Status        TEXT    NOT NULL,
            ErrorMessage  TEXT,
            UNIQUE(FileEntryId, Method, Algorithm)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS DuplicateGroups (
            Id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId                     INTEGER NOT NULL REFERENCES DuplicateRuns(Id) ON DELETE CASCADE,
            Method                    TEXT    NOT NULL,
            Algorithm                 TEXT    NOT NULL,
            Confidence                REAL    NOT NULL,
            TotalBytes                INTEGER NOT NULL DEFAULT 0,
            ReclaimableBytes          INTEGER NOT NULL DEFAULT 0,
            RepresentativeFileEntryId INTEGER NOT NULL REFERENCES FileEntries(Id) ON DELETE CASCADE
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS DuplicateGroupMembers (
            Id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            GroupId              INTEGER NOT NULL REFERENCES DuplicateGroups(Id) ON DELETE CASCADE,
            FileEntryId          INTEGER NOT NULL REFERENCES FileEntries(Id) ON DELETE CASCADE,
            FullPath             TEXT    NOT NULL,
            FileName             TEXT    NOT NULL,
            SizeBytes            INTEGER NOT NULL DEFAULT 0,
            ModifiedUtc          TEXT    NOT NULL,
            Score                REAL    NOT NULL DEFAULT 0,
            IsKeeper             INTEGER NOT NULL DEFAULT 0,
            IsSelected           INTEGER NOT NULL DEFAULT 0,
            RecommendationReason TEXT    NOT NULL DEFAULT '',
            ExistsNow            INTEGER NOT NULL DEFAULT 1
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS DuplicateErrors (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId       INTEGER NOT NULL REFERENCES DuplicateRuns(Id) ON DELETE CASCADE,
            FileEntryId INTEGER REFERENCES FileEntries(Id) ON DELETE CASCADE,
            Path        TEXT    NOT NULL,
            ErrorType   TEXT    NOT NULL,
            Message     TEXT    NOT NULL,
            OccurredUtc TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_FileEntries_Session_Size_Extension ON FileEntries (SessionId, SizeBytes, Extension);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateSignatures_File_Method ON DuplicateSignatures (FileEntryId, Method, Algorithm);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateGroups_Run_Reclaimable ON DuplicateGroups (RunId, ReclaimableBytes DESC);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateGroupMembers_GroupId ON DuplicateGroupMembers (GroupId);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateRuns_SessionId ON DuplicateRuns (SessionId, StartedUtc DESC);",
    ];

    internal static readonly string[] V4Statements =
    [
        "ALTER TABLE CleanupLog ADD COLUMN AuditDataJson TEXT;"
    ];

    /// <summary>
    /// V5: Signature cache validity metadata, quarantine table, additional indexes.
    /// </summary>
    internal static readonly string[] V5Statements =
    [
        // ── Signature cache validity columns ─────────────────────────────────
        "ALTER TABLE DuplicateSignatures ADD COLUMN AlgorithmVersion  INTEGER NOT NULL DEFAULT 1;",
        "ALTER TABLE DuplicateSignatures ADD COLUMN SourceSizeBytes    INTEGER NOT NULL DEFAULT 0;",
        "ALTER TABLE DuplicateSignatures ADD COLUMN SourceModifiedUtc  TEXT    NOT NULL DEFAULT '';",
        "ALTER TABLE DuplicateSignatures ADD COLUMN SourceFileIdentity TEXT;",

        // ── Quarantine table ──────────────────────────────────────────────────
        """
        CREATE TABLE IF NOT EXISTS QuarantinedFiles (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            MemberId        INTEGER NOT NULL REFERENCES DuplicateGroupMembers(Id) ON DELETE CASCADE,
            RunId           INTEGER NOT NULL,
            OriginalPath    TEXT    NOT NULL,
            QuarantinePath  TEXT    NOT NULL,
            QuarantinedUtc  TEXT    NOT NULL,
            RestoredUtc     TEXT,
            RestoredPath    TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_RunId ON QuarantinedFiles (RunId);",
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_MemberId ON QuarantinedFiles (MemberId);",

        // ── Additional duplicate indexes for P2 scalability ───────────────────
        "CREATE INDEX IF NOT EXISTS IX_DuplicateGroupMembers_GroupId_Keeper ON DuplicateGroupMembers (GroupId, IsKeeper, IsSelected, ExistsNow);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateGroups_Run_Method_Conf ON DuplicateGroups (RunId, Method, Confidence, ReclaimableBytes DESC);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateErrors_RunId_Type ON DuplicateErrors (RunId, ErrorType);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateSignatures_Session_Method ON DuplicateSignatures (SessionId, Method, Algorithm, AlgorithmVersion);",
    ];

    /// <summary>
    /// V6: normalized path indexes for duplicate-path protection and scalable path lookup.
    /// Existing duplicate file rows are collapsed before the unique index is created.
    /// </summary>
    internal static readonly string[] V6Statements =
    [
        "ALTER TABLE FileEntries ADD COLUMN NormalizedFullPath TEXT;",
        "UPDATE FileEntries SET NormalizedFullPath = upper(FullPath) WHERE NormalizedFullPath IS NULL OR NormalizedFullPath = '';",
        """
        DELETE FROM FileEntries
        WHERE Id NOT IN (
            SELECT MIN(Id)
            FROM FileEntries
            GROUP BY SessionId, NormalizedFullPath
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_FileEntries_Session_NormalizedFullPath ON FileEntries (SessionId, NormalizedFullPath);",
        "CREATE INDEX IF NOT EXISTS IX_FileEntries_Session_NormalizedFullPath ON FileEntries (SessionId, NormalizedFullPath);",
        "CREATE INDEX IF NOT EXISTS IX_FileEntries_Session_PathNoCase ON FileEntries (SessionId, FullPath COLLATE NOCASE);",

        "ALTER TABLE FolderEntries ADD COLUMN NormalizedFullPath TEXT;",
        "UPDATE FolderEntries SET NormalizedFullPath = upper(FullPath) WHERE NormalizedFullPath IS NULL OR NormalizedFullPath = '';",
        "CREATE INDEX IF NOT EXISTS IX_FolderEntries_Session_NormalizedFullPath ON FolderEntries (SessionId, NormalizedFullPath);",
        "CREATE INDEX IF NOT EXISTS IX_FolderEntries_Session_PathNoCase ON FolderEntries (SessionId, FullPath COLLATE NOCASE);",
    ];

    internal static readonly string[] V7Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS DriveHealthSnapshots (
            Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            DriveName          TEXT    NOT NULL,
            VolumeLabel        TEXT    NOT NULL DEFAULT '',
            DriveFormat        TEXT    NOT NULL DEFAULT '',
            TotalBytes         INTEGER NOT NULL DEFAULT 0,
            FreeBytes          INTEGER NOT NULL DEFAULT 0,
            FreePercent        INTEGER NOT NULL DEFAULT 0,
            Status             TEXT    NOT NULL,
            Source             TEXT    NOT NULL DEFAULT '',
            Message            TEXT    NOT NULL DEFAULT '',
            Model              TEXT    NOT NULL DEFAULT '',
            SerialNumber       TEXT    NOT NULL DEFAULT '',
            MediaType          TEXT    NOT NULL DEFAULT '',
            TemperatureCelsius INTEGER,
            WearPercent        INTEGER,
            CapturedUtc        TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_DriveHealthSnapshots_Drive_Captured ON DriveHealthSnapshots (DriveName, CapturedUtc DESC);",
        "CREATE INDEX IF NOT EXISTS IX_DriveHealthSnapshots_Captured ON DriveHealthSnapshots (CapturedUtc DESC);",
    ];

    internal static readonly string[] V8Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS DuplicateOperationJournal (
            Id                INTEGER PRIMARY KEY AUTOINCREMENT,
            OperationId       TEXT    NOT NULL UNIQUE,
            Kind              TEXT    NOT NULL,
            Status            TEXT    NOT NULL,
            RunId             INTEGER NOT NULL,
            GroupId           INTEGER,
            MemberId          INTEGER,
            QuarantineId      INTEGER,
            Method            TEXT    NOT NULL,
            SourcePath        TEXT    NOT NULL,
            SourceIdentity    TEXT,
            DestinationPath   TEXT,
            SourceSizeBytes   INTEGER NOT NULL DEFAULT 0,
            SourceModifiedUtc TEXT,
            PlannedUtc        TEXT    NOT NULL,
            CompletedUtc      TEXT,
            BytesFreed        INTEGER,
            ErrorMessage      TEXT,
            MetadataJson      TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_DuplicateOperationJournal_RunId_Planned ON DuplicateOperationJournal (RunId, PlannedUtc DESC);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateOperationJournal_Status ON DuplicateOperationJournal (Status);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateOperationJournal_MemberId ON DuplicateOperationJournal (MemberId);",
        "CREATE INDEX IF NOT EXISTS IX_DuplicateOperationJournal_QuarantineId ON DuplicateOperationJournal (QuarantineId);",
    ];

    /// <summary>
    /// V9: QuarantinedFiles.MemberId becomes nullable so generic-cleanup
    /// quarantines (no duplicate group member) get first-class restore records.
    /// SQLite cannot alter a column constraint, so the table is rebuilt in
    /// place; ids and rows are preserved. QuarantinedFiles is a leaf table
    /// (nothing references it), so drop + rename is safe with foreign_keys=ON.
    /// </summary>
    internal static readonly string[] V9Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS QuarantinedFiles_v9 (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            MemberId        INTEGER REFERENCES DuplicateGroupMembers(Id) ON DELETE CASCADE,
            RunId           INTEGER NOT NULL,
            OriginalPath    TEXT    NOT NULL,
            QuarantinePath  TEXT    NOT NULL,
            QuarantinedUtc  TEXT    NOT NULL,
            RestoredUtc     TEXT,
            RestoredPath    TEXT
        );
        """,
        """
        INSERT INTO QuarantinedFiles_v9
            (Id, MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc, RestoredUtc, RestoredPath)
        SELECT Id, MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc, RestoredUtc, RestoredPath
        FROM QuarantinedFiles;
        """,
        "DROP TABLE QuarantinedFiles;",
        "ALTER TABLE QuarantinedFiles_v9 RENAME TO QuarantinedFiles;",
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_RunId ON QuarantinedFiles (RunId);",
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_MemberId ON QuarantinedFiles (MemberId);",
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_Unrestored ON QuarantinedFiles (QuarantinedUtc DESC) WHERE RestoredUtc IS NULL;",
    ];

    /// <summary>
    /// V10: quarantine restore records outlive duplicate and scan-history rows.
    /// Deleting a duplicate member now detaches MemberId instead of cascading
    /// into QuarantinedFiles and orphaning the physical quarantine file.
    /// </summary>
    internal static readonly string[] V10Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS QuarantinedFiles_v10 (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            MemberId        INTEGER REFERENCES DuplicateGroupMembers(Id) ON DELETE SET NULL,
            RunId           INTEGER NOT NULL,
            OriginalPath    TEXT    NOT NULL,
            QuarantinePath  TEXT    NOT NULL,
            QuarantinedUtc  TEXT    NOT NULL,
            RestoredUtc     TEXT,
            RestoredPath    TEXT
        );
        """,
        """
        INSERT INTO QuarantinedFiles_v10
            (Id, MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc, RestoredUtc, RestoredPath)
        SELECT Id, MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc, RestoredUtc, RestoredPath
        FROM QuarantinedFiles;
        """,
        "DROP TABLE QuarantinedFiles;",
        "ALTER TABLE QuarantinedFiles_v10 RENAME TO QuarantinedFiles;",
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_RunId ON QuarantinedFiles (RunId);",
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_MemberId ON QuarantinedFiles (MemberId);",
        "CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_Unrestored ON QuarantinedFiles (QuarantinedUtc DESC) WHERE RestoredUtc IS NULL;",
    ];

    /// <summary>
    /// V11: FolderEntries uses the same normalized Windows path identity as
    /// FileEntries. Historical case-only duplicates are alternate observations
    /// of one folder, so the oldest row survives and numeric snapshots use the
    /// greatest observed value instead of being summed and double-counted.
    /// </summary>
    internal static readonly string[] V11Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS FolderEntries_v11 (
            Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId          INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
            FullPath           TEXT    NOT NULL,
            FolderName         TEXT    NOT NULL,
            DirectSizeBytes    INTEGER NOT NULL DEFAULT 0,
            TotalSizeBytes     INTEGER NOT NULL DEFAULT 0,
            FileCount          INTEGER NOT NULL DEFAULT 0,
            SubFolderCount     INTEGER NOT NULL DEFAULT 0,
            IsReparsePoint     INTEGER NOT NULL DEFAULT 0,
            WasAccessDenied    INTEGER NOT NULL DEFAULT 0,
            NormalizedFullPath TEXT    NOT NULL,
            UNIQUE (SessionId, NormalizedFullPath)
        );
        """,
        """
        WITH NormalizedFolders AS (
            SELECT
                Id,
                SessionId,
                upper(COALESCE(NULLIF(NormalizedFullPath, ''), FullPath)) AS NormalizedFullPath,
                DirectSizeBytes,
                TotalSizeBytes,
                FileCount,
                SubFolderCount,
                IsReparsePoint,
                WasAccessDenied
            FROM FolderEntries
        ),
        AggregatedFolders AS (
            SELECT
                SessionId,
                NormalizedFullPath,
                MIN(Id) AS SurvivorId,
                MAX(DirectSizeBytes) AS DirectSizeBytes,
                CASE
                    WHEN MAX(TotalSizeBytes) >= MAX(DirectSizeBytes) THEN MAX(TotalSizeBytes)
                    ELSE MAX(DirectSizeBytes)
                END AS TotalSizeBytes,
                MAX(FileCount) AS FileCount,
                MAX(SubFolderCount) AS SubFolderCount,
                MAX(IsReparsePoint) AS IsReparsePoint,
                MAX(WasAccessDenied) AS WasAccessDenied
            FROM NormalizedFolders
            GROUP BY SessionId, NormalizedFullPath
        )
        INSERT INTO FolderEntries_v11
            (Id, SessionId, FullPath, FolderName, DirectSizeBytes, TotalSizeBytes,
             FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied, NormalizedFullPath)
        SELECT
            merged.SurvivorId,
            merged.SessionId,
            survivor.FullPath,
            survivor.FolderName,
            merged.DirectSizeBytes,
            merged.TotalSizeBytes,
            merged.FileCount,
            merged.SubFolderCount,
            merged.IsReparsePoint,
            merged.WasAccessDenied,
            merged.NormalizedFullPath
        FROM AggregatedFolders merged
        INNER JOIN FolderEntries survivor ON survivor.Id = merged.SurvivorId;
        """,
        "DROP TABLE FolderEntries;",
        "ALTER TABLE FolderEntries_v11 RENAME TO FolderEntries;",
        "CREATE INDEX IF NOT EXISTS IX_FolderEntries_Session_Size ON FolderEntries (SessionId, TotalSizeBytes DESC);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_FolderEntries_Session_NormalizedFullPath ON FolderEntries (SessionId, NormalizedFullPath);",
        "CREATE INDEX IF NOT EXISTS IX_FolderEntries_Session_NormalizedFullPath ON FolderEntries (SessionId, NormalizedFullPath);",
        "CREATE INDEX IF NOT EXISTS IX_FolderEntries_Session_PathNoCase ON FolderEntries (SessionId, FullPath COLLATE NOCASE);",
    ];

    /// <summary>
    /// V12: persist scan-time stable file identity. Existing rows remain null
    /// and are intentionally ineligible for scan-backed destructive actions
    /// until the user runs a fresh scan.
    /// </summary>
    internal static readonly string[] V12Statements =
    [
        "ALTER TABLE FileEntries ADD COLUMN IdentityVolumeSerial TEXT;",
        "ALTER TABLE FileEntries ADD COLUMN IdentityFileIndex TEXT;",
    ];

    /// <summary>
    /// V13: materialise each folder's parent so tree and drill-down queries become
    /// indexed equality lookups.
    /// <para>
    /// Before this level, every parent/child query derived the relationship inside
    /// the predicate — either <c>parent.FullPath = substr(child.FullPath, ...)</c>
    /// or <c>substr(NormalizedFullPath, 1, n) = prefix</c>. Neither form can use an
    /// index: the only index over <c>FullPath</c> is <c>COLLATE NOCASE</c> and the
    /// predicates compare with the default BINARY collation, and applying
    /// <c>substr()</c> to a column defeats indexing outright. SQLite fell back to
    /// constraining on <c>SessionId</c> alone, so a folder tree over a 213k-folder
    /// session cost roughly 4.5e10 row visits.
    /// </para>
    /// <para>
    /// The backfill derives the parent by pure substring on the already-normalised
    /// path. It must never recompute the normalisation itself: SQLite's
    /// <c>upper()</c> is ASCII-only while <see cref="ScanOptionValidator"/> uses
    /// <c>ToUpperInvariant</c>, so any SQL-side casing would disagree on paths
    /// containing non-ASCII characters.
    /// </para>
    /// <para>
    /// <c>rtrim(p, replace(p, '\', ''))</c> strips the trailing path segment:
    /// the second argument is the set of every character in the path except the
    /// separator, so trimming from the right stops exactly at the final separator.
    /// A path that is its own trim result is a root and gets NULL.
    /// </para>
    /// </summary>
    internal static readonly string[] V13Statements =
    [
        "ALTER TABLE FolderEntries ADD COLUMN ParentNormalizedPath TEXT;",
        """
        UPDATE FolderEntries
        SET ParentNormalizedPath = (
            SELECT CASE
                       WHEN trimmed = NormalizedFullPath OR length(trimmed) = 0 THEN NULL
                       WHEN length(trimmed) = 3 AND substr(trimmed, 2, 2) = ':\' THEN trimmed
                       ELSE substr(trimmed, 1, length(trimmed) - 1)
                   END
            FROM (SELECT rtrim(NormalizedFullPath, replace(NormalizedFullPath, '\', '')) AS trimmed)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_FolderEntries_Session_Parent_Size ON FolderEntries (SessionId, ParentNormalizedPath, TotalSizeBytes DESC);",
    ];
}
