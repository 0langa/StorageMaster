using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.Deduplication;

public sealed class DuplicateFinderServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sm_dupfinder_{Guid.NewGuid():N}");

    public DuplicateFinderServiceTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task RunAsync_UnavailableMethod_FailsFastWithClearMessage()
    {
        var strategy = new Mock<IDuplicateDetectionStrategy>();
        strategy.SetupGet(x => x.Method).Returns(DuplicateMethod.VideoPHash);
        strategy.SetupGet(x => x.DisplayName).Returns("Video perceptual hash");
        strategy.SetupGet(x => x.IsAvailable).Returns(false);
        strategy.SetupGet(x => x.UnavailableReason).Returns("FFmpeg/ffprobe not configured or not found.");

        var service = new DuplicateFinderService(
            Mock.Of<IDuplicateRepository>(),
            Mock.Of<IDuplicateCandidateProvider>(),
            new FileContentHasher(),
            [strategy.Object],
            new DuplicateKeeperPolicy(),
            NullLogger<DuplicateFinderService>.Instance);

        var act = () => service.RunAsync(new DuplicateScanOptions
        {
            SessionId = 42,
            Methods = [DuplicateMethod.VideoPHash],
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FFmpeg*");
    }

    [Fact]
    public async Task RunAsync_ExactDuplicates_CreateGroupAndAutoSelectNonKeeper()
    {
        var fileA = WriteTempFile("a.bin", "same-content", DateTime.UtcNow.AddHours(-2));
        var fileB = WriteTempFile("b.bin", "same-content", DateTime.UtcNow.AddHours(-1));
        var candidates = new[]
        {
            new DuplicateCandidate(
                MakeFileEntry(1, fileA, FileTypeCategory.Unknown),
                new FileIdentity("VOL", 101)),
            new DuplicateCandidate(
                MakeFileEntry(2, fileB, FileTypeCategory.Unknown),
                new FileIdentity("VOL", 102)),
        };

        var provider = new Mock<IDuplicateCandidateProvider>();
        provider.Setup(x => x.GetCandidatesAsync(It.IsAny<DuplicateCandidateQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates);

        var repo = BuildRepositoryMock(42, out var savedGroups, out var savedMembers);

        var service = new DuplicateFinderService(
            repo.Object,
            provider.Object,
            new FileContentHasher(),
            [new ExactSha256Strategy(new FileContentHasher(), new FileSnapshotProvider())],
            new DuplicateKeeperPolicy(),
            NullLogger<DuplicateFinderService>.Instance);

        var run = await service.RunAsync(new DuplicateScanOptions
        {
            SessionId = 42,
            Methods = [DuplicateMethod.ExactSha256],
            MinimumSizeBytes = 0,
        });

        run.Status.Should().Be(DuplicateRunStatus.Completed);
        savedGroups.Should().ContainSingle();
        savedMembers.Should().HaveCount(2);
        savedMembers.Select(static member => member.Identity).Should().BeEquivalentTo([
            new FileIdentity("VOL", 101),
            new FileIdentity("VOL", 102),
        ]);
        savedMembers.Should().OnlyContain(member =>
            member.Attributes == candidates.Single(candidate => candidate.File.Id == member.FileEntryId).File.Attributes);
        savedMembers.Count(static m => m.IsKeeper).Should().Be(1);
        savedMembers.Count(static m => m.IsSelected).Should().Be(1, "exact duplicates should auto-select non-keepers");
    }

    [Fact]
    public async Task RunAsync_NormalizedText_GroupsDifferentRawSizes()
    {
        var fileA = WriteTempFile("a.txt", "hello  \r\nworld\r\n", DateTime.UtcNow.AddHours(-2));
        var fileB = WriteTempFile("b.txt", "hello\nworld\n", DateTime.UtcNow.AddHours(-1));
        var candidates = new[]
        {
            new DuplicateCandidate(MakeFileEntry(11, fileA, FileTypeCategory.Document)),
            new DuplicateCandidate(MakeFileEntry(12, fileB, FileTypeCategory.Document)),
        };

        DuplicateCandidateQuery? capturedQuery = null;
        var provider = new Mock<IDuplicateCandidateProvider>();
        provider.Setup(x => x.GetCandidatesAsync(It.IsAny<DuplicateCandidateQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DuplicateCandidateQuery, CancellationToken>((query, _) => capturedQuery = query)
            .ReturnsAsync(candidates);

        var repo = BuildRepositoryMock(77, out var savedGroups, out var savedMembers);

        var service = new DuplicateFinderService(
            repo.Object,
            provider.Object,
            new FileContentHasher(),
            [new NormalizedTextStrategy(new FileSnapshotProvider())],
            new DuplicateKeeperPolicy(),
            NullLogger<DuplicateFinderService>.Instance);

        var run = await service.RunAsync(new DuplicateScanOptions
        {
            SessionId = 77,
            Methods = [DuplicateMethod.NormalizedText],
            MinimumSizeBytes = 0,
        });

        run.Status.Should().Be(DuplicateRunStatus.Completed);
        capturedQuery.Should().NotBeNull();
        capturedQuery!.RequireSameSizeBucket.Should().BeFalse();
        savedGroups.Should().ContainSingle();
        savedMembers.Count(static m => m.IsSelected).Should().Be(0, "normalized-text matches stay review-only");
    }

    [Fact]
    public async Task RunAsync_CachedSignature_SameSizeAndMtimeReplacementIdentity_Recomputes()
    {
        const long sessionId = 81;
        var path = WriteTempFile("replaced.bin", "same-size", DateTime.UtcNow.AddHours(-1));
        var candidate = new DuplicateCandidate(MakeFileEntry(81, path, FileTypeCategory.Unknown));
        var cached = MakeSignature(candidate.File, "cached-old-content", "VOL:100");
        var fresh = MakeSignature(candidate.File, "fresh-new-content", "VOL:200");
        var strategy = BuildCacheTestStrategy(fresh);
        var candidates = BuildCandidateProvider(candidate);
        var snapshots = new Mock<IFileSnapshotProvider>();
        snapshots.Setup(provider => provider.TakeSnapshotAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSnapshot(
                path,
                new FileIdentity("VOL", 200),
                candidate.File.SizeBytes,
                candidate.File.ModifiedUtc,
                candidate.File.Attributes));
        var repo = BuildRepositoryMock(sessionId, out _, out _);
        repo.Setup(repository => repository.GetCachedSignaturesAsync(
                sessionId, DuplicateMethod.NormalizedText, "CacheTest", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([cached]);

        var service = CreateCacheTestService(repo, candidates, strategy, snapshots.Object);

        await service.RunAsync(CacheTestOptions(sessionId));

        strategy.Verify(value => value.ComputeSignatureAsync(
            candidate, It.IsAny<CancellationToken>()), Times.Once,
            "a replacement file can retain the scan row's size and timestamp but has a new file identity");
        repo.Verify(repository => repository.SaveResultsAsync(
            It.IsAny<long>(),
            It.Is<IReadOnlyList<DuplicateSignature>>(signatures =>
                signatures.Count == 1 && signatures[0].SignatureText == "fresh-new-content"),
            It.IsAny<IReadOnlyList<DuplicateGroup>>(),
            It.IsAny<IReadOnlyList<DuplicateGroupMember>>(),
            It.IsAny<IReadOnlyList<DuplicateError>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_CachedSignature_MissingLiveFile_Recomputes()
    {
        const long sessionId = 82;
        var path = WriteTempFile("missing.bin", "content", DateTime.UtcNow.AddHours(-1));
        var candidate = new DuplicateCandidate(MakeFileEntry(82, path, FileTypeCategory.Unknown));
        var cached = MakeSignature(candidate.File, "cached", "VOL:300");
        var fresh = MakeSignature(candidate.File, null, null) with
        {
            Status = "Error",
            ErrorMessage = "File no longer exists before hashing.",
        };
        var strategy = BuildCacheTestStrategy(fresh);
        var candidates = BuildCandidateProvider(candidate);
        var snapshots = new Mock<IFileSnapshotProvider>();
        snapshots.Setup(provider => provider.TakeSnapshotAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileSnapshot?)null);
        var repo = BuildRepositoryMock(sessionId, out _, out _);
        repo.Setup(repository => repository.GetCachedSignaturesAsync(
                sessionId, DuplicateMethod.NormalizedText, "CacheTest", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([cached]);
        File.Delete(path);

        var service = CreateCacheTestService(repo, candidates, strategy, snapshots.Object);

        await service.RunAsync(CacheTestOptions(sessionId));

        strategy.Verify(value => value.ComputeSignatureAsync(
            candidate, It.IsAny<CancellationToken>()), Times.Once,
            "a missing live path must never inherit a cached signature from its old scan row");
    }

    [Fact]
    public async Task RunAsync_CachedSignature_LiveAttributesChanged_Recomputes()
    {
        const long sessionId = 83;
        var path = WriteTempFile("attributes.bin", "content", DateTime.UtcNow.AddHours(-1));
        var candidate = new DuplicateCandidate(MakeFileEntry(83, path, FileTypeCategory.Unknown));
        var cached = MakeSignature(candidate.File, "cached", "VOL:400");
        var fresh = MakeSignature(candidate.File, "fresh", "VOL:400");
        var strategy = BuildCacheTestStrategy(fresh);
        var candidates = BuildCandidateProvider(candidate);
        var snapshots = new Mock<IFileSnapshotProvider>();
        snapshots.Setup(provider => provider.TakeSnapshotAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSnapshot(
                path,
                new FileIdentity("VOL", 400),
                candidate.File.SizeBytes,
                candidate.File.ModifiedUtc,
                FileAttributes.ReadOnly));
        var repo = BuildRepositoryMock(sessionId, out _, out _);
        repo.Setup(repository => repository.GetCachedSignaturesAsync(
                sessionId, DuplicateMethod.NormalizedText, "CacheTest", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([cached]);

        var service = CreateCacheTestService(repo, candidates, strategy, snapshots.Object);

        await service.RunAsync(CacheTestOptions(sessionId));

        strategy.Verify(value => value.ComputeSignatureAsync(
            candidate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_CachedSignature_LiveSnapshotExactlyMatches_ReusesCache()
    {
        const long sessionId = 84;
        var path = WriteTempFile("unchanged.bin", "content", DateTime.UtcNow.AddHours(-1));
        var candidate = new DuplicateCandidate(MakeFileEntry(84, path, FileTypeCategory.Unknown));
        var cached = MakeSignature(candidate.File, "cached", "VOL:500");
        var fresh = MakeSignature(candidate.File, "should-not-compute", "VOL:500");
        var strategy = BuildCacheTestStrategy(fresh);
        var candidates = BuildCandidateProvider(candidate);
        var snapshots = new Mock<IFileSnapshotProvider>();
        snapshots.Setup(provider => provider.TakeSnapshotAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSnapshot(
                path,
                new FileIdentity("VOL", 500),
                candidate.File.SizeBytes,
                candidate.File.ModifiedUtc,
                candidate.File.Attributes));
        var repo = BuildRepositoryMock(sessionId, out _, out _);
        repo.Setup(repository => repository.GetCachedSignaturesAsync(
                sessionId, DuplicateMethod.NormalizedText, "CacheTest", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([cached]);

        var service = CreateCacheTestService(repo, candidates, strategy, snapshots.Object);

        await service.RunAsync(CacheTestOptions(sessionId));

        strategy.Verify(value => value.ComputeSignatureAsync(
            It.IsAny<DuplicateCandidate>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(repository => repository.SaveResultsAsync(
            It.IsAny<long>(),
            It.Is<IReadOnlyList<DuplicateSignature>>(signatures =>
                signatures.Count == 1 && signatures[0].SignatureText == "cached"),
            It.IsAny<IReadOnlyList<DuplicateGroup>>(),
            It.IsAny<IReadOnlyList<DuplicateGroupMember>>(),
            It.IsAny<IReadOnlyList<DuplicateError>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private Mock<IDuplicateRepository> BuildRepositoryMock(
        long sessionId,
        out List<DuplicateGroup> savedGroups,
        out List<DuplicateGroupMember> savedMembers)
    {
        var savedGroupsHolder = new List<DuplicateGroup>();
        var savedMembersHolder = new List<DuplicateGroupMember>();
        savedGroups = savedGroupsHolder;
        savedMembers = savedMembersHolder;

        var run = new DuplicateRun
        {
            Id = 1000 + sessionId,
            SessionId = sessionId,
            StartedUtc = DateTime.UtcNow,
            Status = DuplicateRunStatus.Running,
            ConfigJson = "{}",
        };

        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(x => x.CreateRunAsync(It.IsAny<DuplicateScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        repo.Setup(x => x.GetCachedSignaturesAsync(sessionId, It.IsAny<DuplicateMethod>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repo.Setup(x => x.SaveResultsAsync(run.Id, It.IsAny<IReadOnlyList<DuplicateSignature>>(), It.IsAny<IReadOnlyList<DuplicateGroup>>(), It.IsAny<IReadOnlyList<DuplicateGroupMember>>(), It.IsAny<IReadOnlyList<DuplicateError>>(), It.IsAny<CancellationToken>()))
            .Callback<long, IReadOnlyList<DuplicateSignature>, IReadOnlyList<DuplicateGroup>, IReadOnlyList<DuplicateGroupMember>, IReadOnlyList<DuplicateError>, CancellationToken>((_, _, groups, members, _, _) =>
            {
                savedGroupsHolder.Clear();
                savedGroupsHolder.AddRange(groups);
                savedMembersHolder.Clear();
                savedMembersHolder.AddRange(members);
            })
            .Returns(Task.CompletedTask);
        repo.Setup(x => x.CompleteRunAsync(run.Id, It.IsAny<DuplicateRunStatus>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.Setup(x => x.GetRunsForSessionAsync(sessionId, It.IsAny<CancellationToken>()))
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
                    GroupCount = savedGroupsHolder.Count,
                    ReclaimableBytes = savedGroupsHolder.Sum(static g => g.ReclaimableBytes),
                    ErrorCount = 0,
                },
            ]);
        return repo;
    }

    private static DuplicateFinderService CreateCacheTestService(
        Mock<IDuplicateRepository> repository,
        Mock<IDuplicateCandidateProvider> candidates,
        Mock<IDuplicateDetectionStrategy> strategy,
        IFileSnapshotProvider snapshots) =>
        new(
            repository.Object,
            candidates.Object,
            Mock.Of<IFileContentHasher>(),
            [strategy.Object],
            new DuplicateKeeperPolicy(),
            NullLogger<DuplicateFinderService>.Instance,
            snapshots);

    private static Mock<IDuplicateCandidateProvider> BuildCandidateProvider(DuplicateCandidate candidate)
    {
        var provider = new Mock<IDuplicateCandidateProvider>();
        provider.Setup(value => value.GetCandidatesAsync(
                It.IsAny<DuplicateCandidateQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        return provider;
    }

    private static Mock<IDuplicateDetectionStrategy> BuildCacheTestStrategy(DuplicateSignature computed)
    {
        var strategy = new Mock<IDuplicateDetectionStrategy>();
        strategy.SetupGet(value => value.Method).Returns(DuplicateMethod.NormalizedText);
        strategy.SetupGet(value => value.Algorithm).Returns("CacheTest");
        strategy.SetupGet(value => value.AlgorithmVersion).Returns(1);
        strategy.SetupGet(value => value.DisplayName).Returns("Cache test");
        strategy.SetupGet(value => value.IsAvailable).Returns(true);
        strategy.SetupGet(value => value.UsePartialHashPreFilter).Returns(false);
        strategy.Setup(value => value.BuildCandidateQuery(It.IsAny<DuplicateScanOptions>()))
            .Returns<DuplicateScanOptions>(options => new DuplicateCandidateQuery
            {
                SessionId = options.SessionId,
                MinimumSizeBytes = 0,
                RequireSameSizeBucket = false,
            });
        strategy.Setup(value => value.ComputeSignatureAsync(
                It.IsAny<DuplicateCandidate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(computed);
        strategy
            .Setup(value => value.BuildMatches(
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<DuplicateCandidate>>>()))
            .Returns(Array.Empty<DuplicateStrategyMatch>());
        return strategy;
    }

    private static DuplicateScanOptions CacheTestOptions(long sessionId) => new()
    {
        SessionId = sessionId,
        Methods = [DuplicateMethod.NormalizedText],
        MinimumSizeBytes = 0,
        MaxConcurrency = 1,
        PerDriveConcurrency = 1,
    };

    private static DuplicateSignature MakeSignature(
        FileEntry file,
        string? signatureText,
        string? sourceIdentity) => new()
        {
            Id = 1,
            SessionId = file.SessionId,
            FileEntryId = file.Id,
            Method = DuplicateMethod.NormalizedText,
            Algorithm = "CacheTest",
            AlgorithmVersion = 1,
            SignatureText = signatureText,
            ComputedUtc = DateTime.UtcNow,
            Status = "Ready",
            SourceSizeBytes = file.SizeBytes,
            SourceModifiedUtc = file.ModifiedUtc,
            SourceFileIdentity = sourceIdentity,
        };

    private string WriteTempFile(string name, string contents, DateTime modifiedUtc)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }

    private static FileEntry MakeFileEntry(long id, string path, FileTypeCategory category) => new()
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
        Category = category,
        IsReparsePoint = false,
    };
}
