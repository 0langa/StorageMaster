using System.Collections.Concurrent;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Detection strategy for byte-exact duplicates via SHA-256 with a partial-hash
/// pre-filter to avoid full hashing of non-duplicate candidates.
///
/// Candidate query uses same-size buckets (valid because byte-exact duplicates
/// must have identical raw sizes).
/// </summary>
public sealed class ExactSha256Strategy(
    IFileContentHasher hasher,
    IFileSnapshotProvider snapshotProvider) : IDuplicateDetectionStrategy
{
    public DuplicateMethod Method => DuplicateMethod.ExactSha256;
    public string Algorithm => "SHA-256";
    public int AlgorithmVersion => 1;
    public bool SupportsAutoSelection => true;
    public bool UsePartialHashPreFilter => true;
    public double DefaultConfidence => 1.0d;
    public string DisplayName => "Exact SHA-256";

    public DuplicateCandidateQuery BuildCandidateQuery(DuplicateScanOptions options) =>
        new()
        {
            SessionId = options.SessionId,
            MinimumSizeBytes = options.MinimumSizeBytes,
            RequireSameSizeBucket = true,           // exact only — safe optimisation
            Extensions = options.IncludeExtensions,
            Categories = options.IncludeCategories,
            IncludedPaths = options.IncludedPaths,
            ExcludedPaths = options.ExcludedPaths,
            IncludeReparsePoints = options.IncludeReparsePoints,
            IncludeHiddenFiles = options.IncludeHiddenFiles,
        };

    /// <summary>
    /// Computes SHA-256 for one candidate with before/after snapshot validation.
    /// If the file changes during hashing, returns an error signature instead
    /// of silently using a stale hash.
    /// </summary>
    public async Task<DuplicateSignature> ComputeSignatureAsync(
        DuplicateCandidate candidate,
        CancellationToken ct = default)
    {
        var before = await snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
        if (before is null)
            return ErrorSignature(candidate, "FileNotFound", "File no longer exists before hashing.");

        try
        {
            var hash = await hasher.ComputeSha256Async(candidate.File.FullPath, ct);

            var after = await snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
            if (after is null || !before.IsIdenticalTo(after))
                return ErrorSignature(candidate, "FileChangedDuringHash",
                    "File was modified or removed while being hashed.");

            return new DuplicateSignature
            {
                Id = 0,
                SessionId = candidate.File.SessionId,
                FileEntryId = candidate.File.Id,
                Method = Method,
                Algorithm = Algorithm,
                AlgorithmVersion = AlgorithmVersion,
                SignatureText = hash,
                ComputedUtc = DateTime.UtcNow,
                Status = "Ready",
                SourceSizeBytes = before.SizeBytes,
                SourceModifiedUtc = before.LastWriteUtc,
                SourceFileIdentity = before.Identity is { } id
                    ? $"{id.VolumeSerial}:{id.FileIndex}"
                    : null,
            };
        }
        catch (Exception ex)
        {
            return ErrorSignature(candidate, "HashError", ex.Message);
        }
    }

    /// <summary>
    /// Groups candidates by exact SHA-256 value. Applies partial-hash pre-filter
    /// to avoid full-hashing every candidate in a size bucket.
    /// </summary>
    public IEnumerable<DuplicateStrategyMatch> BuildMatches(
        IReadOnlyDictionary<string, IReadOnlyList<DuplicateCandidate>> signatureGroups)
    {
        foreach (var (hash, candidates) in signatureGroups)
        {
            if (candidates.Count < 2) continue;

            // De-duplicate NTFS hardlinks: files sharing volume+fileIndex are the
            // same on-disk inode and must not count as reclaimable duplicates.
            var distinctInodes = candidates
                .GroupBy(c => c.Identity is { } id
                    ? $"{id.VolumeSerial}:{id.FileIndex}"
                    : c.File.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(static g => g.First())
                .ToList();

            if (distinctInodes.Count < 2) continue;

            yield return new DuplicateStrategyMatch(
                distinctInodes,
                DefaultConfidence,
                "Exact byte duplicate");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs partial hashing for every candidate and groups by that hash.
    /// Callers should then full-hash only groups with > 1 member.
    /// </summary>
    public async Task<ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>>>
        BuildPartialHashGroupsAsync(
            IReadOnlyList<DuplicateCandidate> candidates,
            int maxConcurrency,
            ConcurrentBag<DuplicateError> errors,
            long runId,
            CancellationToken ct)
    {
        var partialGroups = new ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, maxConcurrency),
        }, async (candidate, token) =>
        {
            try
            {
                var ph = await hasher.ComputePartialHashAsync(candidate.File.FullPath, token);
                partialGroups.GetOrAdd(ph, static _ => []).Add(candidate);
            }
            catch (Exception ex)
            {
                errors.Add(new DuplicateError
                {
                    Id = 0,
                    RunId = runId,
                    FileEntryId = candidate.File.Id,
                    Path = candidate.File.FullPath,
                    ErrorType = "PartialHash",
                    Message = ex.Message,
                    OccurredUtc = DateTime.UtcNow,
                });
            }
        });
        return partialGroups;
    }

    private DuplicateSignature ErrorSignature(DuplicateCandidate c, string errorType, string message) =>
        new()
        {
            Id = 0,
            SessionId = c.File.SessionId,
            FileEntryId = c.File.Id,
            Method = Method,
            Algorithm = Algorithm,
            AlgorithmVersion = AlgorithmVersion,
            ComputedUtc = DateTime.UtcNow,
            Status = "Error",
            ErrorMessage = message,
            SourceSizeBytes = c.File.SizeBytes,
            SourceModifiedUtc = c.File.ModifiedUtc,
        };
}
