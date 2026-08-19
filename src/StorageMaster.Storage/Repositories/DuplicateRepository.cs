using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Storage.Repositories;

public sealed class DuplicateRepository(StorageDbContext db) : IDuplicateRepository, IDuplicateCandidateProvider
{
    private readonly StorageDbContext _db = db;

    public async Task<DuplicateRun> CreateRunAsync(DuplicateScanOptions options, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var startedUtc = DateTime.UtcNow;
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DuplicateRuns
                    (SessionId, StartedUtc, Status, ConfigJson)
                VALUES
                    ($sid, $started, $status, $config);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$sid", options.SessionId);
            cmd.Parameters.AddWithValue("$started", startedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$status", DuplicateRunStatus.Running.ToString());
            cmd.Parameters.AddWithValue("$config", JsonSerializer.Serialize(options));
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            return new DuplicateRun
            {
                Id = id,
                SessionId = options.SessionId,
                StartedUtc = startedUtc,
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
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
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
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            foreach (var signature in signatures)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO DuplicateSignatures
                        (SessionId, FileEntryId, Method, Algorithm,
                         AlgorithmVersion, SignatureBlob, SignatureText, MetadataJson,
                         ComputedUtc, Status, ErrorMessage,
                         SourceSizeBytes, SourceModifiedUtc, SourceFileIdentity)
                    VALUES
                        ($sid, $file, $method, $algorithm,
                         $algVer, $blob, $text, $meta,
                         $computed, $status, $error,
                         $srcSize, $srcMod, $srcIdent)
                    ON CONFLICT(FileEntryId, Method, Algorithm) DO UPDATE SET
                        AlgorithmVersion  = excluded.AlgorithmVersion,
                        SignatureBlob     = excluded.SignatureBlob,
                        SignatureText     = excluded.SignatureText,
                        MetadataJson      = excluded.MetadataJson,
                        ComputedUtc       = excluded.ComputedUtc,
                        Status            = excluded.Status,
                        ErrorMessage      = excluded.ErrorMessage,
                        SourceSizeBytes   = excluded.SourceSizeBytes,
                        SourceModifiedUtc = excluded.SourceModifiedUtc,
                        SourceFileIdentity = excluded.SourceFileIdentity;
                    """;
                cmd.Parameters.AddWithValue("$sid", signature.SessionId);
                cmd.Parameters.AddWithValue("$file", signature.FileEntryId);
                cmd.Parameters.AddWithValue("$method", signature.Method.ToString());
                cmd.Parameters.AddWithValue("$algorithm", signature.Algorithm);
                cmd.Parameters.AddWithValue("$algVer", signature.AlgorithmVersion);
                cmd.Parameters.AddWithValue("$blob", (object?)signature.SignatureBlob ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$text", (object?)signature.SignatureText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$meta", (object?)signature.MetadataJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$computed", signature.ComputedUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$status", signature.Status);
                cmd.Parameters.AddWithValue("$error", (object?)signature.ErrorMessage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$srcSize", signature.SourceSizeBytes);
                cmd.Parameters.AddWithValue("$srcMod", signature.SourceModifiedUtc == default
                                                            ? (object)DBNull.Value
                                                            : signature.SourceModifiedUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$srcIdent", (object?)signature.SourceFileIdentity ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                groupIds[group.Id] = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
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
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<DuplicateRun>> GetRunsForSessionAsync(long sessionId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateRuns
            WHERE SessionId = $sid
            ORDER BY StartedUtc DESC;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateRun>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadRun(reader));
        return list;
    }

    public async Task<DuplicateRunSummary> GetDuplicateRunSummaryAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) AS GroupCount,
                COALESCE(SUM(CASE WHEN Method = 'ExactSha256' THEN 1 ELSE 0 END), 0) AS ExactGroupCount,
                COALESCE(SUM(CASE WHEN Method <> 'ExactSha256' THEN 1 ELSE 0 END), 0) AS ReviewGroupCount,
                COALESCE(SUM(ReclaimableBytes), 0) AS ReclaimableBytes
            FROM DuplicateGroups
            WHERE RunId = $run;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var groupCount = 0L;
        var exactGroupCount = 0L;
        var reviewGroupCount = 0L;
        var reclaimableBytes = 0L;
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            groupCount = reader.GetInt64(reader.GetOrdinal("GroupCount"));
            exactGroupCount = reader.GetInt64(reader.GetOrdinal("ExactGroupCount"));
            reviewGroupCount = reader.GetInt64(reader.GetOrdinal("ReviewGroupCount"));
            reclaimableBytes = reader.GetInt64(reader.GetOrdinal("ReclaimableBytes"));
        }

        using var errCmd = conn.CreateCommand();
        errCmd.CommandText = "SELECT COUNT(*) FROM DuplicateErrors WHERE RunId = $run;";
        errCmd.Parameters.AddWithValue("$run", runId);
        var errorCount = Convert.ToInt64(await errCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

        return new DuplicateRunSummary
        {
            RunId = runId,
            GroupCount = groupCount,
            ExactGroupCount = exactGroupCount,
            ReviewGroupCount = reviewGroupCount,
            ReclaimableBytes = reclaimableBytes,
            ErrorCount = errorCount,
        };
    }

    public async Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsPageAsync(
        long runId,
        int page,
        int pageSize,
        DuplicateGroupQueryFilter? filters,
        DuplicateGroupSortBy sortBy,
        CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 500);
        var offset = (safePage - 1) * safePageSize;

        var whereParts = new List<string> { "g.RunId = $run" };
        var parameters = new List<(string Name, object Value)>
        {
            ("$run", runId),
        };

        if (filters is not null)
        {
            if (filters.Method is { } method)
            {
                whereParts.Add("g.Method = $method");
                parameters.Add(("$method", method.ToString()));
            }

            if (filters.MinConfidence is { } minConfidence)
            {
                whereParts.Add("g.Confidence >= $minConfidence");
                parameters.Add(("$minConfidence", minConfidence));
            }

            if (filters.HasSelectedMembers is { } selectedOnly)
            {
                whereParts.Add(selectedOnly
                    ? "EXISTS (SELECT 1 FROM DuplicateGroupMembers gm WHERE gm.GroupId = g.Id AND gm.IsSelected = 1 AND gm.IsKeeper = 0)"
                    : "NOT EXISTS (SELECT 1 FROM DuplicateGroupMembers gm WHERE gm.GroupId = g.Id AND gm.IsSelected = 1 AND gm.IsKeeper = 0)");
            }

            if (filters.ExistsNow is { } existsNow)
            {
                whereParts.Add(existsNow
                    ? "EXISTS (SELECT 1 FROM DuplicateGroupMembers gm WHERE gm.GroupId = g.Id AND gm.ExistsNow = 1)"
                    : "EXISTS (SELECT 1 FROM DuplicateGroupMembers gm WHERE gm.GroupId = g.Id AND gm.ExistsNow = 0)");
            }

            if (filters.IncludeErroredOnly)
            {
                whereParts.Add("""
                    EXISTS (
                        SELECT 1
                        FROM DuplicateGroupMembers gm
                        JOIN DuplicateErrors de
                          ON de.RunId = g.RunId
                         AND de.FileEntryId = gm.FileEntryId
                        WHERE gm.GroupId = g.Id
                    )
                    """);
            }

            if (!string.IsNullOrWhiteSpace(filters.SearchText))
            {
                whereParts.Add("""
                    EXISTS (
                        SELECT 1 FROM DuplicateGroupMembers gm
                        WHERE gm.GroupId = g.Id
                          AND (lower(gm.FileName) LIKE $search OR lower(gm.FullPath) LIKE $search)
                    )
                    """);
                parameters.Add(("$search", "%" + filters.SearchText.ToLowerInvariant() + "%"));
            }
        }

        var sortSql = sortBy switch
        {
            DuplicateGroupSortBy.ConfidenceDesc => "g.Confidence DESC, g.ReclaimableBytes DESC, g.Id ASC",
            DuplicateGroupSortBy.Method => "g.Method ASC, g.ReclaimableBytes DESC, g.Id ASC",
            DuplicateGroupSortBy.MemberCountDesc => "(SELECT COUNT(*) FROM DuplicateGroupMembers gm WHERE gm.GroupId = g.Id) DESC, g.ReclaimableBytes DESC, g.Id ASC",
            DuplicateGroupSortBy.LatestModifiedDesc => "(SELECT MAX(gm.ModifiedUtc) FROM DuplicateGroupMembers gm WHERE gm.GroupId = g.Id) DESC, g.ReclaimableBytes DESC, g.Id ASC",
            _ => "g.ReclaimableBytes DESC, g.Id ASC",
        };

        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT g.*
            FROM DuplicateGroups g
            WHERE {string.Join(" AND ", whereParts)}
            ORDER BY {sortSql}
            LIMIT $take OFFSET $skip;
            """;
        cmd.Parameters.AddWithValue("$take", safePageSize);
        cmd.Parameters.AddWithValue("$skip", offset);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateGroup>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

    public async Task<IReadOnlyList<DuplicateError>> GetDuplicateErrorsPageAsync(
        long runId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 500);
        var offset = (safePage - 1) * safePageSize;

        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateErrors
            WHERE RunId = $run
            ORDER BY OccurredUtc DESC, Id DESC
            LIMIT $take OFFSET $skip;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$take", safePageSize);
        cmd.Parameters.AddWithValue("$skip", offset);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateError>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DuplicateError
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                RunId = reader.GetInt64(reader.GetOrdinal("RunId")),
                FileEntryId = reader.IsDBNull(reader.GetOrdinal("FileEntryId"))
                                ? null
                                : reader.GetInt64(reader.GetOrdinal("FileEntryId")),
                Path = reader.GetString(reader.GetOrdinal("Path")),
                ErrorType = reader.GetString(reader.GetOrdinal("ErrorType")),
                Message = reader.GetString(reader.GetOrdinal("Message")),
                OccurredUtc = UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("OccurredUtc"))),
            });
        return list;
    }

    public async Task<IReadOnlyList<DuplicateGroup>> GetGroupsForRunAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateGroups
            WHERE RunId = $run
            ORDER BY ReclaimableBytes DESC, Id ASC;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateGroup>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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
        return await GetDuplicateGroupMembersAsync(groupId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DuplicateGroupMember>> GetDuplicateGroupMembersAsync(long groupId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT gm.*,
                   fe.Attributes AS SourceAttributes,
                   fe.IdentityVolumeSerial,
                   fe.IdentityFileIndex
            FROM DuplicateGroupMembers gm
            INNER JOIN FileEntries fe ON fe.Id = gm.FileEntryId
            WHERE gm.GroupId = $group
            ORDER BY gm.IsKeeper DESC, gm.FullPath ASC;
            """;
        cmd.Parameters.AddWithValue("$group", groupId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateGroupMember>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DuplicateGroupMember
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                GroupId = reader.GetInt64(reader.GetOrdinal("GroupId")),
                FileEntryId = reader.GetInt64(reader.GetOrdinal("FileEntryId")),
                FullPath = reader.GetString(reader.GetOrdinal("FullPath")),
                FileName = reader.GetString(reader.GetOrdinal("FileName")),
                SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
                ModifiedUtc = UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("ModifiedUtc"))),
                Attributes = (FileAttributes)reader.GetInt32(reader.GetOrdinal("SourceAttributes")),
                Identity = ReadFileIdentity(reader),
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

        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    // ── IDuplicateCandidateProvider ───────────────────────────────────────────

    public async Task<IReadOnlyList<DuplicateCandidate>> GetCandidatesAsync(
        DuplicateCandidateQuery query,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();

        // Extension filter: comma-joined list checked with instr trick
        var extFilter = query.Extensions.Count > 0
            ? string.Join(',', query.Extensions.Select(e => e.Trim().ToLowerInvariant()))
            : string.Empty;

        var categoryParameters = new List<string>();
        for (var i = 0; i < query.Categories.Count; i++)
            categoryParameters.Add($"$cat{i}");
        var categoryFilterSql = categoryParameters.Count == 0
            ? string.Empty
            : $"AND Category IN ({string.Join(", ", categoryParameters)})";

        // Path scopes are exact paths or separator-bounded descendants. Using
        // substr instead of LIKE keeps %, _ and sibling prefixes literal.
        var excluded = query.ExcludedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(CreatePathScope)
            .DistinctBy(static scope => scope.ExactPath, StringComparer.Ordinal)
            .ToArray();
        var excludedSql = excluded.Length == 0
            ? string.Empty
            : string.Join(" ", excluded.Select((_, i) =>
                $"AND NOT (NormalizedFullPath = $excl{i} OR substr(NormalizedFullPath, 1, length($exclPrefix{i})) = $exclPrefix{i})"));

        var included = query.IncludedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(CreatePathScope)
            .DistinctBy(static scope => scope.ExactPath, StringComparer.Ordinal)
            .ToArray();
        var includedSql = included.Length == 0
            ? string.Empty
            : "AND (" + string.Join(" OR ", included.Select((_, i) =>
                $"(NormalizedFullPath = $incl{i} OR substr(NormalizedFullPath, 1, length($inclPrefix{i})) = $inclPrefix{i})")) + ")";

        // Same-size-bucket subquery — omitted for fuzzy/normalized strategies
        var sizeBucketSql = query.RequireSameSizeBucket
            ? """
              AND SizeBytes IN (
                    SELECT SizeBytes
                    FROM FileEntries
                    WHERE SessionId = $sid
                      AND SizeBytes >= $minSize
                    GROUP BY SizeBytes
                    HAVING COUNT(*) > 1
              )
              """
            : string.Empty;

        cmd.CommandText = $"""
            SELECT *
            FROM FileEntries
            WHERE SessionId = $sid
              AND SizeBytes >= $minSize
              AND IsReparsePoint <= $allowReparse
              AND (
                    $includeHidden = 1
                    OR (Attributes & $hiddenMask) = 0
                  )
              AND (
                    $extFilter = ''
                    OR instr(',' || $extFilter || ',', ',' || lower(Extension) || ',') > 0
                  )
              {categoryFilterSql}
              {includedSql}
              {sizeBucketSql}
              {excludedSql}
            ORDER BY SizeBytes DESC, FullPath ASC;
            """;

        cmd.Parameters.AddWithValue("$sid", query.SessionId);
        cmd.Parameters.AddWithValue("$minSize", query.MinimumSizeBytes);
        cmd.Parameters.AddWithValue("$allowReparse", query.IncludeReparsePoints ? 1 : 0);
        cmd.Parameters.AddWithValue("$includeHidden", query.IncludeHiddenFiles ? 1 : 0);
        cmd.Parameters.AddWithValue("$hiddenMask", (int)(FileAttributes.Hidden | FileAttributes.System));
        cmd.Parameters.AddWithValue("$extFilter", extFilter);
        for (var i = 0; i < query.Categories.Count; i++)
            cmd.Parameters.AddWithValue($"$cat{i}", query.Categories[i].ToString());
        for (var i = 0; i < included.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$incl{i}", included[i].ExactPath);
            cmd.Parameters.AddWithValue($"$inclPrefix{i}", included[i].DescendantPrefix);
        }
        for (var i = 0; i < excluded.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$excl{i}", excluded[i].ExactPath);
            cmd.Parameters.AddWithValue($"$exclPrefix{i}", excluded[i].DescendantPrefix);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateCandidate>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var file = ReadFileEntry(reader);
            list.Add(new DuplicateCandidate(file, file.Identity));
        }
        return list;
    }

    // ── IDuplicateRepository — errors ─────────────────────────────────────────

    public async Task<IReadOnlyList<DuplicateError>> GetErrorsForRunAsync(
        long runId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateErrors
            WHERE RunId = $run
            ORDER BY OccurredUtc ASC;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateError>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DuplicateError
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                RunId = reader.GetInt64(reader.GetOrdinal("RunId")),
                FileEntryId = reader.IsDBNull(reader.GetOrdinal("FileEntryId"))
                                ? null
                                : reader.GetInt64(reader.GetOrdinal("FileEntryId")),
                Path = reader.GetString(reader.GetOrdinal("Path")),
                ErrorType = reader.GetString(reader.GetOrdinal("ErrorType")),
                Message = reader.GetString(reader.GetOrdinal("Message")),
                OccurredUtc = UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("OccurredUtc"))),
            });
        return list;
    }

    // ── IDuplicateRepository — signature cache ────────────────────────────────

    public async Task<IReadOnlyList<DuplicateSignature>> GetCachedSignaturesAsync(
        long sessionId,
        DuplicateMethod method,
        string algorithm,
        int algorithmVersion,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM DuplicateSignatures
            WHERE SessionId        = $sid
              AND Method           = $method
              AND Algorithm        = $algorithm
              AND AlgorithmVersion = $algVer
              AND Status           = 'Ready';
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$method", method.ToString());
        cmd.Parameters.AddWithValue("$algorithm", algorithm);
        cmd.Parameters.AddWithValue("$algVer", algorithmVersion);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateSignature>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadSignature(reader));
        return list;
    }

    // ── IDuplicateRepository — quarantine ─────────────────────────────────────

    public async Task<QuarantinedFile> RecordQuarantineAsync(
        long? memberId,
        long runId,
        string originalPath,
        string quarantinePath,
        CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO QuarantinedFiles
                    (MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc)
                VALUES
                    ($member, $run, $orig, $qpath, $qutc);
                SELECT last_insert_rowid();
                """;
            var now = DateTime.UtcNow;
            cmd.Parameters.AddWithValue("$member", (object?)memberId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$run", runId);
            cmd.Parameters.AddWithValue("$orig", originalPath);
            cmd.Parameters.AddWithValue("$qpath", quarantinePath);
            cmd.Parameters.AddWithValue("$qutc", now.ToString("O"));
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            return new QuarantinedFile
            {
                Id = id,
                MemberId = memberId,
                RunId = runId,
                OriginalPath = originalPath,
                QuarantinePath = quarantinePath,
                QuarantinedUtc = now,
            };
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<QuarantinedFile>> GetQuarantinedFilesAsync(
        long runId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM QuarantinedFiles
            WHERE RunId = $run
            ORDER BY QuarantinedUtc ASC;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<QuarantinedFile>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadQuarantinedFile(reader));
        return list;
    }

    public async Task<IReadOnlyList<QuarantinedFile>> GetUnrestoredQuarantinedFilesAsync(
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM QuarantinedFiles
            WHERE RestoredUtc IS NULL
            ORDER BY QuarantinedUtc DESC;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<QuarantinedFile>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadQuarantinedFile(reader));
        return list;
    }

    public async Task<QuarantinedFile?> GetQuarantinedFileAsync(
        long quarantineId, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM QuarantinedFiles
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", quarantineId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadQuarantinedFile(reader)
            : null;
    }

    public async Task MarkRestoredAsync(
        long quarantineId,
        string restoredPath,
        CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE QuarantinedFiles
                SET RestoredUtc  = $utc,
                    RestoredPath = $path
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$path", restoredPath);
            cmd.Parameters.AddWithValue("$id", quarantineId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    // ── IDuplicateRepository — recovery journal ─────────────────────────────

    public async Task<DuplicateOperationJournalEntry> RecordDuplicateOperationIntentAsync(
        DuplicateOperationJournalEntry entry,
        CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DuplicateOperationJournal
                    (OperationId, Kind, Status, RunId, GroupId, MemberId, QuarantineId,
                     Method, SourcePath, SourceIdentity, DestinationPath, SourceSizeBytes,
                     SourceModifiedUtc, PlannedUtc, CompletedUtc, BytesFreed, ErrorMessage, MetadataJson)
                VALUES
                    ($operation, $kind, $status, $run, $group, $member, $quarantine,
                     $method, $source, $identity, $destination, $size,
                     $modified, $planned, $completed, $bytes, $error, $metadata);
                SELECT last_insert_rowid();
                """;
            AddJournalParameters(cmd, entry);
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            return entry with { Id = id };
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task UpdateDuplicateOperationOutcomeAsync(
        long journalId,
        DuplicateOperationStatus status,
        string? destinationPath,
        long? bytesFreed,
        string? errorMessage,
        CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE DuplicateOperationJournal
                SET Status = $status,
                    DestinationPath = COALESCE($destination, DestinationPath),
                    CompletedUtc = $completed,
                    BytesFreed = $bytes,
                    ErrorMessage = $error
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$destination", (object?)destinationPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$bytes", (object?)bytesFreed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", journalId);
            var updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (updated != 1)
                throw new InvalidOperationException($"Duplicate operation journal {journalId} was not found.");
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<QuarantinedFile> CompleteQuarantineMoveAsync(
        long journalId,
        long? memberId,
        long runId,
        string originalPath,
        string quarantinePath,
        long bytesFreed,
        CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var completedUtc = DateTime.UtcNow;

            using (var journalCommand = conn.CreateCommand())
            {
                journalCommand.Transaction = tx;
                journalCommand.CommandText = """
                    UPDATE DuplicateOperationJournal
                    SET Status = $status,
                        DestinationPath = $destination,
                        CompletedUtc = $completed,
                        BytesFreed = $bytes,
                        ErrorMessage = NULL
                    WHERE Id = $id
                      AND Kind = $kind;
                    """;
                journalCommand.Parameters.AddWithValue("$status", DuplicateOperationStatus.Quarantined.ToString());
                journalCommand.Parameters.AddWithValue("$destination", quarantinePath);
                journalCommand.Parameters.AddWithValue("$completed", completedUtc.ToString("O"));
                journalCommand.Parameters.AddWithValue("$bytes", bytesFreed);
                journalCommand.Parameters.AddWithValue("$id", journalId);
                journalCommand.Parameters.AddWithValue("$kind", DuplicateOperationKind.Delete.ToString());
                var updated = await journalCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (updated != 1)
                    throw new InvalidOperationException($"Duplicate operation journal {journalId} was not found.");
            }

            long? quarantineId = null;
            DateTime quarantinedUtc = completedUtc;
            using (var findCommand = conn.CreateCommand())
            {
                findCommand.Transaction = tx;
                findCommand.CommandText = """
                    SELECT Id, QuarantinedUtc
                    FROM QuarantinedFiles
                    WHERE MemberId IS $member
                      AND RunId = $run
                      AND OriginalPath = $original
                      AND QuarantinePath = $quarantine
                    ORDER BY Id DESC
                    LIMIT 1;
                    """;
                findCommand.Parameters.AddWithValue("$member", (object?)memberId ?? DBNull.Value);
                findCommand.Parameters.AddWithValue("$run", runId);
                findCommand.Parameters.AddWithValue("$original", originalPath);
                findCommand.Parameters.AddWithValue("$quarantine", quarantinePath);
                using var reader = await findCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    quarantineId = reader.GetInt64(0);
                    quarantinedUtc = UtcTimestamp.Parse(reader.GetString(1));
                }
            }

            if (quarantineId is null)
            {
                using var quarantineCommand = conn.CreateCommand();
                quarantineCommand.Transaction = tx;
                quarantineCommand.CommandText = """
                    INSERT INTO QuarantinedFiles
                        (MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc)
                    VALUES
                        ($member, $run, $original, $quarantine, $utc);
                    SELECT last_insert_rowid();
                    """;
                quarantineCommand.Parameters.AddWithValue("$member", (object?)memberId ?? DBNull.Value);
                quarantineCommand.Parameters.AddWithValue("$run", runId);
                quarantineCommand.Parameters.AddWithValue("$original", originalPath);
                quarantineCommand.Parameters.AddWithValue("$quarantine", quarantinePath);
                quarantineCommand.Parameters.AddWithValue("$utc", quarantinedUtc.ToString("O"));
                quarantineId = Convert.ToInt64(await quarantineCommand.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new QuarantinedFile
            {
                Id = quarantineId.Value,
                MemberId = memberId,
                RunId = runId,
                OriginalPath = originalPath,
                QuarantinePath = quarantinePath,
                QuarantinedUtc = quarantinedUtc,
            };
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<DuplicateOperationJournalEntry>> GetDuplicateOperationJournalAsync(
        long runId,
        CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM DuplicateOperationJournal
            WHERE RunId = $run
            ORDER BY PlannedUtc ASC, Id ASC;
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DuplicateOperationJournalEntry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadJournalEntry(reader));
        return list;
    }

    // ── Readers ───────────────────────────────────────────────────────────────

    private static void AddJournalParameters(SqliteCommand cmd, DuplicateOperationJournalEntry entry)
    {
        cmd.Parameters.AddWithValue("$operation", entry.OperationId.ToString("N"));
        cmd.Parameters.AddWithValue("$kind", entry.Kind.ToString());
        cmd.Parameters.AddWithValue("$status", entry.Status.ToString());
        cmd.Parameters.AddWithValue("$run", entry.RunId);
        cmd.Parameters.AddWithValue("$group", (object?)entry.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$member", (object?)entry.MemberId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$quarantine", (object?)entry.QuarantineId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$method", entry.Method.ToString());
        cmd.Parameters.AddWithValue("$source", entry.SourcePath);
        cmd.Parameters.AddWithValue("$identity", (object?)entry.SourceIdentity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$destination", (object?)entry.DestinationPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", entry.SourceSizeBytes);
        cmd.Parameters.AddWithValue("$modified", entry.SourceModifiedUtc is null ? DBNull.Value : entry.SourceModifiedUtc.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$planned", entry.PlannedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", entry.CompletedUtc is null ? DBNull.Value : entry.CompletedUtc.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$bytes", (object?)entry.BytesFreed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", (object?)entry.MetadataJson ?? DBNull.Value);
    }

    private static DuplicateOperationJournalEntry ReadJournalEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        OperationId = Guid.Parse(r.GetString(r.GetOrdinal("OperationId"))),
        Kind = Enum.Parse<DuplicateOperationKind>(r.GetString(r.GetOrdinal("Kind"))),
        Status = Enum.Parse<DuplicateOperationStatus>(r.GetString(r.GetOrdinal("Status"))),
        RunId = r.GetInt64(r.GetOrdinal("RunId")),
        GroupId = r.IsDBNull(r.GetOrdinal("GroupId")) ? null : r.GetInt64(r.GetOrdinal("GroupId")),
        MemberId = r.IsDBNull(r.GetOrdinal("MemberId")) ? null : r.GetInt64(r.GetOrdinal("MemberId")),
        QuarantineId = r.IsDBNull(r.GetOrdinal("QuarantineId")) ? null : r.GetInt64(r.GetOrdinal("QuarantineId")),
        Method = Enum.Parse<DeletionMethod>(r.GetString(r.GetOrdinal("Method"))),
        SourcePath = r.GetString(r.GetOrdinal("SourcePath")),
        SourceIdentity = r.IsDBNull(r.GetOrdinal("SourceIdentity")) ? null : r.GetString(r.GetOrdinal("SourceIdentity")),
        DestinationPath = r.IsDBNull(r.GetOrdinal("DestinationPath")) ? null : r.GetString(r.GetOrdinal("DestinationPath")),
        SourceSizeBytes = r.GetInt64(r.GetOrdinal("SourceSizeBytes")),
        SourceModifiedUtc = r.IsDBNull(r.GetOrdinal("SourceModifiedUtc")) ? null : UtcTimestamp.Parse(r.GetString(r.GetOrdinal("SourceModifiedUtc"))),
        PlannedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("PlannedUtc"))),
        CompletedUtc = r.IsDBNull(r.GetOrdinal("CompletedUtc")) ? null : UtcTimestamp.Parse(r.GetString(r.GetOrdinal("CompletedUtc"))),
        BytesFreed = r.IsDBNull(r.GetOrdinal("BytesFreed")) ? null : r.GetInt64(r.GetOrdinal("BytesFreed")),
        ErrorMessage = r.IsDBNull(r.GetOrdinal("ErrorMessage")) ? null : r.GetString(r.GetOrdinal("ErrorMessage")),
        MetadataJson = r.IsDBNull(r.GetOrdinal("MetadataJson")) ? null : r.GetString(r.GetOrdinal("MetadataJson")),
    };

    private static DuplicateSignature ReadSignature(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        SessionId = r.GetInt64(r.GetOrdinal("SessionId")),
        FileEntryId = r.GetInt64(r.GetOrdinal("FileEntryId")),
        Method = Enum.Parse<DuplicateMethod>(r.GetString(r.GetOrdinal("Method"))),
        Algorithm = r.GetString(r.GetOrdinal("Algorithm")),
        AlgorithmVersion = r.GetInt32(r.GetOrdinal("AlgorithmVersion")),
        SignatureBlob = r.IsDBNull(r.GetOrdinal("SignatureBlob")) ? null : (byte[])r.GetValue(r.GetOrdinal("SignatureBlob")),
        SignatureText = r.IsDBNull(r.GetOrdinal("SignatureText")) ? null : r.GetString(r.GetOrdinal("SignatureText")),
        MetadataJson = r.IsDBNull(r.GetOrdinal("MetadataJson")) ? null : r.GetString(r.GetOrdinal("MetadataJson")),
        ComputedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("ComputedUtc"))),
        Status = r.GetString(r.GetOrdinal("Status")),
        ErrorMessage = r.IsDBNull(r.GetOrdinal("ErrorMessage")) ? null : r.GetString(r.GetOrdinal("ErrorMessage")),
        SourceSizeBytes = r.GetInt64(r.GetOrdinal("SourceSizeBytes")),
        SourceModifiedUtc = r.IsDBNull(r.GetOrdinal("SourceModifiedUtc")) || r.GetString(r.GetOrdinal("SourceModifiedUtc")) == ""
                             ? default
                             : UtcTimestamp.Parse(r.GetString(r.GetOrdinal("SourceModifiedUtc"))),
        SourceFileIdentity = r.IsDBNull(r.GetOrdinal("SourceFileIdentity")) ? null : r.GetString(r.GetOrdinal("SourceFileIdentity")),
    };

    private static QuarantinedFile ReadQuarantinedFile(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        MemberId = r.IsDBNull(r.GetOrdinal("MemberId")) ? null : r.GetInt64(r.GetOrdinal("MemberId")),
        RunId = r.GetInt64(r.GetOrdinal("RunId")),
        OriginalPath = r.GetString(r.GetOrdinal("OriginalPath")),
        QuarantinePath = r.GetString(r.GetOrdinal("QuarantinePath")),
        QuarantinedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("QuarantinedUtc"))),
        RestoredUtc = r.IsDBNull(r.GetOrdinal("RestoredUtc")) ? null : UtcTimestamp.Parse(r.GetString(r.GetOrdinal("RestoredUtc"))),
        RestoredPath = r.IsDBNull(r.GetOrdinal("RestoredPath")) ? null : r.GetString(r.GetOrdinal("RestoredPath")),
    };

    private static DuplicateRun ReadRun(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        SessionId = reader.GetInt64(reader.GetOrdinal("SessionId")),
        StartedUtc = UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("StartedUtc"))),
        CompletedUtc = reader.IsDBNull(reader.GetOrdinal("CompletedUtc")) ? null : UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("CompletedUtc"))),
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
        CreatedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("CreatedUtc"))),
        ModifiedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("ModifiedUtc"))),
        AccessedUtc = UtcTimestamp.Parse(r.GetString(r.GetOrdinal("AccessedUtc"))),
        Attributes = (FileAttributes)r.GetInt32(r.GetOrdinal("Attributes")),
        Category = Enum.TryParse<FileTypeCategory>(r.GetString(r.GetOrdinal("Category")), out var cat) ? cat : FileTypeCategory.Unknown,
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

    private static PathScope CreatePathScope(string path)
    {
        var exactPath = NormalizeForStorage(path);
        var descendantPrefix = Path.EndsInDirectorySeparator(exactPath)
            ? exactPath
            : exactPath + Path.DirectorySeparatorChar;
        return new PathScope(exactPath, descendantPrefix);
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

    private readonly record struct PathScope(string ExactPath, string DescendantPrefix);
}
