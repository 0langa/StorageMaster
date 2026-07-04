using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Deduplication;

public sealed class DuplicateDeletionServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sm_dupdelete_{Guid.NewGuid():N}");

    public DuplicateDeletionServiceTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task DeleteSelectedAsync_SkipsChangedFilesAndUsesBatchDelete()
    {
        var keeperPath = WriteFile("keeper.txt", "keep");
        var deletePath = WriteFile("delete.txt", "delete");
        var changedPath = WriteFile("changed.txt", "old");

        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);
        var changed = MakeMember(3, 10, changedPath, isKeeper: false, isSelected: true);
        await File.AppendAllTextAsync(changedPath, " changed");

        IReadOnlyList<DeletionRequest>? capturedRequests = null;
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteManyAsync(It.IsAny<IReadOnlyList<DeletionRequest>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<DeletionRequest>, CancellationToken>((requests, _) => capturedRequests = requests)
            .Returns(DeleteMany([new DeletionOutcome(deletePath, true, selected.SizeBytes)]));

        var cleanupLog = new Mock<ICleanupLogRepository>();
        cleanupLog.Setup(x => x.LogResultAsync(It.IsAny<CleanupResult>(), It.IsAny<CleanupSuggestion>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(x => x.MarkMembersDeletedAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.Setup(x => x.RecordDuplicateOperationIntentAsync(It.IsAny<DuplicateOperationJournalEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateOperationJournalEntry entry, CancellationToken _) => entry with { Id = 123 });
        repo.Setup(x => x.UpdateDuplicateOperationOutcomeAsync(
                It.IsAny<long>(),
                It.IsAny<DuplicateOperationStatus>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new DuplicateDeletionService(deleter.Object, cleanupLog.Object, repo.Object);

        var freed = await service.DeleteSelectedAsync(
            MakeGroup(),
            [keeper, selected, changed],
            DeletionMethod.RecycleBin);

        freed.Should().Be(selected.SizeBytes);
        capturedRequests.Should().NotBeNull();
        capturedRequests!.Should().ContainSingle();
        capturedRequests[0].Path.Should().Be(deletePath);
        var expectedIds = new[] { selected.Id };
        repo.Verify(x => x.MarkMembersDeletedAsync(It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(expectedIds)), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.RecordDuplicateOperationIntentAsync(
            It.Is<DuplicateOperationJournalEntry>(entry =>
                entry.Kind == DuplicateOperationKind.Delete &&
                entry.Status == DuplicateOperationStatus.Planned &&
                entry.Method == DeletionMethod.RecycleBin &&
                entry.SourcePath == deletePath &&
                entry.MemberId == selected.Id),
            It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.UpdateDuplicateOperationOutcomeAsync(
            123,
            DuplicateOperationStatus.Completed,
            null,
            selected.SizeBytes,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RestoreFromQuarantineAsync_MovesFileBackAndMarksRestored()
    {
        var originalPath = Path.Combine(_tempDir, "restored.txt");
        var quarantinePath = WriteFile("quarantine.txt", "payload");

        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(x => x.GetQuarantinedFileAsync(55, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuarantinedFile
            {
                Id = 55,
                MemberId = 8,
                RunId = 99,
                OriginalPath = originalPath,
                QuarantinePath = quarantinePath,
                QuarantinedUtc = DateTime.UtcNow,
            });
        repo.Setup(x => x.MarkRestoredAsync(55, originalPath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.Setup(x => x.RecordDuplicateOperationIntentAsync(It.IsAny<DuplicateOperationJournalEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateOperationJournalEntry entry, CancellationToken _) => entry with { Id = 456 });
        repo.Setup(x => x.UpdateDuplicateOperationOutcomeAsync(
                It.IsAny<long>(),
                It.IsAny<DuplicateOperationStatus>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new DuplicateDeletionService(
            Mock.Of<IFileDeleter>(),
            Mock.Of<ICleanupLogRepository>(),
            repo.Object);

        await service.RestoreFromQuarantineAsync(55);

        File.Exists(originalPath).Should().BeTrue();
        File.Exists(quarantinePath).Should().BeFalse();
        repo.Verify(x => x.MarkRestoredAsync(55, originalPath, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.RecordDuplicateOperationIntentAsync(
            It.Is<DuplicateOperationJournalEntry>(entry =>
                entry.Kind == DuplicateOperationKind.Restore &&
                entry.Status == DuplicateOperationStatus.Planned &&
                entry.SourcePath == quarantinePath &&
                entry.DestinationPath == originalPath &&
                entry.QuarantineId == 55),
            It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.UpdateDuplicateOperationOutcomeAsync(
            456,
            DuplicateOperationStatus.Restored,
            originalPath,
            0,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteSelectedAsync_FailedFilesystemOutcome_RecordsFailedJournalAndKeepsMember()
    {
        var keeperPath = WriteFile("keeper-fail.txt", "keep");
        var deletePath = WriteFile("delete-fail.txt", "delete");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);

        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteManyAsync(It.IsAny<IReadOnlyList<DeletionRequest>>(), It.IsAny<CancellationToken>()))
            .Returns(DeleteMany([new DeletionOutcome(deletePath, false, 0, "access denied")]));

        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(x => x.RecordDuplicateOperationIntentAsync(It.IsAny<DuplicateOperationJournalEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateOperationJournalEntry entry, CancellationToken _) => entry with { Id = 789 });
        repo.Setup(x => x.UpdateDuplicateOperationOutcomeAsync(
                It.IsAny<long>(),
                It.IsAny<DuplicateOperationStatus>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new DuplicateDeletionService(
            deleter.Object,
            Mock.Of<ICleanupLogRepository>(),
            repo.Object);

        var freed = await service.DeleteSelectedAsync(
            MakeGroup(),
            [keeper, selected],
            DeletionMethod.Quarantine);

        freed.Should().Be(0);
        repo.Verify(x => x.MarkMembersDeletedAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(x => x.UpdateDuplicateOperationOutcomeAsync(
            789,
            DuplicateOperationStatus.Failed,
            null,
            0,
            "access denied",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static async IAsyncEnumerable<DeletionOutcome> DeleteMany(IEnumerable<DeletionOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            yield return outcome;
            await Task.Yield();
        }
    }

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static DuplicateGroup MakeGroup() => new()
    {
        Id = 10,
        RunId = 77,
        Method = DuplicateMethod.ExactSha256,
        Algorithm = "SHA-256",
        Confidence = 1.0,
        TotalBytes = 0,
        ReclaimableBytes = 0,
        RepresentativeFileEntryId = 1,
    };

    private static DuplicateGroupMember MakeMember(long id, long groupId, string path, bool isKeeper, bool isSelected) => new()
    {
        Id = id,
        GroupId = groupId,
        FileEntryId = id + 100,
        FullPath = path,
        FileName = Path.GetFileName(path),
        SizeBytes = new FileInfo(path).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(path),
        Score = 1.0,
        IsKeeper = isKeeper,
        IsSelected = isSelected,
        RecommendationReason = isKeeper ? "Keeper" : "Duplicate",
        ExistsNow = true,
    };
}
