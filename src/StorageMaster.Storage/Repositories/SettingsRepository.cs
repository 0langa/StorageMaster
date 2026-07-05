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

    // Serializes whole-settings writers within the process. The DB WriteLock
    // only guards the SQLite write itself; this lock closes the wider
    // load-modify-save window so one writer cannot drop another's change.
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

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
        await _mutationLock.WaitAsync(ct);
        try
        {
            await SaveCoreAsync(settings, ct);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct);
        try
        {
            var settings = await LoadAsync(ct);
            mutate(settings);
            await SaveCoreAsync(settings, ct);
            return settings;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken ct)
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
