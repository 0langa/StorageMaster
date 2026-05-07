namespace StorageMaster.Core.Models;

public enum DriveHealthStatus
{
    Unknown,
    Healthy,
    Warning,
    Critical,
    Unsupported,
}

public sealed record DriveHealthSnapshot
{
    public required string DriveName { get; init; }
    public string VolumeLabel { get; init; } = string.Empty;
    public string DriveFormat { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public int FreePercent { get; init; }
    public DriveHealthStatus Status { get; init; } = DriveHealthStatus.Unknown;
    public string Source { get; init; } = "Unknown";
    public string Message { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public int? TemperatureCelsius { get; init; }
    public int? WearPercent { get; init; }
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
}
