using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Coordinates one or more <see cref="IDuplicateDetectionStrategy"/> instances
/// to produce a complete duplicate analysis run.
///
/// Pipeline per strategy:
///   1.  Build candidate query (strategy decides exact vs. fuzzy scope).
///   2.  Fetch candidates from repository.
///   3.  Load cached signatures; re-use when still valid (size + mtime + algorithm version).
///   4.  Compute missing/stale signatures (parallel, bounded concurrency).
///       - ExactSha256: applies partial-hash pre-filter before full hash.
///       - All: before/after snapshot comparison to detect file changes during hash.
///   5.  Upsert signatures into DB.
///   6.  Call strategy.BuildMatches() to cluster into duplicate groups.
///   7.  Apply keeper policy; persist groups + members.
///
/// Progress emitted is per-phase and never exceeds the phase total.
/// Cancellation persists DuplicateRunStatus.Cancelled; partial results are
/// discarded from the active run.
/// </summary>
public sealed class DuplicateFinderService(
    IDuplicateRepository repository,
    IDuplicateCandidateProvider candidateProvider,
    IFileContentHasher hasher,
    IEnumerable<IDuplicateDetectionStrategy> strategies,
    IDuplicateKeeperPolicy keeperPolicy,
    ILogger<DuplicateFinderService> logger) : IDuplicateFinderService
{
    private readonly IReadOnlyDictionary<DuplicateMethod, IDuplicateDetectionStrategy> _strategyMap =
        strategies.ToDictionary(s => s.Method);

    public async Task<DuplicateRun> RunAsync(
        DuplicateScanOptions options,
        IProgress<DuplicateDetectionProgress>? progress = null,
        CancellationToken ct = default)
    {
        // ── P0.3: Validate methods before expensive work ───────────────────
        var missing = options.Methods
            .Where(m => !_strategyMap.ContainsKey(m))
            .ToList();
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Select(m => m.ToString()));
            throw new InvalidOperationException(
                $"The following duplicate detection methods have no registered strategy: {names}. " +
                "Install the required provider package or disable the method.");
        }

        var unavailable = options.Methods
            .Select(m => _strategyMap[m])
            .Where(static s => !s.IsAvailable)
            .ToList();
        if (unavailable.Count > 0)
        {
            var message = string.Join("; ", unavailable.Select(s =>
                s.UnavailableReason is { Length: > 0 }
                    ? $"{s.DisplayName}: {s.UnavailableReason}"
                    : s.DisplayName));
            throw new InvalidOperationException(
                $"One or more duplicate detection methods are unavailable: {message}");
        }

        var run = await repository.CreateRunAsync(options, ct);
        var allSignatures = new ConcurrentBag<DuplicateSignature>();
        var allErrors = new ConcurrentBag<DuplicateError>();
        var allGroupPairs = new List<(DuplicateGroup Group, List<DuplicateGroupMember> Members)>();
        var nextOrdinal = new long[] { 1L };  // boxed to avoid ref-in-async restriction

        try
        {
            foreach (var method in options.Methods)
            {
                ct.ThrowIfCancellationRequested();

                var strategy = _strategyMap[method];
                logger.LogInformation("Run {RunId}: starting {Method} phase", run.Id, method);

                await RunStrategyPhaseAsync(
                    run, options, strategy, progress,
                    allSignatures, allErrors, allGroupPairs,
                    nextOrdinal, ct);
            }

            var groupRecords = allGroupPairs.Select(static p => p.Group).ToList();
            var memberRecords = allGroupPairs.SelectMany(static p => p.Members).ToList();

            await repository.SaveResultsAsync(
                run.Id,
                [.. allSignatures],
                groupRecords,
                memberRecords,
                [.. allErrors],
                ct);

            await repository.CompleteRunAsync(
                run.Id,
                DuplicateRunStatus.Completed,
                allSignatures.Count,
                groupRecords.Count,
                groupRecords.Sum(static g => g.TotalBytes),
                groupRecords.Sum(static g => g.ReclaimableBytes),
                allErrors.Count,
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Run {RunId} cancelled", run.Id);
            await repository.CompleteRunAsync(
                run.Id, DuplicateRunStatus.Cancelled,
                0, 0, 0, 0, allErrors.Count,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} failed", run.Id);
            await repository.CompleteRunAsync(
                run.Id, DuplicateRunStatus.Failed,
                0, 0, 0, 0, allErrors.Count, ex.Message,
                CancellationToken.None);
        }

        var completed = (await repository.GetRunsForSessionAsync(options.SessionId, CancellationToken.None))
            .First(r => r.Id == run.Id);
        return completed;
    }

    // ── Per-strategy phase ────────────────────────────────────────────────────

    private async Task RunStrategyPhaseAsync(
        DuplicateRun run,
        DuplicateScanOptions options,
        IDuplicateDetectionStrategy strategy,
        IProgress<DuplicateDetectionProgress>? progress,
        ConcurrentBag<DuplicateSignature> allSignatures,
        ConcurrentBag<DuplicateError> allErrors,
        List<(DuplicateGroup, List<DuplicateGroupMember>)> allGroupPairs,
        long[] nextOrdinal,   // [0] = next group ordinal
        CancellationToken ct)
    {
        // 1. Candidates
        var query = strategy.BuildCandidateQuery(options);
        var candidates = await candidateProvider.GetCandidatesAsync(query, ct);
        if (candidates.Count == 0) return;

        // 2. Load cached signatures
        var cached = await repository.GetCachedSignaturesAsync(
            options.SessionId, strategy.Method, strategy.Algorithm, strategy.AlgorithmVersion, ct);
        var cacheMap = cached.ToDictionary(s => s.FileEntryId);
        var perDriveSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        // 3. Determine which candidates need fresh signatures
        var needsSig = new List<DuplicateCandidate>(candidates.Count);
        var sigsByKey = new ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>>(StringComparer.Ordinal);
        var groupsFound = new int[] { 0 };  // boxed counter — passed to async helpers

        foreach (var c in candidates)
        {
            if (cacheMap.TryGetValue(c.File.Id, out var cached_sig) &&
                IsCacheValid(cached_sig, c.File, strategy.AlgorithmVersion))
            {
                // Reuse cached signature
                allSignatures.Add(cached_sig);
                if (cached_sig.SignatureText is not null)
                    sigsByKey.GetOrAdd(cached_sig.SignatureText, static _ => []).Add(c);
            }
            else
            {
                needsSig.Add(c);
            }
        }

        // 4. Compute missing signatures
        if (needsSig.Count > 0)
        {
            if (strategy.UsePartialHashPreFilter)
            {
                await RunWithPartialHashPreFilterAsync(
                    run, options, strategy, progress,
                    needsSig, allSignatures, allErrors,
                    sigsByKey, groupsFound, perDriveSemaphores, ct);
            }
            else
            {
                await ComputeSignaturesAsync(
                    run, options, strategy, progress,
                    needsSig, needsSig.Count, allSignatures, allErrors,
                    sigsByKey, groupsFound, perDriveSemaphores, ct);
            }
        }

        // 5. Build duplicate groups from signature clusters
        var signatureGroupsSnapshot = sigsByKey
            .ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<DuplicateCandidate>)kv.Value.ToList());

        foreach (var match in strategy.BuildMatches(signatureGroupsSnapshot))
        {
            allGroupPairs.Add(CreateGroup(
                keeperPolicy, run.Id, nextOrdinal,
                strategy, match, options.KeeperPolicy));
            groupsFound[0]++;
        }

        logger.LogInformation(
            "Run {RunId}: {Method} phase done — {Candidates} candidates, {Groups} groups, {Errors} errors",
            run.Id, strategy.Method, candidates.Count, groupsFound[0], allErrors.Count);
    }

    // ── Partial-hash pre-filter (ExactSha256) ─────────────────────────────────

    private async Task RunWithPartialHashPreFilterAsync(
        DuplicateRun run,
        DuplicateScanOptions options,
        IDuplicateDetectionStrategy strategy,
        IProgress<DuplicateDetectionProgress>? progress,
        IReadOnlyList<DuplicateCandidate> candidates,
        ConcurrentBag<DuplicateSignature> allSignatures,
        ConcurrentBag<DuplicateError> allErrors,
        ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>> sigsByKey,
        int[] groupsFound,   // [0] = count
        ConcurrentDictionary<string, SemaphoreSlim> perDriveSemaphores,
        CancellationToken ct)
    {
        // Group candidates by size — the SQL already filters to multi-occurrence
        // sizes, but we bucket in memory here to drive partial-hash clustering.
        var bySizeGroups = candidates
            .GroupBy(static c => c.File.SizeBytes)
            .Where(static g => g.Count() > 1)
            .ToList();

        // Validate existence + current size before any I/O
        var totalToHash = 0;
        var validBySizeGroups = new List<(long Size, List<DuplicateCandidate> Valid)>();
        foreach (var sizeGroup in bySizeGroups)
        {
            var valid = new List<DuplicateCandidate>(sizeGroup.Count());
            foreach (var c in sizeGroup)
            {
                var info = new FileInfo(c.File.FullPath);
                if (!info.Exists || info.Length != c.File.SizeBytes) continue;
                valid.Add(c);
            }
            if (valid.Count > 1)
            {
                validBySizeGroups.Add((sizeGroup.Key, valid));
                totalToHash += valid.Count;
            }
        }

        if (totalToHash == 0) return;

        var processed = 0;

        foreach (var (_, validCandidates) in validBySizeGroups)
        {
            ct.ThrowIfCancellationRequested();

            // Partial hash — cluster, then full-hash only clusters with > 1 member
            var partialGroups = new ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(validCandidates, new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
            }, async (c, token) =>
            {
                var driveKey = GetDriveKey(c.File.FullPath);
                var driveGate = perDriveSemaphores.GetOrAdd(
                    driveKey,
                    _ => new SemaphoreSlim(Math.Max(1, options.PerDriveConcurrency)));
                var acquired = false;
                try
                {
                    await driveGate.WaitAsync(token);
                    acquired = true;
                    var ph = await hasher.ComputePartialHashAsync(c.File.FullPath, token);
                    partialGroups.GetOrAdd(ph, static _ => []).Add(c);
                }
                catch (Exception ex)
                {
                    allErrors.Add(MakeError(run.Id, c, "PartialHash", ex.Message));
                }
                finally
                {
                    if (acquired)
                        driveGate.Release();
                    var cur = Interlocked.Increment(ref processed);
                    progress?.Report(new DuplicateDetectionProgress
                    {
                        RunId = run.Id,
                        Phase = "Partial hash pre-filter",
                        Method = strategy.Method,
                        Processed = cur,
                        Total = totalToHash,
                        CurrentPath = c.File.FullPath,
                        GroupsFound = groupsFound[0],
                        Errors = allErrors.Count,
                        CanCancel = true,
                    });
                }
            });

            // Full-hash only within partial-hash clusters
            foreach (var cluster in partialGroups.Values.Where(static g => g.Count > 1))
            {
                await ComputeSignaturesAsync(
                    run, options, strategy, progress,
                    cluster.ToList(), totalToHash, allSignatures, allErrors,
                    sigsByKey, groupsFound, perDriveSemaphores, ct);
            }
        }
    }

    // ── Parallel signature computation ────────────────────────────────────────

    private async Task ComputeSignaturesAsync(
        DuplicateRun run,
        DuplicateScanOptions options,
        IDuplicateDetectionStrategy strategy,
        IProgress<DuplicateDetectionProgress>? progress,
        IReadOnlyList<DuplicateCandidate> candidates,
        int phaseTotal,
        ConcurrentBag<DuplicateSignature> allSignatures,
        ConcurrentBag<DuplicateError> allErrors,
        ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>> sigsByKey,
        int[] groupsFound,
        ConcurrentDictionary<string, SemaphoreSlim> perDriveSemaphores,
        CancellationToken ct)
    {
        var processed = 0;
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
        }, async (c, token) =>
        {
            var driveKey = GetDriveKey(c.File.FullPath);
            var driveGate = perDriveSemaphores.GetOrAdd(
                driveKey,
                _ => new SemaphoreSlim(Math.Max(1, options.PerDriveConcurrency)));
            var acquired = false;
            await driveGate.WaitAsync(token);
            acquired = true;
            try
            {
                var sig = await strategy.ComputeSignatureAsync(c, token);
                allSignatures.Add(sig);

                if (sig.Status == "Ready" && sig.SignatureText is not null)
                    sigsByKey.GetOrAdd(sig.SignatureText, static _ => []).Add(c);
                else if (sig.Status == "Error" && sig.ErrorMessage is not null)
                    allErrors.Add(MakeError(run.Id, c, sig.ErrorMessage.Contains("Changed") ? "FileChangedDuringHash" : "SignatureError", sig.ErrorMessage ?? string.Empty));
            }
            finally
            {
                if (acquired)
                    driveGate.Release();
            }

            var cur = Interlocked.Increment(ref processed);
            progress?.Report(new DuplicateDetectionProgress
            {
                RunId = run.Id,
                Phase = strategy.DisplayName,
                Method = strategy.Method,
                Processed = cur,
                Total = Math.Max(phaseTotal, 1),
                CurrentPath = c.File.FullPath,
                GroupsFound = groupsFound[0],
                Errors = allErrors.Count,
                CanCancel = true,
            });
        });
    }

    // ── Group creation ────────────────────────────────────────────────────────

    private static (DuplicateGroup, List<DuplicateGroupMember>) CreateGroup(
        IDuplicateKeeperPolicy keeperPolicy,
        long runId,
        long[] nextOrdinal,
        IDuplicateDetectionStrategy strategy,
        DuplicateStrategyMatch match,
        KeeperPolicy policy)
    {
        var keeper = keeperPolicy.ChooseKeeper(match.Candidates, policy);
        var totalBytes = match.Candidates.Sum(static c => c.File.SizeBytes);
        var reclaimable = totalBytes - keeper.File.SizeBytes;
        var groupId = Interlocked.Increment(ref nextOrdinal[0]) - 1;

        var groupRecord = new DuplicateGroup
        {
            Id = groupId,
            RunId = runId,
            Method = strategy.Method,
            Algorithm = strategy.Algorithm,
            Confidence = match.Confidence,
            TotalBytes = totalBytes,
            ReclaimableBytes = reclaimable,
            RepresentativeFileEntryId = keeper.File.Id,
        };

        var members = match.Candidates
            .Select(c => new DuplicateGroupMember
            {
                Id = 0,
                GroupId = groupId,
                FileEntryId = c.File.Id,
                FullPath = c.File.FullPath,
                FileName = c.File.FileName,
                SizeBytes = c.File.SizeBytes,
                ModifiedUtc = c.File.ModifiedUtc,
                Score = match.Confidence,
                IsKeeper = c.File.Id == keeper.File.Id,
                IsSelected = strategy.SupportsAutoSelection && c.File.Id != keeper.File.Id,
                RecommendationReason = c.File.Id == keeper.File.Id
                    ? $"Kept by policy {policy}"
                    : match.ReasonText,
                ExistsNow = true,
            })
            .OrderBy(static m => m.IsKeeper ? 0 : 1)
            .ThenBy(static m => m.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (groupRecord, members);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static bool IsCacheValid(DuplicateSignature cached, FileEntry current, int currentAlgorithmVersion) =>
        cached.Status == "Ready" &&
        cached.AlgorithmVersion == currentAlgorithmVersion &&
        cached.SourceSizeBytes == current.SizeBytes &&
        cached.SourceModifiedUtc == current.ModifiedUtc;

    private static DuplicateError MakeError(long runId, DuplicateCandidate c, string type, string msg) =>
        new()
        {
            Id = 0,
            RunId = runId,
            FileEntryId = c.File.Id,
            Path = c.File.FullPath,
            ErrorType = type,
            Message = msg,
            OccurredUtc = DateTime.UtcNow,
        };

    private static string GetDriveKey(string path)
    {
        try
        {
            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
