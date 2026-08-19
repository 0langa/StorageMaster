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

    // Serializes mutations issued through this repository instance. The
    // context's path-keyed WriteLock coordinates independent contexts, while
    // UpdateAsync's SQLite transaction makes the database read/modify/write
    // one atomic operation.
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public SettingsRepository(StorageDbContext db) => _db = db;

    public AppSettings Current => Clone(_current);

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.GetConnectionAsync(ct);
        var settings = await LoadCoreAsync(conn, transaction: null, ct).ConfigureAwait(false);
        _current = Clone(settings);
        return settings;
    }

    private static async Task<AppSettings> LoadCoreAsync(
        SqliteConnection conn,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", Key);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (result is string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.FfmpegPath = FfmpegPathNormalizer.Normalize(settings.FfmpegPath);
            return settings;
        }

        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var conn = await _db.GetConnectionAsync(ct);
                await using var transaction =
                    (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                var settings = await LoadCoreAsync(conn, transaction, ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                mutate(settings);
                ct.ThrowIfCancellationRequested();

                await PersistAsync(conn, transaction, settings, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                _current = Clone(settings);
                return settings;
            }
            finally
            {
                _db.WriteLock.Release();
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken ct)
    {
        await _db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await _db.GetConnectionAsync(ct);
            await PersistAsync(conn, transaction: null, settings, ct).ConfigureAwait(false);
            _current = Clone(settings);
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    private static async Task PersistAsync(
        SqliteConnection conn,
        SqliteTransaction? transaction,
        AppSettings settings,
        CancellationToken ct)
    {
        settings.FfmpegPath = FfmpegPathNormalizer.Normalize(settings.FfmpegPath);
        var json = JsonSerializer.Serialize(settings);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($key, $val)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", Key);
        cmd.Parameters.AddWithValue("$val", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
}
