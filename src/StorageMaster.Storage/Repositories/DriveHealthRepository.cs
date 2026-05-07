using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Storage.Repositories;

public sealed class DriveHealthRepository(StorageDbContext db) : IDriveHealthRepository
{
    public async Task SaveSnapshotsAsync(IReadOnlyList<DriveHealthSnapshot> snapshots, CancellationToken ct = default)
    {
        if (snapshots.Count == 0)
            return;

        await db.WriteLock.WaitAsync(ct);
        try
        {
            var conn = await db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO DriveHealthSnapshots (
                    DriveName, VolumeLabel, DriveFormat, TotalBytes, FreeBytes, FreePercent,
                    Status, Source, Message, Model, SerialNumber, MediaType,
                    TemperatureCelsius, WearPercent, CapturedUtc)
                VALUES (
                    $drive, $label, $format, $total, $free, $freePercent,
                    $status, $source, $message, $model, $serial, $media,
                    $temperature, $wear, $captured);
                """;

            var drive = cmd.Parameters.Add("$drive", SqliteType.Text);
            var label = cmd.Parameters.Add("$label", SqliteType.Text);
            var format = cmd.Parameters.Add("$format", SqliteType.Text);
            var total = cmd.Parameters.Add("$total", SqliteType.Integer);
            var free = cmd.Parameters.Add("$free", SqliteType.Integer);
            var freePercent = cmd.Parameters.Add("$freePercent", SqliteType.Integer);
            var status = cmd.Parameters.Add("$status", SqliteType.Text);
            var source = cmd.Parameters.Add("$source", SqliteType.Text);
            var message = cmd.Parameters.Add("$message", SqliteType.Text);
            var model = cmd.Parameters.Add("$model", SqliteType.Text);
            var serial = cmd.Parameters.Add("$serial", SqliteType.Text);
            var media = cmd.Parameters.Add("$media", SqliteType.Text);
            var temperature = cmd.Parameters.Add("$temperature", SqliteType.Integer);
            var wear = cmd.Parameters.Add("$wear", SqliteType.Integer);
            var captured = cmd.Parameters.Add("$captured", SqliteType.Text);

            foreach (var snapshot in snapshots)
            {
                ct.ThrowIfCancellationRequested();
                drive.Value = snapshot.DriveName;
                label.Value = snapshot.VolumeLabel;
                format.Value = snapshot.DriveFormat;
                total.Value = snapshot.TotalBytes;
                free.Value = snapshot.FreeBytes;
                freePercent.Value = snapshot.FreePercent;
                status.Value = snapshot.Status.ToString();
                source.Value = snapshot.Source;
                message.Value = snapshot.Message;
                model.Value = snapshot.Model;
                serial.Value = snapshot.SerialNumber;
                media.Value = snapshot.MediaType;
                temperature.Value = (object?)snapshot.TemperatureCelsius ?? DBNull.Value;
                wear.Value = (object?)snapshot.WearPercent ?? DBNull.Value;
                captured.Value = snapshot.CapturedUtc.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<DriveHealthSnapshot>> GetLatestSnapshotsAsync(CancellationToken ct = default)
    {
        var conn = await db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.*
            FROM DriveHealthSnapshots d
            INNER JOIN (
                SELECT DriveName, MAX(CapturedUtc) AS CapturedUtc
                FROM DriveHealthSnapshots
                GROUP BY DriveName
            ) latest
              ON latest.DriveName = d.DriveName
             AND latest.CapturedUtc = d.CapturedUtc
            ORDER BY d.DriveName;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<DriveHealthSnapshot>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadSnapshot(reader));
        return results;
    }

    public async Task<IReadOnlyList<DriveHealthSnapshot>> GetHistoryAsync(
        string driveName,
        int limit = 100,
        CancellationToken ct = default)
    {
        var conn = await db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM DriveHealthSnapshots
            WHERE DriveName = $drive
            ORDER BY CapturedUtc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$drive", driveName);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<DriveHealthSnapshot>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadSnapshot(reader));
        return results;
    }

    private static DriveHealthSnapshot ReadSnapshot(SqliteDataReader reader) => new()
    {
        DriveName = reader.GetString(reader.GetOrdinal("DriveName")),
        VolumeLabel = reader.GetString(reader.GetOrdinal("VolumeLabel")),
        DriveFormat = reader.GetString(reader.GetOrdinal("DriveFormat")),
        TotalBytes = reader.GetInt64(reader.GetOrdinal("TotalBytes")),
        FreeBytes = reader.GetInt64(reader.GetOrdinal("FreeBytes")),
        FreePercent = reader.GetInt32(reader.GetOrdinal("FreePercent")),
        Status = Enum.TryParse<DriveHealthStatus>(reader.GetString(reader.GetOrdinal("Status")), out var status)
            ? status
            : DriveHealthStatus.Unknown,
        Source = reader.GetString(reader.GetOrdinal("Source")),
        Message = reader.GetString(reader.GetOrdinal("Message")),
        Model = reader.GetString(reader.GetOrdinal("Model")),
        SerialNumber = reader.GetString(reader.GetOrdinal("SerialNumber")),
        MediaType = reader.GetString(reader.GetOrdinal("MediaType")),
        TemperatureCelsius = reader.IsDBNull(reader.GetOrdinal("TemperatureCelsius"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("TemperatureCelsius")),
        WearPercent = reader.IsDBNull(reader.GetOrdinal("WearPercent"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("WearPercent")),
        CapturedUtc = DateTime.Parse(
            reader.GetString(reader.GetOrdinal("CapturedUtc")),
            null,
            System.Globalization.DateTimeStyles.RoundtripKind),
    };
}
