using StorageMaster.Core.Interfaces;

namespace StorageMaster.Platform.Windows;

public sealed class DriveInfoProvider : IDriveInfoProvider
{
    public IReadOnlyList<DriveDetail> GetAvailableDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable)
            .Select(ToDriveDetail)
            .ToList();
    }

    public DriveDetail? GetDrive(string rootPath)
    {
        var root = Path.GetPathRoot(rootPath);
        if (root is null) return null;

        try
        {
            var info = new DriveInfo(root);
            return info.IsReady ? ToDriveDetail(info) : null;
        }
        catch { return null; }
    }

    private static DriveDetail ToDriveDetail(DriveInfo d) => new(
        Name:        d.Name,
        VolumeLabel: TrySafe(() => d.VolumeLabel) ?? string.Empty,
        DriveFormat: TrySafe(() => d.DriveFormat)  ?? string.Empty,
        TotalBytes:  TrySafe(() => d.TotalSize)    ?? 0,
        FreeBytes:   TrySafe(() => d.TotalFreeSpace) ?? 0,
        UsedBytes:   TrySafe(() => d.TotalSize - d.TotalFreeSpace) ?? 0,
        IsReady:     d.IsReady);

    private static T? TrySafe<T>(Func<T> fn) where T : struct
    {
        try { return fn(); }
        catch { return null; }
    }

    private static string? TrySafe(Func<string> fn)
    {
        try { return fn(); }
        catch { return null; }
    }
}
