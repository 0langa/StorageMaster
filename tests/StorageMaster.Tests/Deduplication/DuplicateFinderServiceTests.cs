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
            new DuplicateCandidate(MakeFileEntry(1, fileA, FileTypeCategory.Unknown)),
            new DuplicateCandidate(MakeFileEntry(2, fileB, FileTypeCategory.Unknown)),
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
