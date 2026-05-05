using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Captures an atomic-enough snapshot of a file's identity attributes.
/// Used to detect whether a file changed during a long hashing operation.
/// </summary>
public interface IFileSnapshotProvider
{
    /// <summary>
    /// Returns a snapshot of the file at <paramref name="path"/>, or null if
    /// the file does not exist or is inaccessible.
    /// </summary>
    ValueTask<FileSnapshot?> TakeSnapshotAsync(string path, CancellationToken ct = default);
}
