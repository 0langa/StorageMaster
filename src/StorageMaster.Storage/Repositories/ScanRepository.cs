using System.Globalization;
using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Storage.Repositories;

/// <summary>
/// SQLite-backed implementation of IScanRepository.
///
/// Bulk inserts use explicit transactions to batch many rows into a single fsync,
/// reducing write amplification by an order of magnitude vs. autocommit inserts.
/// </summary>
public sealed class ScanRepository : IScanRepository
{
    private readonly StorageDbContext _db;

    public ScanRepository(StorageDbContext db) => _db = db;

    // ── Sessions ──────────────────────────────────────────────────────────

    public async Task<ScanSession> CreateSessionAsync(string rootPath, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            var startedUtc = DateTime.UtcNow;
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ScanSessions (RootPath, StartedUtc, Status)
                VALUES ($root, $started, 'Running');
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$root", rootPath);
            cmd.Parameters.AddWithValue("$started", startedUtc.ToString("O"));
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

            return new ScanSession
            {
                Id = id,
                RootPath = rootPath,
                StartedUtc = startedUtc,
                Status = ScanStatus.Running,
            };
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<ScanSession?> GetSessionAsync(long sessionId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ScanSessions WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<ScanSession>> GetRecentSessionsAsync(int count = 10, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ScanSessions ORDER BY StartedUtc DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", count);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var sessions = new List<ScanSession>();
        while (await reader.ReadAsync(ct))
            sessions.Add(ReadSession(reader));
        return sessions;
    }

    public async Task UpdateSessionAsync(ScanSession session, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE ScanSessions SET
                    CompletedUtc      = $completed,
                    Status            = $status,
                    TotalSizeBytes    = $size,
                    TotalFiles        = $files,
                    TotalFolders      = $folders,
                    AccessDeniedCount = $denied,
                    ErrorMessage      = $error
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$completed", (object?)session.CompletedUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", session.Status.ToString());
            cmd.Parameters.AddWithValue("$size", session.TotalSizeBytes);
            cmd.Parameters.AddWithValue("$files", session.TotalFiles);
            cmd.Parameters.AddWithValue("$folders", session.TotalFolders);
            cmd.Parameters.AddWithValue("$denied", session.AccessDeniedCount);
            cmd.Parameters.AddWithValue("$error", (object?)session.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", session.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    // ── File entries ──────────────────────────────────────────────────────

    public async Task InsertFileEntriesAsync(IReadOnlyList<FileEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO FileEntries
                    (SessionId, FullPath, FileName, Extension, SizeBytes,
                     CreatedUtc, ModifiedUtc, AccessedUtc, Attributes, Category, IsReparsePoint,
                     NormalizedFullPath, IdentityVolumeSerial, IdentityFileIndex)
                VALUES
                    ($sid, $path, $name, $ext, $size,
                     $created, $modified, $accessed, $attrs, $cat, $reparse,
                     $normalized, $identityVolume, $identityIndex)
                ON CONFLICT(SessionId, NormalizedFullPath) DO UPDATE SET
                    FileName       = excluded.FileName,
                    Extension      = excluded.Extension,
                    SizeBytes      = excluded.SizeBytes,
                    CreatedUtc     = excluded.CreatedUtc,
                    ModifiedUtc    = excluded.ModifiedUtc,
                    AccessedUtc    = excluded.AccessedUtc,
                    Attributes     = excluded.Attributes,
                    Category       = excluded.Category,
                    IsReparsePoint = excluded.IsReparsePoint,
                    IdentityVolumeSerial = excluded.IdentityVolumeSerial,
                    IdentityFileIndex = excluded.IdentityFileIndex;
                """;

            var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pExt = cmd.Parameters.Add("$ext", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pCreated = cmd.Parameters.Add("$created", SqliteType.Text);
            var pMod = cmd.Parameters.Add("$modified", SqliteType.Text);
            var pAccess = cmd.Parameters.Add("$accessed", SqliteType.Text);
            var pAttrs = cmd.Parameters.Add("$attrs", SqliteType.Integer);
            var pCat = cmd.Parameters.Add("$cat", SqliteType.Text);
            var pReparse = cmd.Parameters.Add("$reparse", SqliteType.Integer);
            var pNormalized = cmd.Parameters.Add("$normalized", SqliteType.Text);
            var pIdentityVolume = cmd.Parameters.Add("$identityVolume", SqliteType.Text);
            var pIdentityIndex = cmd.Parameters.Add("$identityIndex", SqliteType.Text);

            foreach (var e in entries)
            {
                pSid.Value = e.SessionId;
                pPath.Value = e.FullPath;
                pName.Value = e.FileName;
                pExt.Value = e.Extension;
                pSize.Value = e.SizeBytes;
                pCreated.Value = e.CreatedUtc.ToString("O");
                pMod.Value = e.ModifiedUtc.ToString("O");
                pAccess.Value = e.AccessedUtc.ToString("O");
                pAttrs.Value = (int)e.Attributes;
                pCat.Value = e.Category.ToString();
                pReparse.Value = e.IsReparsePoint ? 1 : 0;
                pNormalized.Value = NormalizeForStorage(e.FullPath);
                pIdentityVolume.Value = (object?)e.Identity?.VolumeSerial ?? DBNull.Value;
                pIdentityIndex.Value = e.Identity is null
                    ? DBNull.Value
                    : e.Identity.FileIndex.ToString(CultureInfo.InvariantCulture);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    // ── Folder entries ─────────────────────────────────────────────────────

    public async Task UpsertFolderEntriesAsync(IReadOnlyList<FolderEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;

            // Exact-casing rows are scanner metric batches and accumulate.
            // Case-only variants are duplicate observations of one Windows
            // folder and merge conservatively without double-counting.
            cmd.CommandText = """
                INSERT INTO FolderEntries
                    (SessionId, FullPath, FolderName, DirectSizeBytes, TotalSizeBytes,
                     FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied,
                     NormalizedFullPath, ParentNormalizedPath)
                VALUES
                    ($sid, $path, $name, $direct, $total,
                     $files, $subs, $reparse, $denied,
                     $normalized, $parent)
                ON CONFLICT(SessionId, NormalizedFullPath) DO UPDATE SET
                    DirectSizeBytes = CASE
                        WHEN FolderEntries.FullPath = excluded.FullPath
                            THEN FolderEntries.DirectSizeBytes + excluded.DirectSizeBytes
                        ELSE max(FolderEntries.DirectSizeBytes, excluded.DirectSizeBytes)
                    END,
                    TotalSizeBytes = CASE
                        WHEN FolderEntries.FullPath = excluded.FullPath
                            THEN FolderEntries.TotalSizeBytes + excluded.TotalSizeBytes
                        ELSE max(
                            FolderEntries.TotalSizeBytes,
                            excluded.TotalSizeBytes,
                            FolderEntries.DirectSizeBytes,
                            excluded.DirectSizeBytes)
                    END,
                    FileCount = CASE
                        WHEN FolderEntries.FullPath = excluded.FullPath
                            THEN FolderEntries.FileCount + excluded.FileCount
                        ELSE max(FolderEntries.FileCount, excluded.FileCount)
                    END,
                    SubFolderCount = CASE
                        WHEN FolderEntries.FullPath = excluded.FullPath
                            THEN excluded.SubFolderCount
                        ELSE max(FolderEntries.SubFolderCount, excluded.SubFolderCount)
                    END,
                    IsReparsePoint  = FolderEntries.IsReparsePoint OR excluded.IsReparsePoint,
                    WasAccessDenied = FolderEntries.WasAccessDenied OR excluded.WasAccessDenied,
                    NormalizedFullPath = excluded.NormalizedFullPath,
                    ParentNormalizedPath = excluded.ParentNormalizedPath;
                """;

            var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pDirect = cmd.Parameters.Add("$direct", SqliteType.Integer);
            var pTotal = cmd.Parameters.Add("$total", SqliteType.Integer);
            var pFiles = cmd.Parameters.Add("$files", SqliteType.Integer);
            var pSubs = cmd.Parameters.Add("$subs", SqliteType.Integer);
            var pReparse = cmd.Parameters.Add("$reparse", SqliteType.Integer);
            var pDenied = cmd.Parameters.Add("$denied", SqliteType.Integer);
            var pNormalized = cmd.Parameters.Add("$normalized", SqliteType.Text);
            var pParent = cmd.Parameters.Add("$parent", SqliteType.Text);

            foreach (var e in entries)
            {
                var normalized = NormalizeForStorage(e.FullPath);
                pSid.Value = e.SessionId;
                pPath.Value = e.FullPath;
                pName.Value = e.FolderName;
                pDirect.Value = e.DirectSizeBytes;
                pTotal.Value = e.TotalSizeBytes;
                pFiles.Value = e.FileCount;
                pSubs.Value = e.SubFolderCount;
                pReparse.Value = e.IsReparsePoint ? 1 : 0;
                pDenied.Value = e.WasAccessDenied ? 1 : 0;
                pNormalized.Value = normalized;
                pParent.Value = (object?)ScanOptionValidator.GetParentOfNormalizedPath(normalized)
                    ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    // ── Queries ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FileEntry>> GetLargestFilesAsync(
        long sessionId, int topN, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM FileEntries
            WHERE SessionId = $sid
            ORDER BY SizeBytes DESC, FullPath ASC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$n", topN);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FileEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFileEntry(reader));
        return list;
    }

    public async Task<IReadOnlyList<FolderEntry>> GetLargestFoldersAsync(
        long sessionId, int topN, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM FolderEntries
            WHERE SessionId = $sid
            ORDER BY TotalSizeBytes DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$n", topN);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FolderEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFolderEntry(reader));
        return list;
    }

    public async Task<IReadOnlyList<FileEntry>> SearchFilesAsync(
        long sessionId,
        string? filter,
        string? categoryFilter,
        string sortColumn,
        bool descending,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        var sort = sortColumn switch
        {
            "Modified" => $"ModifiedUtc {(descending ? "DESC" : "ASC")}",
            "Type" => $"Category {(descending ? "DESC" : "ASC")}, SizeBytes DESC",
            _ => $"SizeBytes {(descending ? "DESC" : "ASC")}, FullPath ASC",
        };

        cmd.CommandText = $"""
            SELECT * FROM FileEntries
            WHERE SessionId = $sid
              AND ($filter = '' OR FullPath LIKE '%' || $filter || '%')
              AND ($category = '' OR Category = $category)
            ORDER BY {sort}
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$filter", filter?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$category", categoryFilter?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$offset", offset);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FileEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFileEntry(reader));
        return list;
    }

    public async Task<long> CountFilesAsync(long sessionId, string? filter, string? categoryFilter, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM FileEntries
            WHERE SessionId = $sid
              AND ($filter = '' OR FullPath LIKE '%' || $filter || '%')
              AND ($category = '' OR Category = $category);
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$filter", filter?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$category", categoryFilter?.Trim() ?? string.Empty);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<FolderEntry>> SearchFoldersAsync(
        long sessionId,
        string? filter,
        string sortColumn,
        bool descending,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        var sort = sortColumn switch
        {
            "Files" => $"FileCount {(descending ? "DESC" : "ASC")}, TotalSizeBytes DESC",
            _ => $"TotalSizeBytes {(descending ? "DESC" : "ASC")}, FullPath ASC",
        };

        cmd.CommandText = $"""
            SELECT * FROM FolderEntries
            WHERE SessionId = $sid
              AND ($filter = '' OR FullPath LIKE '%' || $filter || '%')
            ORDER BY {sort}
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$filter", filter?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$offset", offset);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FolderEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFolderEntry(reader));
        return list;
    }

    public async Task<long> CountFoldersAsync(long sessionId, string? filter, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM FolderEntries
            WHERE SessionId = $sid
              AND ($filter = '' OR FullPath LIKE '%' || $filter || '%');
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$filter", filter?.Trim() ?? string.Empty);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyDictionary<FileTypeCategory, (long Count, long Bytes)>> GetCategoryBreakdownAsync(
        long sessionId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Category, COUNT(*) AS FileCount, SUM(SizeBytes) AS TotalBytes
            FROM FileEntries
            WHERE SessionId = $sid
            GROUP BY Category;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var dict = new Dictionary<FileTypeCategory, (long, long)>();
        while (await reader.ReadAsync(ct))
        {
            var cat = Enum.TryParse<FileTypeCategory>(reader.GetString(0), out var c)
                ? c : FileTypeCategory.Unknown;
            dict[cat] = (reader.GetInt64(1), reader.GetInt64(2));
        }
        return dict;
    }

    public async Task DeleteSessionAsync(long sessionId, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ScanSessions WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", sessionId);
            await cmd.ExecuteNonQueryAsync(ct);

            using var optimize = conn.CreateCommand();
            optimize.CommandText = "PRAGMA optimize;";
            await optimize.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task DeleteFileEntryAsync(long fileId, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM FileEntries WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", fileId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task MarkSessionStaleAsync(long sessionId, string reason, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE ScanSessions
                SET ErrorMessage = CASE
                    WHEN ErrorMessage IS NULL OR ErrorMessage = '' THEN $reason
                    ELSE ErrorMessage || char(10) || $reason
                END
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$reason", reason);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<FolderEntry>> GetAllFolderPathsForSessionAsync(
        long sessionId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, SessionId, FullPath, FolderName, DirectSizeBytes, TotalSizeBytes,
                   FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied
            FROM FolderEntries
            WHERE SessionId = $sid;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FolderEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFolderEntry(reader));
        return list;
    }

    public async Task<IReadOnlyList<FolderEntry>> GetFolderTreeRootsAsync(
        long sessionId,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        // A root is a folder whose parent is not itself part of this session.
        // Matching on the materialised ParentNormalizedPath keeps the correlated
        // lookup on the (SessionId, NormalizedFullPath) unique index. The former
        // `parent.FullPath = substr(f.FullPath, ...)` form could not use any index,
        // because the only FullPath index is COLLATE NOCASE while the comparison
        // used BINARY, so SQLite scanned every folder row for every folder row.
        cmd.CommandText = """
            SELECT f.*
            FROM FolderEntries f
            WHERE f.SessionId = $sid
              AND (
                    f.NormalizedFullPath = $rootNorm
                    OR f.ParentNormalizedPath IS NULL
                    OR NOT EXISTS (
                        SELECT 1
                        FROM FolderEntries parent
                        WHERE parent.SessionId = f.SessionId
                          AND parent.NormalizedFullPath = f.ParentNormalizedPath
                    )
                  )
            ORDER BY f.TotalSizeBytes DESC, f.FullPath ASC;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$rootNorm", NormalizeForStorage(await GetSessionRootPathAsync(conn, sessionId, ct)));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FolderEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFolderEntry(reader));
        return list;
    }

    public async Task<IReadOnlyList<FolderEntry>> GetFolderChildrenAsync(
        long sessionId,
        string parentPath,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        // Direct children are an indexed equality on the materialised parent.
        // The previous prefix form applied substr() to the column, which defeats
        // indexing entirely and made every Space Map drill-down scan the whole
        // session.
        cmd.CommandText = """
            SELECT *
            FROM FolderEntries
            WHERE SessionId = $sid
              AND ParentNormalizedPath = $parentNorm
            ORDER BY TotalSizeBytes DESC, FullPath ASC;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$parentNorm", NormalizeForStorage(parentPath));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FolderEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFolderEntry(reader));
        return list;
    }

    public async Task<int> CountFolderChildrenAsync(
        long sessionId,
        string parentPath,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM FolderEntries
            WHERE SessionId = $sid
              AND ParentNormalizedPath = $parentNorm;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$parentNorm", NormalizeForStorage(parentPath));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<string> GetSessionRootPathAsync(
        SqliteConnection conn,
        long sessionId,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RootPath FROM ScanSessions WHERE Id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        return await cmd.ExecuteScalarAsync(ct) as string ?? string.Empty;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        try
        {
            return ScanOptionValidator.NormalizeDirectoryPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    public async Task UpdateFolderTotalsAsync(
        long sessionId,
        IReadOnlyDictionary<string, long> pathToTotal,
        CancellationToken ct = default)
    {
        if (pathToTotal.Count == 0) return;

        // Previously this ran one UPDATE per folder, matched on FullPath, across
        // many small transactions. FullPath equality cannot use an index — the
        // only one covering it is COLLATE NOCASE while the predicate compares
        // BINARY — so each statement scanned every folder row in the session. A
        // 213k-folder scan therefore cost on the order of 4.5e10 row visits and
        // took over twenty minutes.
        //
        // Now the totals land in a temporary table and one UPDATE ... FROM joins
        // on NormalizedFullPath, which is covered by a unique BINARY index. The
        // whole finalisation is a single transaction holding the write lock once.
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct);

            using (var create = conn.CreateCommand())
            {
                create.Transaction = (SqliteTransaction)tx;
                create.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS FolderTotalStaging (
                        NormalizedFullPath TEXT PRIMARY KEY,
                        TotalSizeBytes     INTEGER NOT NULL
                    );
                    DELETE FROM FolderTotalStaging;
                    """;
                await create.ExecuteNonQueryAsync(ct);
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)tx;
                insert.CommandText = """
                    INSERT INTO FolderTotalStaging (NormalizedFullPath, TotalSizeBytes)
                    VALUES ($path, $total)
                    ON CONFLICT(NormalizedFullPath) DO UPDATE SET
                        TotalSizeBytes = excluded.TotalSizeBytes;
                    """;
                var pPath = insert.Parameters.Add("$path", SqliteType.Text);
                var pTotal = insert.Parameters.Add("$total", SqliteType.Integer);

                foreach (var (path, total) in pathToTotal)
                {
                    ct.ThrowIfCancellationRequested();
                    pPath.Value = NormalizeForStorage(path);
                    pTotal.Value = total;
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }

            using (var apply = conn.CreateCommand())
            {
                apply.Transaction = (SqliteTransaction)tx;
                apply.CommandText = """
                    UPDATE FolderEntries
                    SET TotalSizeBytes = staging.TotalSizeBytes
                    FROM FolderTotalStaging AS staging
                    WHERE FolderEntries.SessionId = $sid
                      AND FolderEntries.NormalizedFullPath = staging.NormalizedFullPath;
                    """;
                apply.Parameters.AddWithValue("$sid", sessionId);
                await apply.ExecuteNonQueryAsync(ct);
            }

            using (var drop = conn.CreateCommand())
            {
                drop.Transaction = (SqliteTransaction)tx;
                drop.CommandText = "DROP TABLE IF EXISTS FolderTotalStaging;";
                await drop.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    // ── Mapping helpers ────────────────────────────────────────────────────

    private static ScanSession ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        RootPath = r.GetString(r.GetOrdinal("RootPath")),
        StartedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("StartedUtc"))),
        CompletedUtc = r.IsDBNull(r.GetOrdinal("CompletedUtc")) ? null
                            : UtcTimestamp.Parse(r.GetString(r.GetOrdinal("CompletedUtc"))),
        Status = Enum.TryParse<ScanStatus>(r.GetString(r.GetOrdinal("Status")), ignoreCase: true, out var status)
            ? status
            : ScanStatus.Failed,
        TotalSizeBytes = r.GetInt64(r.GetOrdinal("TotalSizeBytes")),
        TotalFiles = r.GetInt64(r.GetOrdinal("TotalFiles")),
        TotalFolders = r.GetInt64(r.GetOrdinal("TotalFolders")),
        AccessDeniedCount = r.GetInt64(r.GetOrdinal("AccessDeniedCount")),
        ErrorMessage = r.IsDBNull(r.GetOrdinal("ErrorMessage")) ? null
                            : r.GetString(r.GetOrdinal("ErrorMessage")),
    };

    private static FileEntry ReadFileEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        SessionId = r.GetInt64(r.GetOrdinal("SessionId")),
        FullPath = r.GetString(r.GetOrdinal("FullPath")),
        FileName = r.GetString(r.GetOrdinal("FileName")),
        Extension = r.GetString(r.GetOrdinal("Extension")),
        SizeBytes = r.GetInt64(r.GetOrdinal("SizeBytes")),
        CreatedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("CreatedUtc"))),
        ModifiedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("ModifiedUtc"))),
        AccessedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("AccessedUtc"))),
        Attributes = (FileAttributes)r.GetInt32(r.GetOrdinal("Attributes")),
        Category = Enum.TryParse<FileTypeCategory>(r.GetString(r.GetOrdinal("Category")), out var cat)
                         ? cat : FileTypeCategory.Unknown,
        Identity = ReadFileIdentity(r),
        IsReparsePoint = r.GetInt32(r.GetOrdinal("IsReparsePoint")) == 1,
    };

    private static FileIdentity? ReadFileIdentity(SqliteDataReader reader)
    {
        var volumeOrdinal = reader.GetOrdinal("IdentityVolumeSerial");
        var indexOrdinal = reader.GetOrdinal("IdentityFileIndex");
        if (reader.IsDBNull(volumeOrdinal) || reader.IsDBNull(indexOrdinal))
            return null;

        var volumeSerial = reader.GetString(volumeOrdinal);
        return string.IsNullOrWhiteSpace(volumeSerial)
            || !ulong.TryParse(
                reader.GetString(indexOrdinal),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var fileIndex)
            ? null
            : new FileIdentity(volumeSerial, fileIndex);
    }

    private static FolderEntry ReadFolderEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        SessionId = r.GetInt64(r.GetOrdinal("SessionId")),
        FullPath = r.GetString(r.GetOrdinal("FullPath")),
        FolderName = r.GetString(r.GetOrdinal("FolderName")),
        DirectSizeBytes = r.GetInt64(r.GetOrdinal("DirectSizeBytes")),
        TotalSizeBytes = r.GetInt64(r.GetOrdinal("TotalSizeBytes")),
        FileCount = r.GetInt32(r.GetOrdinal("FileCount")),
        SubFolderCount = r.GetInt32(r.GetOrdinal("SubFolderCount")),
        IsReparsePoint = r.GetInt32(r.GetOrdinal("IsReparsePoint")) == 1,
        WasAccessDenied = r.GetInt32(r.GetOrdinal("WasAccessDenied")) == 1,
    };

    private static string NormalizeForStorage(string path)
    {
        try
        {
            return ScanOptionValidator.NormalizePathForStorage(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().ToUpperInvariant();
        }
    }
}
