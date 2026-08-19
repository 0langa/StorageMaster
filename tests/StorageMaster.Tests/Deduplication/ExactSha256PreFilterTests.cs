using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.Deduplication;

/// <summary>
/// Covers the size-bucket pre-filter that gates full SHA-256 hashing. The
/// pre-filter only ever sees the candidates that still need a signature, so it
/// has to be told which sizes the signature cache already covers — otherwise a
/// candidate whose only same-size partners were cache hits looks like a singleton
/// bucket and is dropped with no signature and no error.
/// </summary>
public sealed class ExactSha256PreFilterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sm_dupprefilter_{Guid.NewGuid():N}");

    public ExactSha256PreFilterTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task RunAsync_UncachedFileWhoseSamePartnersAreCacheHits_IsStillHashedAndGrouped()
    {
        const long sessionId = 501;
        const string content = "identical-bytes";

        var pathA1 = WriteTempFile("a1.bin", content);
        var pathA2 = WriteTempFile("a2.bin", content);
        var pathB1 = WriteTempFile("b1.bin", content);

        var a1 = new DuplicateCandidate(MakeFileEntry(1, pathA1), new FileIdentity("VOL", 1));
        var a2 = new DuplicateCandidate(MakeFileEntry(2, pathA2), new FileIdentity("VOL", 2));
        var b1 = new DuplicateCandidate(MakeFileEntry(3, pathB1), new FileIdentity("VOL", 3));

        var hasher = new FileContentHasher();
        var sharedHash = await hasher.ComputeSha256Async(pathA1);

        // A1/A2 were hashed by an earlier run against the same session; B1 came in
        // when the user widened the scope, so it has no cached row.
        var repo = BuildRepositoryMock(sessionId, out var savedGroups, out var savedMembers, out var savedErrors);
        repo.Setup(r => r.GetCachedSignaturesAsync(
                sessionId, DuplicateMethod.ExactSha256, "SHA-256", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([CachedSignature(a1.File, sharedHash), CachedSignature(a2.File, sharedHash)]);

        var run = await BuildService(repo, hasher, a1, a2, b1).RunAsync(new DuplicateScanOptions
        {
            SessionId = sessionId,
            Methods = [DuplicateMethod.ExactSha256],
            MinimumSizeBytes = 0,
        });

        run.Status.Should().Be(DuplicateRunStatus.Completed);
        savedErrors.Should().BeEmpty();
        savedGroups.Should().ContainSingle();
        // B1 is a singleton inside the uncached set; only the cached partners' sizes
        // reveal that it still has to be hashed.
        savedMembers.Select(m => m.FileEntryId).Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
    }

    [Fact]
    public async Task RunAsync_MultiplePartialHashClusters_ReportsMonotonicFullHashProgress()
    {
        const long sessionId = 502;

        // Four equally sized files forming two distinct content pairs: one size
        // bucket, two partial-hash clusters, so the full-hash pass runs twice.
        var candidates = new[]
        {
            new DuplicateCandidate(MakeFileEntry(11, WriteTempFile("p1.bin", "aaaa1"))),
            new DuplicateCandidate(MakeFileEntry(12, WriteTempFile("p2.bin", "aaaa1"))),
            new DuplicateCandidate(MakeFileEntry(13, WriteTempFile("q1.bin", "bbbb2"))),
            new DuplicateCandidate(MakeFileEntry(14, WriteTempFile("q2.bin", "bbbb2"))),
        };

        var repo = BuildRepositoryMock(sessionId, out var savedGroups, out _, out _);
        var fullHashProgress = new ConcurrentQueue<int>();
        var progress = new InlineProgress<DuplicateDetectionProgress>(p =>
        {
            if (p.Phase == "Exact SHA-256")
                fullHashProgress.Enqueue(p.Processed);
        });

        await BuildService(repo, new FileContentHasher(), candidates).RunAsync(
            new DuplicateScanOptions
            {
                SessionId = sessionId,
                Methods = [DuplicateMethod.ExactSha256],
                MinimumSizeBytes = 0,
                MaxConcurrency = 1,
                PerDriveConcurrency = 1,
            },
            progress);

        savedGroups.Should().HaveCount(2);
        // The full-hash counter is phase-scoped: restarting it per cluster made the
        // reported progress oscillate 1, 2, 1, 2 instead of advancing.
        fullHashProgress.Should().Equal(new[] { 1, 2, 3, 4 });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>Reports on the calling thread — <see cref="Progress{T}"/> posts
    /// asynchronously, which would make the ordering assertion racy.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static DuplicateFinderService BuildService(
        Mock<IDuplicateRepository> repo,
        IFileContentHasher hasher,
        params DuplicateCandidate[] candidates)
    {
        var provider = new Mock<IDuplicateCandidateProvider>();
        provider.Setup(p => p.GetCandidatesAsync(It.IsAny<DuplicateCandidateQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates);

        // No finder-level snapshot provider: cache validity then rests on the
        // recorded size/mtime, which keeps the fixture independent of real
        // volume serials while still exercising the cached/uncached split.
        return new DuplicateFinderService(
            repo.Object,
            provider.Object,
            hasher,
            [new ExactSha256Strategy(hasher, new FileSnapshotProvider())],
            new DuplicateKeeperPolicy(),
            NullLogger<DuplicateFinderService>.Instance);
    }

    private static Mock<IDuplicateRepository> BuildRepositoryMock(
        long sessionId,
        out List<DuplicateGroup> savedGroups,
        out List<DuplicateGroupMember> savedMembers,
        out List<DuplicateError> savedErrors)
    {
        var groups = new List<DuplicateGroup>();
        var members = new List<DuplicateGroupMember>();
        var errors = new List<DuplicateError>();
        savedGroups = groups;
        savedMembers = members;
        savedErrors = errors;

        var run = new DuplicateRun
        {
            Id = 9000 + sessionId,
            SessionId = sessionId,
            StartedUtc = DateTime.UtcNow,
            Status = DuplicateRunStatus.Running,
            ConfigJson = "{}",
        };

        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(r => r.CreateRunAsync(It.IsAny<DuplicateScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        repo.Setup(r => r.GetCachedSignaturesAsync(
                sessionId, It.IsAny<DuplicateMethod>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repo.Setup(r => r.SaveResultsAsync(
                run.Id,
                It.IsAny<IReadOnlyList<DuplicateSignature>>(),
                It.IsAny<IReadOnlyList<DuplicateGroup>>(),
                It.IsAny<IReadOnlyList<DuplicateGroupMember>>(),
                It.IsAny<IReadOnlyList<DuplicateError>>(),
                It.IsAny<CancellationToken>()))
            .Callback<long, IReadOnlyList<DuplicateSignature>, IReadOnlyList<DuplicateGroup>, IReadOnlyList<DuplicateGroupMember>, IReadOnlyList<DuplicateError>, CancellationToken>(
                (_, _, g, m, e, _) =>
                {
                    groups.Clear();
                    groups.AddRange(g);
                    members.Clear();
                    members.AddRange(m);
                    errors.Clear();
                    errors.AddRange(e);
                })
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.CompleteRunAsync(
                run.Id, It.IsAny<DuplicateRunStatus>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.GetRunsForSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            [
                new DuplicateRun
                {
                    Id = run.Id,
                    SessionId = sessionId,
                    StartedUtc = run.StartedUtc,
                    CompletedUtc = DateTime.UtcNow,
                    Status = DuplicateRunStatus.Completed,
                    ConfigJson = "{}",
                    GroupCount = groups.Count,
                },
            ]);
        return repo;
    }

    private static DuplicateSignature CachedSignature(FileEntry file, string hash) => new()
    {
        Id = file.Id,
        SessionId = file.SessionId,
        FileEntryId = file.Id,
        Method = DuplicateMethod.ExactSha256,
        Algorithm = "SHA-256",
        AlgorithmVersion = 1,
        SignatureText = hash,
        ComputedUtc = DateTime.UtcNow,
        Status = "Ready",
        SourceSizeBytes = file.SizeBytes,
        SourceModifiedUtc = file.ModifiedUtc,
        SourceFileIdentity = "VOL:1",
    };

    private string WriteTempFile(string name, string contents)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static FileEntry MakeFileEntry(long id, string path) => new()
    {
        Id = id,
        SessionId = 1,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = new FileInfo(path).Length,
        CreatedUtc = File.GetCreationTimeUtc(path),
        ModifiedUtc = File.GetLastWriteTimeUtc(path),
        AccessedUtc = File.GetLastAccessTimeUtc(path),
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Unknown,
        IsReparsePoint = false,
    };
}
