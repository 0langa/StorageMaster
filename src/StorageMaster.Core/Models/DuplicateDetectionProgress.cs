namespace StorageMaster.Core.Models;

/// <summary>
/// Rich per-phase progress snapshot emitted by <see cref="IDuplicateFinderService"/>.
/// One record is emitted per processed file; consumers should throttle UI updates.
/// </summary>
public sealed record DuplicateDetectionProgress
{
    public required long   RunId       { get; init; }
    public required string Phase       { get; init; }   // human label, e.g. "Hashing exact candidates"
    public required DuplicateMethod Method { get; init; }
    public required int    Processed   { get; init; }   // files processed in current phase
    public required int    Total       { get; init; }   // total files in current phase
    public string          CurrentPath { get; init; } = string.Empty;
    public int             GroupsFound { get; init; }
    public int             Errors      { get; init; }
    public bool            CanCancel   { get; init; } = true;
}
