namespace StorageMaster.Storage.Schema;

/// <summary>
/// Single source of truth for the SQLite schema.
/// All migrations are additive — columns are only added, never renamed or removed
/// without a corresponding migration version bump.
/// </summary>
internal static class DatabaseSchema
{
    internal const int CurrentVersion = 1;

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
}
