using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

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
            var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ScanSessions (RootPath, StartedUtc, Status)
                VALUES ($root, $started, 'Running');
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$root",    rootPath);
            cmd.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

            return new ScanSession
            {
                Id        = id,
                RootPath  = rootPath,
                StartedUtc = DateTime.UtcNow,
                Status    = ScanStatus.Running,
            };
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<ScanSession?> GetSessionAsync(long sessionId, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ScanSessions WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<ScanSession>> GetRecentSessionsAsync(int count = 10, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
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
            var conn = await _db.GetConnectionAsync(ct);
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
            cmd.Parameters.AddWithValue("$status",    session.Status.ToString());
            cmd.Parameters.AddWithValue("$size",      session.TotalSizeBytes);
            cmd.Parameters.AddWithValue("$files",     session.TotalFiles);
            cmd.Parameters.AddWithValue("$folders",   session.TotalFolders);
            cmd.Parameters.AddWithValue("$denied",    session.AccessDeniedCount);
            cmd.Parameters.AddWithValue("$error",     (object?)session.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id",        session.Id);
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
            var conn = await _db.GetConnectionAsync(ct);
            using var tx  = await conn.BeginTransactionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO FileEntries
                    (SessionId, FullPath, FileName, Extension, SizeBytes,
                     CreatedUtc, ModifiedUtc, AccessedUtc, Attributes, Category, IsReparsePoint)
                VALUES
                    ($sid, $path, $name, $ext, $size,
                     $created, $modified, $accessed, $attrs, $cat, $reparse);
                """;

            var pSid     = cmd.Parameters.Add("$sid",     SqliteType.Integer);
            var pPath    = cmd.Parameters.Add("$path",    SqliteType.Text);
            var pName    = cmd.Parameters.Add("$name",    SqliteType.Text);
            var pExt     = cmd.Parameters.Add("$ext",     SqliteType.Text);
            var pSize    = cmd.Parameters.Add("$size",    SqliteType.Integer);
            var pCreated = cmd.Parameters.Add("$created", SqliteType.Text);
            var pMod     = cmd.Parameters.Add("$modified",SqliteType.Text);
            var pAccess  = cmd.Parameters.Add("$accessed",SqliteType.Text);
            var pAttrs   = cmd.Parameters.Add("$attrs",   SqliteType.Integer);
            var pCat     = cmd.Parameters.Add("$cat",     SqliteType.Text);
            var pReparse = cmd.Parameters.Add("$reparse", SqliteType.Integer);

            foreach (var e in entries)
            {
                pSid.Value     = e.SessionId;
                pPath.Value    = e.FullPath;
                pName.Value    = e.FileName;
                pExt.Value     = e.Extension;
                pSize.Value    = e.SizeBytes;
                pCreated.Value = e.CreatedUtc.ToString("O");
                pMod.Value     = e.ModifiedUtc.ToString("O");
                pAccess.Value  = e.AccessedUtc.ToString("O");
                pAttrs.Value   = (int)e.Attributes;
                pCat.Value     = e.Category.ToString();
                pReparse.Value = e.IsReparsePoint ? 1 : 0;
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
            var conn = await _db.GetConnectionAsync(ct);
            using var tx  = await conn.BeginTransactionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;

            // INSERT OR REPLACE handles the upsert. The UNIQUE constraint on (SessionId, FullPath)
            // ensures duplicate paths from concurrent workers are merged.
            cmd.CommandText = """
                INSERT INTO FolderEntries
                    (SessionId, FullPath, FolderName, DirectSizeBytes, TotalSizeBytes,
                     FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied)
                VALUES
                    ($sid, $path, $name, $direct, $total,
                     $files, $subs, $reparse, $denied)
                ON CONFLICT(SessionId, FullPath) DO UPDATE SET
                    DirectSizeBytes = DirectSizeBytes + excluded.DirectSizeBytes,
                    TotalSizeBytes  = TotalSizeBytes  + excluded.TotalSizeBytes,
                    FileCount       = FileCount       + excluded.FileCount;
                """;

            var pSid     = cmd.Parameters.Add("$sid",    SqliteType.Integer);
            var pPath    = cmd.Parameters.Add("$path",   SqliteType.Text);
            var pName    = cmd.Parameters.Add("$name",   SqliteType.Text);
            var pDirect  = cmd.Parameters.Add("$direct", SqliteType.Integer);
            var pTotal   = cmd.Parameters.Add("$total",  SqliteType.Integer);
            var pFiles   = cmd.Parameters.Add("$files",  SqliteType.Integer);
            var pSubs    = cmd.Parameters.Add("$subs",   SqliteType.Integer);
            var pReparse = cmd.Parameters.Add("$reparse",SqliteType.Integer);
            var pDenied  = cmd.Parameters.Add("$denied", SqliteType.Integer);

            foreach (var e in entries)
            {
                pSid.Value     = e.SessionId;
                pPath.Value    = e.FullPath;
                pName.Value    = e.FolderName;
                pDirect.Value  = e.DirectSizeBytes;
                pTotal.Value   = e.TotalSizeBytes;
                pFiles.Value   = e.FileCount;
                pSubs.Value    = e.SubFolderCount;
                pReparse.Value = e.IsReparsePoint ? 1 : 0;
                pDenied.Value  = e.WasAccessDenied ? 1 : 0;
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
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM FileEntries
            WHERE SessionId = $sid
            ORDER BY SizeBytes DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$n",   topN);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FileEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFileEntry(reader));
        return list;
    }

    public async Task<IReadOnlyList<FolderEntry>> GetLargestFoldersAsync(
        long sessionId, int topN, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM FolderEntries
            WHERE SessionId = $sid
            ORDER BY TotalSizeBytes DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$n",   topN);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FolderEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFolderEntry(reader));
        return list;
    }

    public async Task<IReadOnlyDictionary<FileTypeCategory, (long Count, long Bytes)>> GetCategoryBreakdownAsync(
        long sessionId, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
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
            var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ScanSessions WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", sessionId);
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
        var conn = await _db.GetConnectionAsync(ct);
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

    public async Task UpdateFolderTotalsAsync(
        long sessionId,
        IReadOnlyDictionary<string, long> pathToTotal,
        CancellationToken ct = default)
    {
        if (pathToTotal.Count == 0) return;

        var conn = await _db.GetConnectionAsync(ct);
        const int batchSize = 500;
        var pairs = pathToTotal.ToList();

        for (int offset = 0; offset < pairs.Count; offset += batchSize)
        {
            var batch = pairs.Skip(offset).Take(batchSize).ToList();

            await _db.WriteLock.WaitAsync(ct);
            try
            {
                using var tx  = await conn.BeginTransactionAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    UPDATE FolderEntries
                    SET TotalSizeBytes = $total
                    WHERE SessionId = $sid AND FullPath = $path;
                    """;
                var pTotal = cmd.Parameters.Add("$total", SqliteType.Integer);
                var pSid   = cmd.Parameters.Add("$sid",   SqliteType.Integer);
                var pPath  = cmd.Parameters.Add("$path",  SqliteType.Text);
                pSid.Value = sessionId;

                foreach (var (path, total) in batch)
                {
                    pPath.Value  = path;
                    pTotal.Value = total;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            finally
            {
                _db.WriteLock.Release();
            }
        }
    }

    // ── Mapping helpers ────────────────────────────────────────────────────

    private static ScanSession ReadSession(SqliteDataReader r) => new()
    {
        Id                = r.GetInt64(r.GetOrdinal("Id")),
        RootPath          = r.GetString(r.GetOrdinal("RootPath")),
        StartedUtc        = DateTime.Parse(r.GetString(r.GetOrdinal("StartedUtc"))),
        CompletedUtc      = r.IsDBNull(r.GetOrdinal("CompletedUtc")) ? null
                            : DateTime.Parse(r.GetString(r.GetOrdinal("CompletedUtc"))),
        Status            = Enum.Parse<ScanStatus>(r.GetString(r.GetOrdinal("Status"))),
        TotalSizeBytes    = r.GetInt64(r.GetOrdinal("TotalSizeBytes")),
        TotalFiles        = r.GetInt64(r.GetOrdinal("TotalFiles")),
        TotalFolders      = r.GetInt64(r.GetOrdinal("TotalFolders")),
        AccessDeniedCount = r.GetInt64(r.GetOrdinal("AccessDeniedCount")),
        ErrorMessage      = r.IsDBNull(r.GetOrdinal("ErrorMessage")) ? null
                            : r.GetString(r.GetOrdinal("ErrorMessage")),
    };

    private static FileEntry ReadFileEntry(SqliteDataReader r) => new()
    {
        Id             = r.GetInt64(r.GetOrdinal("Id")),
        SessionId      = r.GetInt64(r.GetOrdinal("SessionId")),
        FullPath       = r.GetString(r.GetOrdinal("FullPath")),
        FileName       = r.GetString(r.GetOrdinal("FileName")),
        Extension      = r.GetString(r.GetOrdinal("Extension")),
        SizeBytes      = r.GetInt64(r.GetOrdinal("SizeBytes")),
        CreatedUtc     = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedUtc"))),
        ModifiedUtc    = DateTime.Parse(r.GetString(r.GetOrdinal("ModifiedUtc"))),
        AccessedUtc    = DateTime.Parse(r.GetString(r.GetOrdinal("AccessedUtc"))),
        Attributes     = (FileAttributes)r.GetInt32(r.GetOrdinal("Attributes")),
        Category       = Enum.TryParse<FileTypeCategory>(r.GetString(r.GetOrdinal("Category")), out var cat)
                         ? cat : FileTypeCategory.Unknown,
        IsReparsePoint = r.GetInt32(r.GetOrdinal("IsReparsePoint")) == 1,
    };

    private static FolderEntry ReadFolderEntry(SqliteDataReader r) => new()
    {
        Id              = r.GetInt64(r.GetOrdinal("Id")),
        SessionId       = r.GetInt64(r.GetOrdinal("SessionId")),
        FullPath        = r.GetString(r.GetOrdinal("FullPath")),
        FolderName      = r.GetString(r.GetOrdinal("FolderName")),
        DirectSizeBytes = r.GetInt64(r.GetOrdinal("DirectSizeBytes")),
        TotalSizeBytes  = r.GetInt64(r.GetOrdinal("TotalSizeBytes")),
        FileCount       = r.GetInt32(r.GetOrdinal("FileCount")),
        SubFolderCount  = r.GetInt32(r.GetOrdinal("SubFolderCount")),
        IsReparsePoint  = r.GetInt32(r.GetOrdinal("IsReparsePoint")) == 1,
        WasAccessDenied = r.GetInt32(r.GetOrdinal("WasAccessDenied")) == 1,
    };
}
