namespace StorageMaster.Core.Models;

public enum UpdateFailureKind
{
    Unknown = 0,
    MissingInstallerAsset,
    NetworkTimeout,
    DownloadFileInUse,
    ChecksumMismatch,
    InvalidSignature,
    InsecureDownloadUrl,
    UserCancelledElevation,
}
