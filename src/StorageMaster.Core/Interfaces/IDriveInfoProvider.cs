namespace StorageMaster.Core.Interfaces;

public sealed record DriveDetail(
    string Name,
    string VolumeLabel,
    string DriveFormat,
    long   TotalBytes,
    long   FreeBytes,
    long   UsedBytes,
    bool   IsReady);

public interface IDriveInfoProvider
{
    IReadOnlyList<DriveDetail> GetAvailableDrives();
    DriveDetail? GetDrive(string rootPath);
}
