using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;
using StorageMaster.Core.SpaceMap;

namespace StorageMaster.Storage.Repositories;

public sealed class SpaceMapRepository : ISpaceMapRepository
{
    private readonly StorageDbContext _db;

    public SpaceMapRepository(StorageDbContext db) => _db = db;

    public async Task<IReadOnlyList<ScanSession>> GetSessionRootCandidatesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM ScanSessions
            WHERE Status = 'Completed'
            ORDER BY StartedUtc DESC
            LIMIT 100;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var sessions = new List<ScanSession>();
        while (await reader.ReadAsync(ct))
            sessions.Add(ReadSession(reader));
        return sessions;
    }

    public async Task<ScanSession?> GetPreviousComparableSessionAsync(
        long currentSessionId,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);

        ScanSession? current;
        using (var currentCmd = conn.CreateCommand())
        {
            currentCmd.CommandText = "SELECT * FROM ScanSessions WHERE Id = $id;";
            currentCmd.Parameters.AddWithValue("$id", currentSessionId);
            using var currentReader = await currentCmd.ExecuteReaderAsync(ct);
            current = await currentReader.ReadAsync(ct) ? ReadSession(currentReader) : null;
        }

        if (current is null)
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM ScanSessions
            WHERE Id <> $id
              AND Status = 'Completed'
              AND upper(RootPath) = $root
              AND StartedUtc < $started
            ORDER BY StartedUtc DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", currentSessionId);
        cmd.Parameters.AddWithValue("$root", NormalizeForStorage(current.RootPath));
        cmd.Parameters.AddWithValue("$started", current.StartedUtc.ToString("O"));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<SpaceMapNode>> GetFolderChildrenWithSizesAsync(
        long sessionId,
        string folderPath,
        SpaceMapNodeKind? kindFilter,
        long minimumSizeBytes,
        int limit,
        CancellationToken ct = default)
    {
        var normalizedFolder = NormalizePath(folderPath);
        var parentSize = await GetFolderSizeAsync(sessionId, normalizedFolder, ct);
        var results = new List<SpaceMapNode>();

        if (limit <= 0)
            return results;

        if (kindFilter is null or SpaceMapNodeKind.Folder)
        {
            var folders = await QueryDirectFoldersAsync(
                sessionId,
                normalizedFolder,
                parentSize,
                minimumSizeBytes,
                limit,
                ct);
            results.AddRange(folders);
        }

        if (kindFilter is null or SpaceMapNodeKind.File)
        {
            // Fetch the top N of each kind before applying the global top N.
            // Any item in the combined top N must be present in its kind's top N.
            var files = await QueryDirectFilesAsync(
                sessionId,
                normalizedFolder,
                parentSize,
                minimumSizeBytes,
                limit,
                ct);
            results.AddRange(files);
        }

        return results
            .OrderByDescending(static node => node.SizeBytes)
            .ThenBy(static node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<SpaceMapNode>> GetLargestFilesUnderFolderAsync(
        long sessionId,
        string folderPath,
        int limit,
        CancellationToken ct = default)
    {
        var folder = NormalizePath(folderPath);
        var prefix = ChildPrefix(folder);
        var prefixNorm = NormalizeForStorage(prefix);
        var parentSize = await GetFolderSizeAsync(sessionId, folder, ct);

        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, SessionId, FullPath, FileName, SizeBytes, ModifiedUtc, Category, IsReparsePoint
            FROM FileEntries
            WHERE SessionId = $sid
              AND substr(NormalizedFullPath, 1, length($prefixNorm)) = $prefixNorm
            ORDER BY SizeBytes DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$prefixNorm", prefixNorm);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpaceMapNode>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFileNode(reader, parentSize));
        return list;
    }

    public async Task<ScanDeltaSummary> GetScanDeltaAsync(
        long currentSessionId,
        long previousSessionId,
        int limit,
        CancellationToken ct = default)
    {
        return new ScanDeltaSummary
        {
            CurrentSessionId = currentSessionId,
            PreviousSessionId = previousSessionId,
            GrowingFolders = await QueryFolderDeltaAsync(currentSessionId, previousSessionId, growing: true, limit, ct),
            ShrinkingFolders = await QueryFolderDeltaAsync(currentSessionId, previousSessionId, growing: false, limit, ct),
            NewLargeFiles = await QueryFileAddRemoveDeltaAsync(currentSessionId, previousSessionId, added: true, limit, ct),
            RemovedFiles = await QueryFileAddRemoveDeltaAsync(currentSessionId, previousSessionId, added: false, limit, ct),
        };
    }

    private async Task<long> GetFolderSizeAsync(long sessionId, string folderPath, CancellationToken ct)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(
                (SELECT TotalSizeBytes FROM FolderEntries WHERE SessionId = $sid AND NormalizedFullPath = $path LIMIT 1),
                (SELECT TotalSizeBytes FROM ScanSessions WHERE Id = $sid),
                0);
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$path", NormalizeForStorage(folderPath));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<IReadOnlyList<SpaceMapNode>> QueryDirectFoldersAsync(
        long sessionId,
        string parentPath,
        long parentSize,
        long minimumSizeBytes,
        int limit,
        CancellationToken ct)
    {
        var prefix = ChildPrefix(parentPath);
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, SessionId, FullPath, FolderName, TotalSizeBytes, FileCount,
                   SubFolderCount, IsReparsePoint
            FROM FolderEntries
            WHERE SessionId = $sid
              AND ParentNormalizedPath = $parentNorm
              AND TotalSizeBytes >= $minBytes
            ORDER BY TotalSizeBytes DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$parentNorm", NormalizeForStorage(parentPath));
        cmd.Parameters.AddWithValue("$minBytes", minimumSizeBytes);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpaceMapNode>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new SpaceMapNode
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                SessionId = reader.GetInt64(reader.GetOrdinal("SessionId")),
                FullPath = reader.GetString(reader.GetOrdinal("FullPath")),
                DisplayName = reader.GetString(reader.GetOrdinal("FolderName")),
                Kind = SpaceMapNodeKind.Folder,
                SizeBytes = reader.GetInt64(reader.GetOrdinal("TotalSizeBytes")),
                ParentSizeBytes = parentSize,
                FileCount = reader.GetInt32(reader.GetOrdinal("FileCount")),
                FolderCount = reader.GetInt32(reader.GetOrdinal("SubFolderCount")),
                IsReparsePoint = reader.GetInt32(reader.GetOrdinal("IsReparsePoint")) == 1,
            });
        }
        return list;
    }

    private async Task<IReadOnlyList<SpaceMapNode>> QueryDirectFilesAsync(
        long sessionId,
        string parentPath,
        long parentSize,
        long minimumSizeBytes,
        int limit,
        CancellationToken ct)
    {
        var prefix = ChildPrefix(parentPath);
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, SessionId, FullPath, FileName, SizeBytes, ModifiedUtc, Category, IsReparsePoint
            FROM FileEntries
            WHERE SessionId = $sid
              AND NormalizedFullPath >= $prefixNorm
              AND NormalizedFullPath < $prefixUpper
              AND instr(substr(NormalizedFullPath, length($prefixNorm) + 1), '\') = 0
              AND SizeBytes >= $minBytes
            ORDER BY SizeBytes DESC
            LIMIT $limit;
            """;
        // NormalizeForStorage strips a trailing separator, so it cannot be used to
        // build a prefix: "C:\Root\" would come back as "C:\ROOT" and the range would
        // then also match siblings such as "C:\RootBackup". Normalise the parent and
        // re-attach the separator instead.
        var prefixNorm = NormalizedChildPrefix(parentPath);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$prefixNorm", prefixNorm);
        cmd.Parameters.AddWithValue("$prefixUpper", PrefixUpperBound(prefixNorm));
        cmd.Parameters.AddWithValue("$minBytes", minimumSizeBytes);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpaceMapNode>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFileNode(reader, parentSize));
        return list;
    }

    private async Task<IReadOnlyList<ScanDeltaItem>> QueryFolderDeltaAsync(
        long currentSessionId,
        long previousSessionId,
        bool growing,
        int limit,
        CancellationToken ct)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = growing
            ? """
              SELECT c.FullPath, c.FolderName, c.TotalSizeBytes AS CurrentBytes,
                     COALESCE(p.TotalSizeBytes, 0) AS PreviousBytes
              FROM FolderEntries c
              LEFT JOIN FolderEntries p
                ON p.SessionId = $prev AND p.NormalizedFullPath = c.NormalizedFullPath
              WHERE c.SessionId = $current
                AND c.TotalSizeBytes > COALESCE(p.TotalSizeBytes, 0)
              ORDER BY (c.TotalSizeBytes - COALESCE(p.TotalSizeBytes, 0)) DESC
              LIMIT $limit;
              """
            : """
              SELECT p.FullPath, p.FolderName, COALESCE(c.TotalSizeBytes, 0) AS CurrentBytes,
                     p.TotalSizeBytes AS PreviousBytes
              FROM FolderEntries p
              LEFT JOIN FolderEntries c
                ON c.SessionId = $current AND c.NormalizedFullPath = p.NormalizedFullPath
              WHERE p.SessionId = $prev
                AND p.TotalSizeBytes > COALESCE(c.TotalSizeBytes, 0)
              ORDER BY (p.TotalSizeBytes - COALESCE(c.TotalSizeBytes, 0)) DESC
              LIMIT $limit;
              """;
        cmd.Parameters.AddWithValue("$current", currentSessionId);
        cmd.Parameters.AddWithValue("$prev", previousSessionId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ScanDeltaItem>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadDeltaItem(reader, SpaceMapNodeKind.Folder));
        return list;
    }

    private async Task<IReadOnlyList<ScanDeltaItem>> QueryFileAddRemoveDeltaAsync(
        long currentSessionId,
        long previousSessionId,
        bool added,
        int limit,
        CancellationToken ct)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = added
            ? """
              SELECT c.FullPath, c.FileName AS DisplayName, c.SizeBytes AS CurrentBytes,
                     0 AS PreviousBytes
              FROM FileEntries c
              LEFT JOIN FileEntries p
                ON p.SessionId = $prev AND p.NormalizedFullPath = c.NormalizedFullPath
              WHERE c.SessionId = $current
                AND p.Id IS NULL
              ORDER BY c.SizeBytes DESC
              LIMIT $limit;
              """
            : """
              SELECT p.FullPath, p.FileName AS DisplayName, 0 AS CurrentBytes,
                     p.SizeBytes AS PreviousBytes
              FROM FileEntries p
              LEFT JOIN FileEntries c
                ON c.SessionId = $current AND c.NormalizedFullPath = p.NormalizedFullPath
              WHERE p.SessionId = $prev
                AND c.Id IS NULL
              ORDER BY p.SizeBytes DESC
              LIMIT $limit;
              """;
        cmd.Parameters.AddWithValue("$current", currentSessionId);
        cmd.Parameters.AddWithValue("$prev", previousSessionId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ScanDeltaItem>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadDeltaItem(reader, SpaceMapNodeKind.File));
        return list;
    }

    private static SpaceMapNode ReadFileNode(SqliteDataReader reader, long parentSize)
    {
        var category = Enum.TryParse<FileTypeCategory>(
            reader.GetString(reader.GetOrdinal("Category")),
            ignoreCase: true,
            out var parsed)
            ? parsed
            : FileTypeCategory.Unknown;

        return new SpaceMapNode
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SessionId = reader.GetInt64(reader.GetOrdinal("SessionId")),
            FullPath = reader.GetString(reader.GetOrdinal("FullPath")),
            DisplayName = reader.GetString(reader.GetOrdinal("FileName")),
            Kind = SpaceMapNodeKind.File,
            SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
            ParentSizeBytes = parentSize,
            FileCount = 1,
            FolderCount = 0,
            ModifiedUtc = UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("ModifiedUtc"))),
            Category = category,
            IsReparsePoint = reader.GetInt32(reader.GetOrdinal("IsReparsePoint")) == 1,
        };
    }

    private static ScanDeltaItem ReadDeltaItem(SqliteDataReader reader, SpaceMapNodeKind kind) => new()
    {
        FullPath = reader.GetString(reader.GetOrdinal("FullPath")),
        DisplayName = reader.GetString(reader.GetOrdinal(kind == SpaceMapNodeKind.File ? "DisplayName" : "FolderName")),
        Kind = kind,
        CurrentBytes = reader.GetInt64(reader.GetOrdinal("CurrentBytes")),
        PreviousBytes = reader.GetInt64(reader.GetOrdinal("PreviousBytes")),
    };

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

    private static string NormalizePath(string path)
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

    /// <summary>
    /// Storage-normalised path of <paramref name="folderPath"/> with a trailing
    /// separator, suitable as a prefix for descendant range scans.
    /// </summary>
    private static string NormalizedChildPrefix(string folderPath)
    {
        var normalized = NormalizeForStorage(folderPath);
        return normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Exclusive upper bound for a prefix range scan.
    /// <para>
    /// Rewriting <c>substr(col, 1, n) = prefix</c> as
    /// <c>col &gt;= prefix AND col &lt; bound</c> is what lets SQLite use the index on
    /// NormalizedFullPath. Applying <c>substr()</c> to the column defeats indexing
    /// outright, which made every Space Map navigation scan the whole session.
    /// </para>
    /// </summary>
    private static string PrefixUpperBound(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return prefix;

        // Stored paths compare with BINARY collation, so incrementing the final code
        // unit gives the first value the prefix no longer covers.
        var last = prefix[^1];
        return last == char.MaxValue
            ? prefix + '￿'
            : string.Concat(prefix.AsSpan(0, prefix.Length - 1), ((char)(last + 1)).ToString());
    }

    private static string ChildPrefix(string folderPath)
    {
        var normalized = NormalizePath(folderPath);
        return normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
    }
}
