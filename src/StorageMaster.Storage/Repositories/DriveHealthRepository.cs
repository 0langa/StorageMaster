using Microsoft.Data.Sqlite;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Storage.Repositories;

public sealed class DriveHealthRepository(StorageDbContext db) : IDriveHealthRepository
{
    /// <summary>
    /// Snapshots kept per drive. Matches the ceiling <see cref="GetHistoryAsync"/>
    /// clamps its limit to, so retention never discards a row the app could show.
    /// </summary>
    internal const int MaxSnapshotsPerDrive = 1000;

    public async Task SaveSnapshotsAsync(IReadOnlyList<DriveHealthSnapshot> snapshots, CancellationToken ct = default)
    {
        if (snapshots.Count == 0)
            return;

        await db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await db.GetConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Nothing else prunes this table: every dashboard refresh, Drive Health page
            // load and CLI health run appends a row per drive, DeleteSessionAsync cannot
            // reach it (no session foreign key), and purging scan history leaves it
            // untouched, so the file grew for the lifetime of the install. Trimming here
            // keeps the write in the transaction that created the rows.
            using var pruneCmd = conn.CreateCommand();
            pruneCmd.Transaction = (SqliteTransaction)tx;
            pruneCmd.CommandText = """
                DELETE FROM DriveHealthSnapshots
                WHERE Id IN (
                    SELECT Id
                    FROM DriveHealthSnapshots
                    WHERE DriveName = $drive
                    ORDER BY CapturedUtc DESC, Id DESC
                    LIMIT -1 OFFSET $keep
                );
                """;
            var pruneDrive = pruneCmd.Parameters.Add("$drive", SqliteType.Text);
            pruneCmd.Parameters.AddWithValue("$keep", MaxSnapshotsPerDrive);

            // Ordinal, because DriveName is compared with BINARY collation in SQL:
            // a case-insensitive grouping here would leave one spelling unpruned.
            var drives = snapshots
                .Select(static snapshot => snapshot.DriveName)
                .Distinct(StringComparer.Ordinal);

            foreach (var driveName in drives)
            {
                ct.ThrowIfCancellationRequested();
                pruneDrive.Value = driveName;
                await pruneCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            db.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<DriveHealthSnapshot>> GetLatestSnapshotsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.GetConnectionAsync(ct);
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
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<DriveHealthSnapshot>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadSnapshot(reader));
        return results;
    }

    public async Task<IReadOnlyList<DriveHealthSnapshot>> GetHistoryAsync(
        string driveName,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = await db.GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM DriveHealthSnapshots
            WHERE DriveName = $drive
            ORDER BY CapturedUtc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$drive", driveName);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSnapshotsPerDrive));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<DriveHealthSnapshot>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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
        CapturedUtc = UtcTimestamp.Parse(reader.GetString(reader.GetOrdinal("CapturedUtc"))),
    };
}
