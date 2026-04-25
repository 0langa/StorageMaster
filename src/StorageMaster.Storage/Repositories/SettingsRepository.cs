using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Storage.Repositories;

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly StorageDbContext _db;
    private const string Key = "AppSettings";

    public SettingsRepository(StorageDbContext db) => _db = db;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", Key);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is string json)
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings);
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($key, $val)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", Key);
        cmd.Parameters.AddWithValue("$val", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
