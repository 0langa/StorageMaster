using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Storage.Repositories;

public sealed class ScanErrorRepository : IScanErrorRepository
{
    private readonly StorageDbContext _db;

    public ScanErrorRepository(StorageDbContext db) => _db = db;

    public async Task LogErrorsAsync(
        long sessionId,
        IReadOnlyList<ScanError> errors,
        CancellationToken ct = default)
    {
        if (errors.Count == 0) return;

        var conn = await _db.GetConnectionAsync(ct);
        using var tx  = await conn.BeginTransactionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO ScanErrors (SessionId, Path, ErrorType, Message, OccurredAt)
            VALUES ($sid, $path, $type, $msg, $at);
            """;

        var pSid  = cmd.Parameters.Add("$sid",  SqliteType.Integer);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pType = cmd.Parameters.Add("$type", SqliteType.Text);
        var pMsg  = cmd.Parameters.Add("$msg",  SqliteType.Text);
        var pAt   = cmd.Parameters.Add("$at",   SqliteType.Text);

        pSid.Value = sessionId;
        foreach (var e in errors)
        {
            pPath.Value = e.Path;
            pType.Value = e.ErrorType;
            pMsg.Value  = e.Message;
            pAt.Value   = e.OccurredAt.ToString("O");
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ScanError>> GetErrorsForSessionAsync(
        long sessionId,
        CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, SessionId, Path, ErrorType, Message, OccurredAt
            FROM ScanErrors
            WHERE SessionId = $sid
            ORDER BY OccurredAt;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ScanError>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ScanError
            {
                Id         = reader.GetInt64(0),
                SessionId  = reader.GetInt64(1),
                Path       = reader.GetString(2),
                ErrorType  = reader.GetString(3),
                Message    = reader.GetString(4),
                OccurredAt = DateTime.Parse(reader.GetString(5)),
            });
        }
        return list;
    }
}
