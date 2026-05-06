using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Legacy <see cref="IDuplicateSignatureProvider"/> wrapper kept for backward
/// compatibility with existing tests. New code should use
/// <see cref="NormalizedTextStrategy"/> directly via <see cref="IDuplicateDetectionStrategy"/>.
/// </summary>
public sealed class NormalizedTextSignatureProvider : IDuplicateSignatureProvider
{
    private readonly NormalizedTextStrategy _strategy = new();

    public DuplicateMethod Method => DuplicateMethod.NormalizedText;

    public Task<DuplicateSignature> ComputeAsync(
        DuplicateCandidate candidate,
        CancellationToken ct = default) =>
        _strategy.ComputeSignatureAsync(candidate, ct);

    /// <summary>Returns true when this file's extension is in the default supported set.</summary>
    public static bool CanProcess(FileEntry file) => NormalizedTextStrategy.CanProcess(file);
}
