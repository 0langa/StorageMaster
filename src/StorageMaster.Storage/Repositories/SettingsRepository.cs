using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Storage.Repositories;

public sealed class SettingsRepository : ISettingsRepository, ISettingsSnapshotProvider
{
    private readonly StorageDbContext _db;
    private const string Key = "AppSettings";
    private AppSettings _current = new();

    public SettingsRepository(StorageDbContext db) => _db = db;

    public AppSettings Current => Clone(_current);

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var conn = await _db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", Key);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.FfmpegPath = FfmpegPathNormalizer.Normalize(settings.FfmpegPath);
            _current = Clone(settings);
            return settings;
        }

        _current = new AppSettings();
        return Clone(_current);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _db.WriteLock.WaitAsync(ct);
        try
        {
            settings.FfmpegPath = FfmpegPathNormalizer.Normalize(settings.FfmpegPath);
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
            _current = Clone(settings);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
}
