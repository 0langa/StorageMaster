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
///   5.  Upsert newly computed signatures into DB (reused cache hits are already there).
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
    ILogger<DuplicateFinderService> logger,
    IFileSnapshotProvider? snapshotProvider = null) : IDuplicateFinderService
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
        // Freshly computed signatures only. Reused cache hits are already stored
        // — writing them back would re-upsert every unchanged row of the session
        // inside the write lock for no change, so only their count is carried.
        var freshSignatures = new ConcurrentBag<DuplicateSignature>();
        var reusedSignatureCount = new int[] { 0 };
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
                    freshSignatures, reusedSignatureCount, allErrors, allGroupPairs,
                    nextOrdinal, ct);
            }

            var groupRecords = allGroupPairs.Select(static p => p.Group).ToList();
            var memberRecords = allGroupPairs.SelectMany(static p => p.Members).ToList();

            await repository.SaveResultsAsync(
                run.Id,
                [.. freshSignatures],
                groupRecords,
                memberRecords,
                [.. allErrors],
                ct);

            await repository.CompleteRunAsync(
                run.Id,
                DuplicateRunStatus.Completed,
                freshSignatures.Count + reusedSignatureCount[0],
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
        ConcurrentBag<DuplicateSignature> freshSignatures,
        int[] reusedSignatureCount,   // [0] = cache hits reused across all phases
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

        var reusable = await SelectReusableCacheHitsAsync(
            run, options, strategy, progress,
            candidates, cacheMap, groupsFound, allErrors, perDriveSemaphores, ct);

        // Sizes already covered by a reusable cached signature. The pre-filter
        // buckets only the candidates it is about to hash, so a candidate whose
        // same-size partners all came from the cache would otherwise look like a
        // singleton bucket and be dropped without ever being hashed.
        var cachedSizes = new HashSet<long>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (reusable[i] is { } cachedSignature)
            {
                // Reuse cached signature — already persisted, so it is counted but
                // not handed back to SaveResultsAsync.
                reusedSignatureCount[0]++;
                if (cachedSignature.SignatureText is not null)
                {
                    sigsByKey.GetOrAdd(cachedSignature.SignatureText, static _ => []).Add(c);
                    cachedSizes.Add(c.File.SizeBytes);
                }
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
                    needsSig, cachedSizes, freshSignatures, allErrors,
                    sigsByKey, groupsFound, perDriveSemaphores, ct);
            }
            else
            {
                await ComputeSignaturesAsync(
                    run, options, strategy, progress,
                    needsSig, needsSig.Count, freshSignatures, allErrors,
                    sigsByKey, groupsFound, new int[] { 0 }, perDriveSemaphores, ct);
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

    // ── Cached-signature reuse ────────────────────────────────────────────────

    /// <summary>
    /// Decides, per candidate, whether its cached signature can be reused.
    ///
    /// The cheap comparisons (status, algorithm version, recorded size/mtime) run
    /// inline; only the survivors pay for a live snapshot, and those snapshots go
    /// through the same per-drive gate as hashing instead of running one at a time
    /// on the caller's thread — on a re-run this pass is otherwise the entire
    /// visible wait before any progress appears. Results are indexed by candidate
    /// position so the cached/uncached partition stays deterministic.
    /// </summary>
    private async Task<DuplicateSignature?[]> SelectReusableCacheHitsAsync(
        DuplicateRun run,
        DuplicateScanOptions options,
        IDuplicateDetectionStrategy strategy,
        IProgress<DuplicateDetectionProgress>? progress,
        IReadOnlyList<DuplicateCandidate> candidates,
        IReadOnlyDictionary<long, DuplicateSignature> cacheMap,
        int[] groupsFound,
        ConcurrentBag<DuplicateError> allErrors,
        ConcurrentDictionary<string, SemaphoreSlim> perDriveSemaphores,
        CancellationToken ct)
    {
        var reusable = new DuplicateSignature?[candidates.Count];
        var needsLiveCheck = new List<int>();

        for (var i = 0; i < candidates.Count; i++)
        {
            if (cacheMap.TryGetValue(candidates[i].File.Id, out var cachedSignature) &&
                IsCacheValid(cachedSignature, candidates[i].File, strategy.AlgorithmVersion))
            {
                reusable[i] = cachedSignature;
                if (snapshotProvider is not null)
                    needsLiveCheck.Add(i);
            }
        }

        if (needsLiveCheck.Count == 0)
            return reusable;

        var processed = 0;
        await Parallel.ForEachAsync(needsLiveCheck, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
        }, async (index, token) =>
        {
            var c = candidates[index];
            var driveGate = perDriveSemaphores.GetOrAdd(
                GetDriveKey(c.File.FullPath),
                _ => new SemaphoreSlim(Math.Max(1, options.PerDriveConcurrency)));
            await driveGate.WaitAsync(token);
            try
            {
                // Each index is touched by exactly one iteration, so no interlocking.
                if (!await MatchesLiveFileAsync(reusable[index]!, c.File, token))
                    reusable[index] = null;
            }
            finally
            {
                driveGate.Release();
            }

            var cur = Interlocked.Increment(ref processed);
            progress?.Report(new DuplicateDetectionProgress
            {
                RunId = run.Id,
                Phase = "Validating cached signatures",
                Method = strategy.Method,
                Processed = cur,
                Total = Math.Max(needsLiveCheck.Count, 1),
                CurrentPath = c.File.FullPath,
                GroupsFound = groupsFound[0],
                Errors = allErrors.Count,
                CanCancel = true,
            });
        });

        return reusable;
    }

    // ── Partial-hash pre-filter (ExactSha256) ─────────────────────────────────

    private async Task RunWithPartialHashPreFilterAsync(
        DuplicateRun run,
        DuplicateScanOptions options,
        IDuplicateDetectionStrategy strategy,
        IProgress<DuplicateDetectionProgress>? progress,
        IReadOnlyList<DuplicateCandidate> candidates,
        IReadOnlySet<long> cachedSizes,   // sizes already served from the signature cache
        ConcurrentBag<DuplicateSignature> freshSignatures,
        ConcurrentBag<DuplicateError> allErrors,
        ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>> sigsByKey,
        int[] groupsFound,   // [0] = count
        ConcurrentDictionary<string, SemaphoreSlim> perDriveSemaphores,
        CancellationToken ct)
    {
        // Group candidates by size — the SQL already filters to multi-occurrence
        // sizes, but we bucket in memory here to drive partial-hash clustering.
        // A bucket of one is still real work when a same-size partner was served
        // from the cache: that partner is not in this list, so dropping singletons
        // outright would lose the pair with no signature and no error to explain it.
        var bySizeGroups = candidates
            .GroupBy(static c => c.File.SizeBytes)
            .Where(g => g.Count() > 1 || cachedSizes.Contains(g.Key))
            .ToList();

        // Validate existence + current size before any I/O
        var totalToHash = 0;
        var validBySizeGroups = new List<(List<DuplicateCandidate> Valid, bool HasCachedPartner)>();
        foreach (var sizeGroup in bySizeGroups)
        {
            var valid = new List<DuplicateCandidate>(sizeGroup.Count());
            foreach (var c in sizeGroup)
            {
                var info = new FileInfo(c.File.FullPath);
                if (!info.Exists || info.Length != c.File.SizeBytes) continue;
                valid.Add(c);
            }

            var hasCachedPartner = cachedSizes.Contains(sizeGroup.Key);
            if (valid.Count > 1 || (valid.Count == 1 && hasCachedPartner))
            {
                validBySizeGroups.Add((valid, hasCachedPartner));
                totalToHash += valid.Count;
            }
        }

        if (totalToHash == 0) return;

        var processed = 0;
        // Hoisted out of ComputeSignaturesAsync: that method runs once per cluster,
        // so a method-local counter restarted at 1 for every pair and the reported
        // full-hash progress oscillated instead of advancing.
        var fullHashProcessed = new int[] { 0 };

        foreach (var (validCandidates, hasCachedPartner) in validBySizeGroups)
        {
            ct.ThrowIfCancellationRequested();

            if (hasCachedPartner)
            {
                // A cached signature carries only the full hash, so a partial-hash
                // cluster cannot rule any of these out — every member needs its full
                // hash anyway and the pre-filter read would be pure waste.
                await ComputeSignaturesAsync(
                    run, options, strategy, progress,
                    validCandidates, totalToHash, freshSignatures, allErrors,
                    sigsByKey, groupsFound, fullHashProcessed, perDriveSemaphores, ct);
                continue;
            }

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
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
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
                    cluster.ToList(), totalToHash, freshSignatures, allErrors,
                    sigsByKey, groupsFound, fullHashProcessed, perDriveSemaphores, ct);
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
        ConcurrentBag<DuplicateSignature> freshSignatures,
        ConcurrentBag<DuplicateError> allErrors,
        ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>> sigsByKey,
        int[] groupsFound,
        int[] processed,   // [0] = phase-scoped counter, shared across clusters
        ConcurrentDictionary<string, SemaphoreSlim> perDriveSemaphores,
        CancellationToken ct)
    {
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
                freshSignatures.Add(sig);

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

            var cur = Interlocked.Increment(ref processed[0]);
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
                Attributes = c.File.Attributes,
                Identity = c.Identity ?? c.File.Identity,
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

    /// <summary>
    /// Second half of cache validation: the live file must still be the exact file
    /// the cached signature was computed from. Split from <see cref="IsCacheValid"/>
    /// so the cheap comparisons can gate the snapshot syscall.
    /// </summary>
    private async ValueTask<bool> MatchesLiveFileAsync(
        DuplicateSignature cached,
        FileEntry current,
        CancellationToken ct)
    {
        if (snapshotProvider is null)
            return true;

        ct.ThrowIfCancellationRequested();
        var live = await snapshotProvider.TakeSnapshotAsync(current.FullPath, ct);
        ct.ThrowIfCancellationRequested();

        if (live is null ||
            live.SizeBytes != current.SizeBytes ||
            live.LastWriteUtc != current.ModifiedUtc ||
            live.Attributes != current.Attributes ||
            live.Identity is not { } liveIdentity ||
            cached.SourceFileIdentity is null)
        {
            return false;
        }

        var canonicalLiveIdentity = string.Concat(
            liveIdentity.VolumeSerial,
            ":",
            liveIdentity.FileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return string.Equals(
            canonicalLiveIdentity,
            cached.SourceFileIdentity,
            StringComparison.OrdinalIgnoreCase);
    }

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
