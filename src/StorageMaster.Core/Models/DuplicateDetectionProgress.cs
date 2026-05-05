namespace StorageMaster.Core.Models;

public sealed record DuplicateDetectionProgress(
    int ProcessedFiles,
    int TotalFiles,
    string CurrentPath,
    string Stage);
