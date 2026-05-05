using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

public sealed class DuplicateFinderService(
    IDuplicateRepository repository,
    IDuplicateCandidateProvider candidateProvider,
    IFileContentHasher hasher,
    IEnumerable<IDuplicateSignatureProvider> signatureProviders,
    IDuplicateKeeperPolicy keeperPolicy,
    IFileIdentityProvider fileIdentityProvider,
    ILogger<DuplicateFinderService> logger) : IDuplicateFinderService
{
    public async Task<DuplicateRun> RunAsync(
        DuplicateScanOptions options,
        IProgress<DuplicateDetectionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var run = await repository.CreateRunAsync(options, ct);
        var signatures = new ConcurrentBag<DuplicateSignature>();
        var errors = new ConcurrentBag<DuplicateError>();
        var providers = signatureProviders.ToDictionary(provider => provider.Method);

        try
        {
            var allCandidates = await candidateProvider.GetExactCandidatesAsync(options, ct);
            var processed = 0;
            var exactGroups = new List<(DuplicateGroup Group, List<DuplicateGroupMember> Members)>();
            var nextGroupOrdinal = 1L;

            if (options.Methods.Contains(DuplicateMethod.ExactSha256))
            {
                var candidatesBySize = allCandidates
                    .GroupBy(static candidate => candidate.File.SizeBytes)
                    .Where(static group => group.Count() > 1)
                    .ToList();
                var total = candidatesBySize.Sum(static group => group.Count());

                foreach (var sizeGroup in candidatesBySize)
                {
                    ct.ThrowIfCancellationRequested();

                    var existingCandidates = sizeGroup
                        .Where(static candidate => File.Exists(candidate.File.FullPath))
                        .Where(static candidate => candidate.File.SizeBytes > 0)
                        .ToList();

                    var validCandidates = new List<DuplicateCandidate>(existingCandidates.Count);
                    foreach (var candidate in existingCandidates)
                    {
                        try
                        {
                            var info = new FileInfo(candidate.File.FullPath);
                            if (!info.Exists || info.Length != candidate.File.SizeBytes)
                                continue;

                            validCandidates.Add(candidate with
                            {
                                Identity = await fileIdentityProvider.GetIdentityAsync(candidate.File.FullPath, ct)
                            });
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new DuplicateError
                            {
                                Id = 0,
                                RunId = run.Id,
                                FileEntryId = candidate.File.Id,
                                Path = candidate.File.FullPath,
                                ErrorType = "Validation",
                                Message = ex.Message,
                                OccurredUtc = DateTime.UtcNow,
                            });
                        }
                    }

                    var partialGroups = new ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>>(StringComparer.Ordinal);
                    await Parallel.ForEachAsync(validCandidates, new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                    }, async (candidate, token) =>
                    {
                        try
                        {
                            var partialHash = await hasher.ComputePartialHashAsync(candidate.File.FullPath, token);
                            partialGroups.GetOrAdd(partialHash, static _ => []).Add(candidate);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new DuplicateError
                            {
                                Id = 0,
                                RunId = run.Id,
                                FileEntryId = candidate.File.Id,
                                Path = candidate.File.FullPath,
                                ErrorType = "PartialHash",
                                Message = ex.Message,
                                OccurredUtc = DateTime.UtcNow,
                            });
                        }
                        finally
                        {
                            var current = Interlocked.Increment(ref processed);
                            progress?.Report(new DuplicateDetectionProgress(
                                current,
                                total,
                                candidate.File.FullPath,
                                "Hashing exact candidates"));
                        }
                    });

                    foreach (var partialGroup in partialGroups.Values.Where(static group => group.Count > 1))
                    {
                        var shaGroups = new ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>>(StringComparer.Ordinal);
                        await Parallel.ForEachAsync(partialGroup, new ParallelOptions
                        {
                            CancellationToken = ct,
                            MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                        }, async (candidate, token) =>
                        {
                            try
                            {
                                var signature = await providers[DuplicateMethod.ExactSha256].ComputeAsync(candidate, token);
                                signatures.Add(signature);
                                shaGroups.GetOrAdd(signature.SignatureText!, static _ => []).Add(candidate);
                            }
                            catch (Exception ex)
                            {
                                errors.Add(new DuplicateError
                                {
                                    Id = 0,
                                    RunId = run.Id,
                                    FileEntryId = candidate.File.Id,
                                    Path = candidate.File.FullPath,
                                    ErrorType = "Sha256",
                                    Message = ex.Message,
                                    OccurredUtc = DateTime.UtcNow,
                                });
                            }
                        });

                        foreach (var shaGroup in shaGroups.Values.Where(static group => group.Count > 1))
                        {
                            var distinctFiles = shaGroup
                                .GroupBy(static candidate => candidate.Identity is null
                                    ? candidate.File.FullPath
                                    : $"{candidate.Identity.VolumeSerial}:{candidate.Identity.FileIndex}")
                                .Select(static group => group.First())
                                .ToList();

                            if (distinctFiles.Count < 2)
                                continue;

                            exactGroups.Add(CreateGroup(
                                keeperPolicy,
                                run.Id,
                                ref nextGroupOrdinal,
                                DuplicateMethod.ExactSha256,
                                "SHA-256",
                                distinctFiles,
                                options.KeeperPolicy,
                                autoSelectDuplicates: true,
                                confidence: 1.0d,
                                reasonText: "Exact byte duplicate"));
                        }
                    }
                }
            }

            if (options.Methods.Contains(DuplicateMethod.NormalizedText)
                && providers.TryGetValue(DuplicateMethod.NormalizedText, out var normalizedProvider))
            {
                var textCandidates = allCandidates
                    .Where(static candidate => NormalizedTextSignatureProvider.CanProcess(candidate.File))
                    .ToList();
                var total = textCandidates.Count;

                var normalizedGroups = new ConcurrentDictionary<string, ConcurrentBag<DuplicateCandidate>>(StringComparer.Ordinal);
                await Parallel.ForEachAsync(textCandidates, new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                }, async (candidate, token) =>
                {
                    try
                    {
                        var signature = await normalizedProvider.ComputeAsync(candidate, token);
                        signatures.Add(signature);
                        normalizedGroups.GetOrAdd(signature.SignatureText!, static _ => []).Add(candidate);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new DuplicateError
                        {
                            Id = 0,
                            RunId = run.Id,
                            FileEntryId = candidate.File.Id,
                            Path = candidate.File.FullPath,
                            ErrorType = "NormalizedText",
                            Message = ex.Message,
                            OccurredUtc = DateTime.UtcNow,
                        });
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref processed);
                        progress?.Report(new DuplicateDetectionProgress(
                            current,
                            Math.Max(total, 1),
                            candidate.File.FullPath,
                            "Normalizing text duplicates"));
                    }
                });

                foreach (var group in normalizedGroups.Values.Where(static candidates => candidates.Count > 1))
                {
                    var distinctFiles = group
                        .GroupBy(static candidate => candidate.File.FullPath, StringComparer.OrdinalIgnoreCase)
                        .Select(static duplicateGroup => duplicateGroup.First())
                        .ToList();
                    if (distinctFiles.Count < 2)
                        continue;

                    exactGroups.Add(CreateGroup(
                        keeperPolicy,
                        run.Id,
                        ref nextGroupOrdinal,
                        DuplicateMethod.NormalizedText,
                        "TEXT-NORM-SHA256",
                        distinctFiles,
                        options.KeeperPolicy,
                        autoSelectDuplicates: false,
                        confidence: 0.8d,
                        reasonText: "Normalized text review"));
                }
            }

            var groupRecords = exactGroups.Select(static pair => pair.Group).ToList();
            var memberRecords = exactGroups.SelectMany(static pair => pair.Members).ToList();

            await repository.SaveResultsAsync(
                run.Id,
                [.. signatures],
                groupRecords,
                memberRecords,
                [.. errors],
                ct);

            await repository.CompleteRunAsync(
                run.Id,
                DuplicateRunStatus.Completed,
                allCandidates.Count,
                groupRecords.Count,
                groupRecords.Sum(static group => group.TotalBytes),
                groupRecords.Sum(static group => group.ReclaimableBytes),
                errors.Count,
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            await repository.CompleteRunAsync(run.Id, DuplicateRunStatus.Cancelled, 0, 0, 0, 0, errors.Count, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Duplicate run {RunId} failed", run.Id);
            await repository.CompleteRunAsync(run.Id, DuplicateRunStatus.Failed, 0, 0, 0, 0, errors.Count, ex.Message, CancellationToken.None);
        }

        var completedRun = (await repository.GetRunsForSessionAsync(options.SessionId, CancellationToken.None))
            .First(candidateRun => candidateRun.Id == run.Id);
        return completedRun;
    }

    private static (DuplicateGroup Group, List<DuplicateGroupMember> Members) CreateGroup(
        IDuplicateKeeperPolicy keeperPolicy,
        long runId,
        ref long nextGroupOrdinal,
        DuplicateMethod method,
        string algorithm,
        IReadOnlyList<DuplicateCandidate> candidates,
        KeeperPolicy keeperPolicyValue,
        bool autoSelectDuplicates,
        double confidence,
        string reasonText)
    {
        var keeper = keeperPolicy.ChooseKeeper(candidates, keeperPolicyValue);
        var totalBytes = candidates.Sum(static candidate => candidate.File.SizeBytes);
        var reclaimableBytes = totalBytes - keeper.File.SizeBytes;
        var groupId = nextGroupOrdinal++;

        var groupRecord = new DuplicateGroup
        {
            Id = groupId,
            RunId = runId,
            Method = method,
            Algorithm = algorithm,
            Confidence = confidence,
            TotalBytes = totalBytes,
            ReclaimableBytes = reclaimableBytes,
            RepresentativeFileEntryId = keeper.File.Id,
        };

        var members = candidates
            .Select(candidate => new DuplicateGroupMember
            {
                Id = 0,
                GroupId = groupId,
                FileEntryId = candidate.File.Id,
                FullPath = candidate.File.FullPath,
                FileName = candidate.File.FileName,
                SizeBytes = candidate.File.SizeBytes,
                ModifiedUtc = candidate.File.ModifiedUtc,
                Score = confidence,
                IsKeeper = candidate.File.Id == keeper.File.Id,
                IsSelected = autoSelectDuplicates && candidate.File.Id != keeper.File.Id,
                RecommendationReason = candidate.File.Id == keeper.File.Id
                    ? DescribeKeeperReason(true, keeperPolicyValue)
                    : reasonText,
                ExistsNow = true,
            })
            .OrderBy(static member => member.IsKeeper ? 0 : 1)
            .ThenBy(static member => member.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (groupRecord, members);
    }

    private static string DescribeKeeperReason(bool isKeeper, KeeperPolicy policy) =>
        isKeeper
            ? $"Kept by policy {policy}"
            : "Duplicate copy";
}
