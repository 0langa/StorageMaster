using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Storage.Repositories;

public sealed class DuplicateRepository(StorageDbContext db) : IDuplicateRepository, IDuplicateCandidateProvider
{
    private readonly StorageDbContext _db = db;

    public async Task<DuplicateRun> CreateRunAsync(DuplicateScanOptions options, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DuplicateRuns
                    (SessionId, StartedUtc, Status, ConfigJson)
                VALUES
                    ($sid, $started, $status, $config);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$sid", options.SessionId);
            cmd.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$status", DuplicateRunStatus.Running.ToString());
            cmd.Parameters.AddWithValue("$config", JsonSerializer.Serialize(options));
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

            return new DuplicateRun
            {
                Id = id,
                SessionId = options.SessionId,
                StartedUtc = DateTime.UtcNow,
                Status = DuplicateRunStatus.Running,
                ConfigJson = JsonSerializer.Serialize(options),
            };
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task CompleteRunAsync(
        long runId,
        DuplicateRunStatus status,
        long candidateCount,
        long groupCount,
        long exactBytes,
        long reclaimableBytes,
        long errorCount,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE DuplicateRuns
                SET CompletedUtc = $completed,
                    Status = $status,
                    CandidateCount = $candidates,
                    GroupCount = $groups,
                    ExactBytes = $exactBytes,
                    ReclaimableBytes = $reclaimable,
                    ErrorCount = $errors,
                    ErrorMessage = $error
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$candidates", candidateCount);
            cmd.Parameters.AddWithValue("$groups", groupCount);
            cmd.Parameters.AddWithValue("$exactBytes", exactBytes);
            cmd.Parameters.AddWithValue("$reclaimable", reclaimableBytes);
            cmd.Parameters.AddWithValue("$errors", errorCount);
            cmd.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", runId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task SaveResultsAsync(
        long runId,
        IReadOnlyList<DuplicateSignature> signatures,
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyList<DuplicateGroupMember> members,
        IReadOnlyList<DuplicateError> errors,
        CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            var conn = await _db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var signature in signatures)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO DuplicateSignatures
                        (SessionId, FileEntryId, Method, Algorithm, SignatureBlob, SignatureText, MetadataJson, ComputedUtc, Status, ErrorMessage)
                    VALUES
                        ($sid, $file, $method, $algorithm, $blob, $text, $meta, $computed, $status, $error)
                    ON CONFLICT(FileEntryId, Method, Algorithm) DO UPDATE SET
                        SignatureBlob = excluded.SignatureBlob,
                        SignatureText = excluded.SignatureText,
                        MetadataJson = excluded.MetadataJson,
                        ComputedUtc = excluded.ComputedUtc,
                        Status = excluded.Status,
                        ErrorMessage = excluded.ErrorMessage;
                    """;
                cmd.Parameters.AddWithValue("$sid", signature.SessionId);
                cmd.Parameters.AddWithValue("$file", signature.FileEntryId);
                cmd.Parameters.AddWithValue("$method", signature.Method.ToString());
                cmd.Parameters.AddWithValue("$algorithm", signature.Algorithm);
                cmd.Parameters.AddWithValue("$blob", (object?)signature.SignatureBlob ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$text", (object?)signature.SignatureText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$meta", (object?)signature.MetadataJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$computed", signature.ComputedUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$status", signature.Status);
                cmd.Parameters.AddWithValue("$error", (object?)signature.ErrorMessage ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var groupIds = new Dictionary<long, long>();
            foreach (var group in groups)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO DuplicateGroups
                        (RunId, Method, Algorithm, Confidence, TotalBytes, ReclaimableBytes, RepresentativeFileEntryId)
                    VALUES
                        ($run, $method, $algorithm, $confidence, $total, $reclaimable, $rep);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$run", runId);
                cmd.Parameters.AddWithValue("$method", group.Method.ToString());
                cmd.Parameters.AddWithValue("$algorithm", group.Algorithm);
                cmd.Parameters.AddWithValue("$confidence", group.Confidence);
                cmd.Parameters.AddWithValue("$total", group.TotalBytes);
                cmd.Parameters.AddWithValue("$reclaimable", group.ReclaimableBytes);
                cmd.Parameters.AddWithValue("$rep", group.RepresentativeFileEntryId);
                groupIds[group.Id] = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }

            foreach (var member in members)
            {
                if (!groupIds.TryGetValue(member.GroupId, out var persistedGroupId))
                    continue;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO DuplicateGroupMembers
                        (GroupId, FileEntryId, FullPath, FileName, SizeBytes, ModifiedUtc, Score, IsKeeper, IsSelected, RecommendationReason, ExistsNow)
                    VALUES
                        ($group, $file, $path, $name, $size, $modified, $score, $keeper, $selected, $reason, $exists);
                    """;
                cmd.Parameters.AddWithValue("$group", persistedGroupId);
                cmd.Parameters.AddWithValue("$file", member.FileEntryId);
                cmd.Parameters.AddWithValue("$path", member.FullPath);
                cmd.Parameters.AddWithValue("$name", member.FileName);
                cmd.Parameters.AddWithValue("$size", member.SizeBytes);
                cmd.Parameters.AddWithValue("$modified", member.ModifiedUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$score", member.Score);
                cmd.Parameters.AddWithValue("$keeper", member.IsKeeper ? 1 : 0);
                cmd.Parameters.AddWithValue("$selected", member.IsSelected ? 1 : 0);
                cmd.Parameters.AddWithValue("$reason", member.RecommendationReason);
                cmd.Parameters.AddWithValue("$exists", member.ExistsNow ? 1 : 0);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var error in errors)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO DuplicateErrors
                        (RunId, FileEntryId, Path, ErrorType, Message, OccurredUtc)
                    VALUES
                        ($run, $file, $path, $type, $message, $occurred);
                    """;
                cmd.Parameters.AddWithValue("$run", runId);
                cmd.Parameters.AddWithValue("$file", (object?)error.FileEntryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$path", error.Path);
                cmd.Parameters.AddWithValue("$type", error.ErrorType);
                cmd.Parameters.AddWithValue("$message", error.Message);
                cmd.Parameters.AddWithValue("$occurred", error.OccurredUtc.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<DuplicateRun>> GetRunsForSessionAsync(long sessionId, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateRuns
            WHERE SessionId = $sid
            ORDER BY StartedUtc DESC;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DuplicateRun>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadRun(reader));
        return list;
    }

    public async Task<IReadOnlyList<DuplicateGroup>> GetGroupsForRunAsync(long runId, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateGroups
            WHERE RunId = $run
            ORDER BY ReclaimableBytes DESC, Id ASC;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DuplicateGroup>();
        while (await reader.ReadAsync(ct))
            list.Add(new DuplicateGroup
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                RunId = reader.GetInt64(reader.GetOrdinal("RunId")),
                Method = Enum.Parse<DuplicateMethod>(reader.GetString(reader.GetOrdinal("Method"))),
                Algorithm = reader.GetString(reader.GetOrdinal("Algorithm")),
                Confidence = reader.GetDouble(reader.GetOrdinal("Confidence")),
                TotalBytes = reader.GetInt64(reader.GetOrdinal("TotalBytes")),
                ReclaimableBytes = reader.GetInt64(reader.GetOrdinal("ReclaimableBytes")),
                RepresentativeFileEntryId = reader.GetInt64(reader.GetOrdinal("RepresentativeFileEntryId")),
            });
        return list;
    }

    public async Task<IReadOnlyList<DuplicateGroupMember>> GetMembersForGroupAsync(long groupId, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateGroupMembers
            WHERE GroupId = $group
            ORDER BY IsKeeper DESC, FullPath ASC;
            """;
        cmd.Parameters.AddWithValue("$group", groupId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DuplicateGroupMember>();
        while (await reader.ReadAsync(ct))
            list.Add(new DuplicateGroupMember
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                GroupId = reader.GetInt64(reader.GetOrdinal("GroupId")),
                FileEntryId = reader.GetInt64(reader.GetOrdinal("FileEntryId")),
                FullPath = reader.GetString(reader.GetOrdinal("FullPath")),
                FileName = reader.GetString(reader.GetOrdinal("FileName")),
                SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
                ModifiedUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("ModifiedUtc"))),
                Score = reader.GetDouble(reader.GetOrdinal("Score")),
                IsKeeper = reader.GetInt32(reader.GetOrdinal("IsKeeper")) == 1,
                IsSelected = reader.GetInt32(reader.GetOrdinal("IsSelected")) == 1,
                RecommendationReason = reader.GetString(reader.GetOrdinal("RecommendationReason")),
                ExistsNow = reader.GetInt32(reader.GetOrdinal("ExistsNow")) == 1,
            });
        return list;
    }

    public async Task MarkMembersDeletedAsync(IReadOnlyList<long> memberIds, CancellationToken ct = default)
    {
        if (memberIds.Count == 0)
            return;

        await _db.WriteLock.WaitAsync(ct);
        try
        {
            var conn = await _db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var memberId in memberIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    UPDATE DuplicateGroupMembers
                    SET ExistsNow = 0, IsSelected = 0
                    WHERE Id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", memberId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<DuplicateCandidate>> GetExactCandidatesAsync(
        DuplicateScanOptions options,
        CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        var filter = options.IncludeExtensions.Count > 0
            ? string.Join(',', options.IncludeExtensions.Select(extension => extension.Trim().ToLowerInvariant()))
            : string.Empty;
        var excluded = options.ExcludedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        var excludedSql = excluded.Length == 0
            ? string.Empty
            : string.Join(" ", excluded.Select((_, index) => $"AND FullPath NOT LIKE $excluded{index} || '%'"));

        cmd.CommandText = $"""
            SELECT *
            FROM FileEntries
            WHERE SessionId = $sid
              AND SizeBytes >= $minSize
              AND IsReparsePoint <= $allowReparse
              AND (
                    $extFilter = ''
                    OR instr(',' || $extFilter || ',', ',' || lower(Extension) || ',') > 0
                  )
              AND SizeBytes IN (
                    SELECT SizeBytes
                    FROM FileEntries
                    WHERE SessionId = $sid
                      AND SizeBytes >= $minSize
                    GROUP BY SizeBytes
                    HAVING COUNT(*) > 1
              )
              {excludedSql}
            ORDER BY SizeBytes DESC, FullPath ASC;
            """;
        cmd.Parameters.AddWithValue("$sid", options.SessionId);
        cmd.Parameters.AddWithValue("$minSize", options.MinimumSizeBytes);
        cmd.Parameters.AddWithValue("$allowReparse", options.IncludeReparsePoints ? 1 : 0);
        cmd.Parameters.AddWithValue("$extFilter", filter);
        for (var i = 0; i < excluded.Length; i++)
            cmd.Parameters.AddWithValue($"$excluded{i}", excluded[i]);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DuplicateCandidate>();
        while (await reader.ReadAsync(ct))
            list.Add(new DuplicateCandidate(ReadFileEntry(reader)));

        return list;
    }

    private static DuplicateRun ReadRun(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        SessionId = reader.GetInt64(reader.GetOrdinal("SessionId")),
        StartedUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("StartedUtc"))),
        CompletedUtc = reader.IsDBNull(reader.GetOrdinal("CompletedUtc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("CompletedUtc"))),
        Status = Enum.Parse<DuplicateRunStatus>(reader.GetString(reader.GetOrdinal("Status"))),
        ConfigJson = reader.GetString(reader.GetOrdinal("ConfigJson")),
        CandidateCount = reader.GetInt64(reader.GetOrdinal("CandidateCount")),
        GroupCount = reader.GetInt64(reader.GetOrdinal("GroupCount")),
        ExactBytes = reader.GetInt64(reader.GetOrdinal("ExactBytes")),
        ReclaimableBytes = reader.GetInt64(reader.GetOrdinal("ReclaimableBytes")),
        ErrorCount = reader.GetInt64(reader.GetOrdinal("ErrorCount")),
        ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
    };

    private static FileEntry ReadFileEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        SessionId = r.GetInt64(r.GetOrdinal("SessionId")),
        FullPath = r.GetString(r.GetOrdinal("FullPath")),
        FileName = r.GetString(r.GetOrdinal("FileName")),
        Extension = r.GetString(r.GetOrdinal("Extension")),
        SizeBytes = r.GetInt64(r.GetOrdinal("SizeBytes")),
        CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedUtc"))),
        ModifiedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("ModifiedUtc"))),
        AccessedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("AccessedUtc"))),
        Attributes = (FileAttributes)r.GetInt32(r.GetOrdinal("Attributes")),
        Category = Enum.TryParse<FileTypeCategory>(r.GetString(r.GetOrdinal("Category")), out var cat) ? cat : FileTypeCategory.Unknown,
        IsReparsePoint = r.GetInt32(r.GetOrdinal("IsReparsePoint")) == 1,
    };
}
