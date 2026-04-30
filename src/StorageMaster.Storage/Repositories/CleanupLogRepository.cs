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
            var conn = await _db.GetConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO CleanupLog
                    (SuggestionId, RuleId, Title, BytesFreed, WasDryRun, Status, ExecutedUtc, ErrorMessage)
                VALUES
                    ($sid, $rule, $title, $freed, $dry, $status, $executed, $error);
                """;
            cmd.Parameters.AddWithValue("$sid",      result.SuggestionId.ToString());
            cmd.Parameters.AddWithValue("$rule",     suggestion.RuleId);
            cmd.Parameters.AddWithValue("$title",    suggestion.Title);
            cmd.Parameters.AddWithValue("$freed",    result.BytesFreed);
            cmd.Parameters.AddWithValue("$dry",      result.WasDryRun ? 1 : 0);
            cmd.Parameters.AddWithValue("$status",   result.Status.ToString());
            cmd.Parameters.AddWithValue("$executed", result.ExecutedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$error",    (object?)result.ErrorMessage ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<CleanupLogEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
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
                Id           = reader.GetInt64(reader.GetOrdinal("Id")),
                SuggestionId = Guid.Parse(reader.GetString(reader.GetOrdinal("SuggestionId"))),
                RuleId       = reader.GetString(reader.GetOrdinal("RuleId")),
                Title        = reader.GetString(reader.GetOrdinal("Title")),
                BytesFreed   = reader.GetInt64(reader.GetOrdinal("BytesFreed")),
                WasDryRun    = reader.GetInt32(reader.GetOrdinal("WasDryRun")) == 1,
                Status       = reader.GetString(reader.GetOrdinal("Status")),
                ExecutedUtc  = DateTime.Parse(reader.GetString(reader.GetOrdinal("ExecutedUtc"))),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null
                               : reader.GetString(reader.GetOrdinal("ErrorMessage")),
            });
        }
        return list;
    }
}
