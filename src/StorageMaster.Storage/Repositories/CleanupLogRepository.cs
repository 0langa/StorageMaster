using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Storage.Repositories;

public sealed class CleanupLogRepository : ICleanupLogRepository
{
    private readonly StorageDbContext _db;

    public CleanupLogRepository(StorageDbContext db) => _db = db;

    public async Task LogResultAsync(CleanupResult result, CleanupSuggestion suggestion, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO CleanupLog
                    (SuggestionId, RuleId, Title, BytesFreed, WasDryRun, Status, ExecutedUtc, ErrorMessage, AuditDataJson)
                VALUES
                    ($sid, $rule, $title, $freed, $dry, $status, $executed, $error, $audit);
                """;
            cmd.Parameters.AddWithValue("$sid", result.SuggestionId.ToString());
            cmd.Parameters.AddWithValue("$rule", suggestion.RuleId);
            cmd.Parameters.AddWithValue("$title", suggestion.Title);
            cmd.Parameters.AddWithValue("$freed", result.BytesFreed);
            cmd.Parameters.AddWithValue("$dry", result.WasDryRun ? 1 : 0);
            cmd.Parameters.AddWithValue("$status", result.Status.ToString());
            cmd.Parameters.AddWithValue("$executed", result.ExecutedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$error", (object?)result.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$audit", (object?)BuildAuditJson(result, suggestion) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    /// <summary>
    /// Uses the suggestion's audit JSON verbatim; when files were quarantined the
    /// original→quarantine path pairs are wrapped in so the destinations survive
    /// in the append-only log and quarantined files stay recoverable.
    /// </summary>
    private static string? BuildAuditJson(CleanupResult result, CleanupSuggestion suggestion)
    {
        if (result.QuarantinedPaths.Count == 0)
            return suggestion.AuditDataJson;

        using var doc = suggestion.AuditDataJson is { Length: > 0 } existing
            ? System.Text.Json.JsonDocument.Parse(existing)
            : null;

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            QuarantinedFiles = result.QuarantinedPaths
                .Select(static move => new { move.OriginalPath, move.QuarantinePath }),
            SuggestionAudit = doc?.RootElement,
        });
    }

    public async Task<IReadOnlyList<CleanupLogEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM CleanupLog ORDER BY ExecutedUtc DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", count);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CleanupLogEntry>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CleanupLogEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                SuggestionId = Guid.Parse(reader.GetString(reader.GetOrdinal("SuggestionId"))),
                RuleId = reader.GetString(reader.GetOrdinal("RuleId")),
                Title = reader.GetString(reader.GetOrdinal("Title")),
                BytesFreed = reader.GetInt64(reader.GetOrdinal("BytesFreed")),
                WasDryRun = reader.GetInt32(reader.GetOrdinal("WasDryRun")) == 1,
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ExecutedUtc = UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("ExecutedUtc"))),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null
                               : reader.GetString(reader.GetOrdinal("ErrorMessage")),
                AuditDataJson = reader.IsDBNull(reader.GetOrdinal("AuditDataJson")) ? null
                               : reader.GetString(reader.GetOrdinal("AuditDataJson")),
            });
        }
        return list;
    }
}
