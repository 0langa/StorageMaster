using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Enumerates regular files without traversing directory or file reparse points.
/// </summary>
public interface INoFollowFileEnumerator
{
    /// <summary>
    /// Recursively enumerates accessible regular files beneath an absolute directory root.
    /// Descendant reparse-point entries are intentionally skipped. Required root-chain
    /// reparses and directories that only permit weak read guards are reported in
    /// <see cref="NoFollowFileEnumerationResult.Errors"/> without discarding files that
    /// were captured successfully.
    /// </summary>
    Task<NoFollowFileEnumerationResult> EnumerateAsync(
        string absoluteRoot,
        CancellationToken ct = default);

    /// <summary>
    /// Recursively enumerates <paramref name="absoluteScanRoot"/> while retaining no-follow
    /// guards for every ancestor from <paramref name="absoluteBoundaryRoot"/> through the
    /// scan root. The scan root must equal or be beneath the boundary root.
    /// </summary>
    Task<NoFollowFileEnumerationResult> EnumerateAsync(
        string absoluteBoundaryRoot,
        string absoluteScanRoot,
        CancellationToken ct = default);

    /// <summary>
    /// Opens no-follow guards from <paramref name="absoluteRoot"/> through the parent of
    /// <paramref name="expected"/>, validates the live regular file against the expected
    /// snapshot, and returns a lease that keeps the ancestry bound until disposal.
    /// Returns <see langword="null"/> when containment or live validation fails, or when
    /// any directory component cannot be held with a strong replacement-blocking guard.
    /// </summary>
    ValueTask<INoFollowFileValidationLease?> TryOpenValidatedFileAsync(
        string absoluteRoot,
        FileSnapshot expected,
        CancellationToken ct = default);
}

/// <summary>
/// Keeps a validated file's directory ancestry guarded against rename, deletion, or
/// reparse-point conversion while a caller performs a path-based operation.
/// </summary>
public interface INoFollowFileValidationLease : IDisposable
{
    /// <summary>The live snapshot captured while all ancestry guards were held.</summary>
    FileSnapshot LiveSnapshot { get; }
}

/// <summary>Result of a no-follow recursive enumeration.</summary>
public sealed record NoFollowFileEnumerationResult(
    IReadOnlyList<FileSnapshot> Files,
    IReadOnlyList<NoFollowFileEnumerationError> Errors)
{
    /// <summary>
    /// True when at least one path could not be strongly guarded, inspected, or snapshotted.
    /// </summary>
    public bool IsPartial => Errors.Count > 0;
}

/// <summary>A path-specific failure encountered during no-follow enumeration.</summary>
public sealed record NoFollowFileEnumerationError(
    string Path,
    NoFollowFileEnumerationErrorKind Kind,
    string Message);

/// <summary>Classifies failures that make a no-follow enumeration partial.</summary>
public enum NoFollowFileEnumerationErrorKind
{
    NotFound,
    AccessDenied,
    EnumerationFailed,
    InspectionFailed,
    ReplacementGuardUnavailable,
    SnapshotUnavailable,
    IdentityUnavailable,
}
