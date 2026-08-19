using StorageMaster.Core.Localization;
using System.Management;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Platform.Windows;

public sealed class DriveHealthProvider(
    IDriveInfoProvider driveInfoProvider,
    ILogger<DriveHealthProvider> logger) : IDriveHealthProvider
{
    public Task<IReadOnlyList<DriveHealthSnapshot>> GetHealthAsync(CancellationToken ct = default) =>
        Task.Run(() => ReadHealth(ct), ct);

    private IReadOnlyList<DriveHealthSnapshot> ReadHealth(CancellationToken ct)
    {
        var storageHealthByIndex = QueryStorageHealthByDiskIndex();
        var snapshots = new List<DriveHealthSnapshot>();

        foreach (var drive in driveInfoProvider.GetAvailableDrives())
        {
            ct.ThrowIfCancellationRequested();

            var freePercent = drive.TotalBytes <= 0
                ? 0
                : (int)Math.Round((double)drive.FreeBytes / drive.TotalBytes * 100d);

            var snapshot = TryReadDriveHealth(drive, storageHealthByIndex, freePercent);
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    private DriveHealthSnapshot TryReadDriveHealth(
        DriveDetail drive,
        IReadOnlyDictionary<int, StorageDiskHealth> storageHealthByIndex,
        int freePercent)
    {
        var capturedUtc = DateTime.UtcNow;
        var baseSnapshot = new DriveHealthSnapshot
        {
            DriveName = drive.Name,
            VolumeLabel = drive.VolumeLabel,
            DriveFormat = drive.DriveFormat,
            TotalBytes = drive.TotalBytes,
            FreeBytes = drive.FreeBytes,
            FreePercent = freePercent,
            Status = DriveHealthStatus.Unsupported,
            Source = "DriveInfo",
            Message = "This drive does not expose local Windows disk health telemetry.",
            CapturedUtc = capturedUtc,
        };

        try
        {
            var driveLetter = drive.Name.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(driveLetter))
                return baseSnapshot;

            var logicalDisk = new ManagementPath($"Win32_LogicalDisk.DeviceID='{driveLetter}'");
            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{{logicalDisk.RelativePath}}} WHERE AssocClass=Win32_LogicalDiskToPartition");

            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using (partition)
                using (var diskSearcher = new ManagementObjectSearcher(
                           $"ASSOCIATORS OF {{{partition.Path.RelativePath}}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                {
                    foreach (ManagementObject disk in diskSearcher.Get())
                    {
                        using (disk)
                        {
                            var index = TryGetInt(disk, "Index");
                            var model = TryGetString(disk, "Model");
                            var serial = TryGetString(disk, "SerialNumber");
                            var mediaType = TryGetString(disk, "MediaType");
                            var win32Status = TryGetString(disk, "Status");

                            if (index is int diskIndex && storageHealthByIndex.TryGetValue(diskIndex, out var storage))
                            {
                                var status = MapStorageHealth(storage.HealthStatus);
                                return baseSnapshot with
                                {
                                    Status = status,
                                    Source = "MSFT_PhysicalDisk",
                                    Message = BuildStorageMessage(status, storage.HealthStatusText),
                                    Model = FirstNonEmpty(storage.Model, model),
                                    SerialNumber = FirstNonEmpty(storage.SerialNumber, serial),
                                    MediaType = FirstNonEmpty(storage.MediaType, mediaType),
                                };
                            }

                            if (!string.IsNullOrWhiteSpace(win32Status))
                            {
                                var status = win32Status.Equals("OK", StringComparison.OrdinalIgnoreCase)
                                    ? DriveHealthStatus.Healthy
                                    : DriveHealthStatus.Warning;
                                return baseSnapshot with
                                {
                                    Status = status,
                                    Source = "Win32_DiskDrive",
                                    Message = status == DriveHealthStatus.Healthy
                                        ? Loc.Get("Health_Message_WindowsReportsOk")
                                        : Loc.Format("Health_Message_WindowsReportsStatus", win32Status),
                                    Model = model,
                                    SerialNumber = serial,
                                    MediaType = mediaType,
                                };
                            }
                        }
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            logger.LogDebug(ex, "Drive health WMI query failed for {Drive}", drive.Name);
            return baseSnapshot with
            {
                Status = DriveHealthStatus.Unknown,
                Source = "WMI",
                Message = "Windows health telemetry could not be read for this drive.",
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Drive health WMI access denied for {Drive}", drive.Name);
            return baseSnapshot with
            {
                Status = DriveHealthStatus.Unknown,
                Source = "WMI",
                Message = "Windows denied access to drive health telemetry.",
            };
        }

        return baseSnapshot;
    }

    private IReadOnlyDictionary<int, StorageDiskHealth> QueryStorageHealthByDiskIndex()
    {
        var result = new Dictionary<int, StorageDiskHealth>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, SerialNumber, MediaType, HealthStatus FROM MSFT_PhysicalDisk");

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    var idText = TryGetString(disk, "DeviceId");
                    if (!int.TryParse(idText, out var diskIndex))
                        continue;

                    var health = TryGetInt(disk, "HealthStatus");
                    result[diskIndex] = new StorageDiskHealth(
                        TryGetString(disk, "FriendlyName"),
                        TryGetString(disk, "SerialNumber"),
                        MapMediaType(TryGetInt(disk, "MediaType")),
                        health,
                        health is null ? "Unknown" : health.Value.ToString());
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "MSFT_PhysicalDisk health query failed");
        }

        return result;
    }

    private static DriveHealthStatus MapStorageHealth(int? healthStatus) => healthStatus switch
    {
        0 => DriveHealthStatus.Healthy,
        1 => DriveHealthStatus.Warning,
        2 => DriveHealthStatus.Critical,
        5 => DriveHealthStatus.Unknown,
        _ => DriveHealthStatus.Unknown,
    };

    // These are read by the user on a drive card, so they come from the catalogue
    // rather than being written here. The status text in the fallback is whatever
    // Windows reported and is passed through untranslated on purpose.
    private static string BuildStorageMessage(DriveHealthStatus status, string statusText) => status switch
    {
        DriveHealthStatus.Healthy => Loc.Get("Health_Message_StorageHealthy"),
        DriveHealthStatus.Warning => Loc.Get("Health_Message_StorageWarning"),
        DriveHealthStatus.Critical => Loc.Get("Health_Message_StorageCritical"),
        _ => Loc.Format("Health_Message_StorageUnknown", statusText),
    };

    private static string MapMediaType(int? mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => string.Empty,
    };

    private static string TryGetString(ManagementBaseObject obj, string name)
    {
        try { return obj[name]?.ToString()?.Trim() ?? string.Empty; }
        catch (ManagementException) { return string.Empty; }
    }

    private static int? TryGetInt(ManagementBaseObject obj, string name)
    {
        try
        {
            var value = obj[name];
            return value is null ? null : Convert.ToInt32(value);
        }
        catch (Exception ex) when (ex is ManagementException or FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private sealed record StorageDiskHealth(
        string Model,
        string SerialNumber,
        string MediaType,
        int? HealthStatus,
        string HealthStatusText);
}
