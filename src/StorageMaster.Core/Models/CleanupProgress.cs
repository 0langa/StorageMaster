namespace StorageMaster.Core.Models;

/// <summary>Progress snapshot emitted by ICleanupEngine during ExecuteAsync.</summary>
public sealed record CleanupProgress(
    int    Completed,
    int    Total,
    string CurrentTitle);
