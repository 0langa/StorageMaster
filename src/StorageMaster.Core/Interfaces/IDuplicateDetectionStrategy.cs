using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Encapsulates everything one duplicate-detection method needs:
/// the candidate query shape, signature computation, match grouping,
/// and review/auto-selection policy.
///
/// Implement this interface to add a new detection method without touching
/// <see cref="IDuplicateFinderService"/> or any existing strategy.
/// </summary>
public interface IDuplicateDetectionStrategy
{
    DuplicateMethod Method { get; }

    /// <summary>Algorithm identifier stored in DuplicateSignatures.Algorithm.</summary>
    string Algorithm { get; }

    /// <summary>
    /// Increment when the algorithm changes in a way that invalidates
    /// previously cached signatures.
    /// </summary>
    int AlgorithmVersion { get; }

    /// <summary>
    /// When true, exact duplicates in this group may be automatically
    /// pre-selected for deletion. When false (fuzzy methods) user review
    /// is required before any member becomes selected.
    /// </summary>
    bool SupportsAutoSelection { get; }

    /// <summary>Confidence score assigned to groups built by this strategy.</summary>
    double DefaultConfidence { get; }

    /// <summary>Human-readable label shown in progress and group headers.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this strategy can run in the current environment.
    /// Video pHash, for example, depends on FFmpeg/ffprobe availability.
    /// </summary>
    bool IsAvailable => true;

    /// <summary>Optional reason shown when <see cref="IsAvailable"/> is false.</summary>
    string? UnavailableReason => null;

    /// <summary>
    /// When true the pipeline applies a partial-hash pre-filter before calling
    /// <see cref="ComputeSignatureAsync"/>, avoiding full hashing of candidates
    /// that cannot possibly be duplicates. Valid only for byte-exact methods.
    /// Default: false.
    /// </summary>
    bool UsePartialHashPreFilter => false;

    /// <summary>
    /// Build the candidate query for this strategy's pass.
    /// The pipeline calls this once per strategy, then feeds results to
    /// <see cref="ComputeSignatureAsync"/>.
    /// </summary>
    DuplicateCandidateQuery BuildCandidateQuery(DuplicateScanOptions options);

    /// <summary>
    /// Compute the signature for a single candidate file.
    /// Never throws for file-level errors; instead returns a signature with
    /// <c>Status = "Error"</c> and <c>ErrorMessage</c> set.
    /// </summary>
    Task<DuplicateSignature> ComputeSignatureAsync(
        DuplicateCandidate candidate,
        CancellationToken ct = default);

    /// <summary>
    /// Given a dictionary of (signature-key → candidates), return the groups
    /// that constitute actual duplicate matches.
    ///
    /// For exact methods: group when > 1 distinct candidate shares a key.
    /// For fuzzy methods: may cluster by distance rather than exact equality.
    /// </summary>
    IEnumerable<DuplicateStrategyMatch> BuildMatches(
        IReadOnlyDictionary<string, IReadOnlyList<DuplicateCandidate>> signatureGroups);
}

/// <summary>A cluster of candidates that form one duplicate group.</summary>
public sealed record DuplicateStrategyMatch(
    IReadOnlyList<DuplicateCandidate> Candidates,
    double Confidence,
    string ReasonText);
