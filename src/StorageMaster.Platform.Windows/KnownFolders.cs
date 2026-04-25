using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Platform.Windows;

public static class KnownFolders
{
    private static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string GetDownloadsPath() => Shell32Interop.GetKnownFolderPath(FOLDERID_Downloads);
}
