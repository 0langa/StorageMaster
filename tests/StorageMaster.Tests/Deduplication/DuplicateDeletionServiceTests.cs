using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.Deduplication;

public sealed class DuplicateDeletionServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sm_dupdelete_{Guid.NewGuid():N}");

    public DuplicateDeletionServiceTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task DeleteSelectedAsync_SkipsChangedFilesAndUsesBatchDelete()
    {
        var keeperPath = WriteFile("keeper.txt", "keep");
        var deletePath = WriteFile("delete.txt", "keep");
        var changedPath = WriteFile("changed.txt", "old");

        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);
        var changed = MakeMember(3, 10, changedPath, isKeeper: false, isSelected: true);
        await File.AppendAllTextAsync(changedPath, " changed");

        DeletionRequest? capturedRequest = null;
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeletionRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new DeletionOutcome(deletePath, true, selected.SizeBytes));

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

        var service = CreateService(deleter.Object, cleanupLog.Object, repo.Object);

        var freed = await service.DeleteSelectedAsync(
            MakeGroup(),
            [keeper, selected, changed],
            DeletionMethod.RecycleBin);

        freed.Should().Be(selected.SizeBytes);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Path.Should().Be(deletePath);
        capturedRequest.ExpectedSnapshot.Should().NotBeNull();
        var expectedIds = new[] { selected.Id };
        repo.Verify(x => x.MarkMembersDeletedAsync(It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(expectedIds)), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.RecordDuplicateOperationIntentAsync(
            It.Is<DuplicateOperationJournalEntry>(entry =>
                entry.Kind == DuplicateOperationKind.Delete &&
                entry.Status == DuplicateOperationStatus.Planned &&
                entry.Method == DeletionMethod.RecycleBin &&
                entry.SourcePath == deletePath &&
                entry.SourceIdentity == $"{selected.Identity!.VolumeSerial}:{selected.Identity.FileIndex}" &&
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
            repo.Object,
            Mock.Of<IFileSnapshotProvider>(),
            []);

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
        var deletePath = WriteFile("delete-fail.txt", "keep");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);

        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeletionOutcome(deletePath, false, 0, "access denied"));

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

        var service = CreateService(
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

    [Fact]
    public async Task DeleteSelectedWithResultAsync_PostMoveAtomicFailure_TerminalizesJournalAndContinuesWithWarnings()
    {
        var keeperPath = WriteFile("keeper-post-move.txt", "same");
        var deletePath = WriteFile("delete-post-move.txt", "same");
        var quarantinePath = Path.Combine(_tempDir, "quarantined-post-move.txt");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);

        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeletionRequest, CancellationToken>((request, _) =>
                File.Move(request.Path, quarantinePath))
            .ReturnsAsync(new DeletionOutcome(
                deletePath,
                true,
                selected.SizeBytes,
                QuarantinePath: quarantinePath));

        var cleanupLog = new Mock<ICleanupLogRepository>();
        cleanupLog.Setup(x => x.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit database unavailable"));

        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(x => x.RecordDuplicateOperationIntentAsync(
                It.IsAny<DuplicateOperationJournalEntry>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateOperationJournalEntry entry, CancellationToken _) => entry with { Id = 901 });
        repo.Setup(x => x.CompleteQuarantineMoveAsync(
                901,
                selected.Id,
                77,
                deletePath,
                quarantinePath,
                selected.SizeBytes,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("quarantine transaction failed"));
        repo.Setup(x => x.UpdateDuplicateOperationOutcomeAsync(
                901,
                DuplicateOperationStatus.Quarantined,
                quarantinePath,
                selected.SizeBytes,
                "quarantine transaction failed",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.Setup(x => x.MarkMembersDeletedAsync(
                It.IsAny<IReadOnlyList<long>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(deleter.Object, cleanupLog.Object, repo.Object);

        var result = await service.DeleteSelectedWithResultAsync(
            MakeGroup(),
            [keeper, selected],
            DeletionMethod.Quarantine);

        result.ProcessedBytes.Should().Be(selected.SizeBytes);
        result.DeletedFileCount.Should().Be(1);
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(warning =>
            warning.Path == deletePath && warning.Message.Contains("terminal recovery journal"));
        result.Warnings.Should().Contain(warning => warning.Message.Contains("cleanup audit"));
        File.Exists(deletePath).Should().BeFalse();
        File.Exists(quarantinePath).Should().BeTrue();
        repo.Verify(x => x.UpdateDuplicateOperationOutcomeAsync(
            901,
            DuplicateOperationStatus.Quarantined,
            quarantinePath,
            selected.SizeBytes,
            "quarantine transaction failed",
            It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.RecordQuarantineAsync(
            It.IsAny<long?>(),
            It.IsAny<long>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteSelectedWithResultAsync_PostMutationJournalFailure_CountsProcessedFileAndContinuesBookkeeping()
    {
        var keeperPath = WriteFile("keeper-journal-failure.txt", "same");
        var deletePath = WriteFile("delete-journal-failure.txt", "same");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);

        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeletionRequest, CancellationToken>((request, _) => File.Delete(request.Path))
            .ReturnsAsync(new DeletionOutcome(deletePath, true, BytesFreed: 0));

        var cleanupLog = new Mock<ICleanupLogRepository>();
        cleanupLog.Setup(x => x.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(x => x.RecordDuplicateOperationIntentAsync(
                It.IsAny<DuplicateOperationJournalEntry>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateOperationJournalEntry entry, CancellationToken _) => entry with { Id = 903 });
        repo.Setup(x => x.UpdateDuplicateOperationOutcomeAsync(
                903,
                DuplicateOperationStatus.Completed,
                null,
                0,
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("journal database unavailable"));
        repo.Setup(x => x.MarkMembersDeletedAsync(
                It.IsAny<IReadOnlyList<long>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(deleter.Object, cleanupLog.Object, repo.Object);

        var result = await service.DeleteSelectedWithResultAsync(
            MakeGroup(),
            [keeper, selected],
            DeletionMethod.RecycleBin);

        result.ProcessedBytes.Should().Be(selected.SizeBytes,
            "processed bytes describe the validated file even when a move reports no physical space reclaimed");
        result.DeletedFileCount.Should().Be(1);
        result.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("terminal recovery journal");
        File.Exists(deletePath).Should().BeFalse();
        repo.Verify(x => x.MarkMembersDeletedAsync(
            It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { selected.Id })),
            It.IsAny<CancellationToken>()), Times.Once);
        cleanupLog.Verify(x => x.LogResultAsync(
            It.Is<CleanupResult>(audit => audit.BytesFreed == 0),
            It.IsAny<CleanupSuggestion>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RestoreFromQuarantineAsync_PostMoveCatalogFailure_LeavesTerminalJournalWithDestination()
    {
        var restoredPath = Path.Combine(_tempDir, "restore-terminal.txt");
        var quarantinePath = WriteFile("restore-terminal.quarantine", "payload");
        var record = new QuarantinedFile
        {
            Id = 72,
            MemberId = 8,
            RunId = 99,
            OriginalPath = restoredPath,
            QuarantinePath = quarantinePath,
            QuarantinedUtc = DateTime.UtcNow,
        };

        var repo = new Mock<IDuplicateRepository>(MockBehavior.Strict);
        repo.Setup(x => x.GetQuarantinedFileAsync(72, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        repo.Setup(x => x.RecordDuplicateOperationIntentAsync(
                It.IsAny<DuplicateOperationJournalEntry>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateOperationJournalEntry entry, CancellationToken _) => entry with { Id = 902 });
        var sequence = new MockSequence();
        repo.InSequence(sequence).Setup(x => x.UpdateDuplicateOperationOutcomeAsync(
                902,
                DuplicateOperationStatus.Restored,
                restoredPath,
                0,
                null,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.InSequence(sequence).Setup(x => x.MarkRestoredAsync(
                72,
                restoredPath,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog update failed"));

        var service = new DuplicateDeletionService(
            Mock.Of<IFileDeleter>(),
            Mock.Of<ICleanupLogRepository>(),
            repo.Object,
            Mock.Of<IFileSnapshotProvider>(),
            []);

        var act = () => service.RestoreFromQuarantineAsync(72);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal recovery journal was saved*");
        File.Exists(restoredPath).Should().BeTrue();
        File.Exists(quarantinePath).Should().BeFalse();
        repo.VerifyAll();
    }

    [Fact]
    public async Task DeleteSelectedAsync_KeeperReplacedWithSameContentSizeAndTimestamp_FailsClosed()
    {
        var keeperPath = WriteFile("keeper-replaced.txt", "same");
        var deletePath = WriteFile("delete-preserved.txt", "same");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);

        ReplaceFilePreservingContentAndTimestamp(keeperPath, "same", keeper.ModifiedUtc);
        var replacement = TakeSnapshot(keeperPath);
        replacement.Identity.Should().NotBe(keeper.Identity);
        replacement.SizeBytes.Should().Be(keeper.SizeBytes);
        replacement.LastWriteUtc.Should().Be(keeper.ModifiedUtc);
        replacement.Attributes.Should().Be(keeper.Attributes);

        var deleter = new Mock<IFileDeleter>(MockBehavior.Strict);
        var repo = new Mock<IDuplicateRepository>(MockBehavior.Strict);
        var service = CreateService(
            deleter.Object,
            Mock.Of<ICleanupLogRepository>(),
            repo.Object);

        var act = () => service.DeleteSelectedAsync(
            MakeGroup(),
            [keeper, selected],
            DeletionMethod.Permanent);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*keeper changed*");
        File.Exists(deletePath).Should().BeTrue("last known original copy must remain");
        deleter.VerifyNoOtherCalls();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteSelectedAsync_SelectedMemberReplacedWithSameContentSizeAndTimestamp_SkipsDeletion()
    {
        var keeperPath = WriteFile("keeper-selected-replaced.txt", "same");
        var deletePath = WriteFile("selected-replaced.txt", "same");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);

        ReplaceFilePreservingContentAndTimestamp(deletePath, "same", selected.ModifiedUtc);
        var replacement = TakeSnapshot(deletePath);
        replacement.Identity.Should().NotBe(selected.Identity);
        replacement.SizeBytes.Should().Be(selected.SizeBytes);
        replacement.LastWriteUtc.Should().Be(selected.ModifiedUtc);
        replacement.Attributes.Should().Be(selected.Attributes);

        var deleter = new Mock<IFileDeleter>(MockBehavior.Strict);
        var repo = new Mock<IDuplicateRepository>(MockBehavior.Strict);
        var service = CreateService(
            deleter.Object,
            Mock.Of<ICleanupLogRepository>(),
            repo.Object);

        var processed = await service.DeleteSelectedAsync(
            MakeGroup(),
            [keeper, selected],
            DeletionMethod.Permanent);

        processed.Should().Be(0);
        File.Exists(deletePath).Should().BeTrue();
        deleter.VerifyNoOtherCalls();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteSelectedAsync_SelectedMemberAttributesChanged_SkipsDeletion()
    {
        var keeperPath = WriteFile("keeper-attributes.txt", "same");
        var deletePath = WriteFile("selected-attributes.txt", "same");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var selected = MakeMember(2, 10, deletePath, isKeeper: false, isSelected: true);
        File.SetAttributes(deletePath, selected.Attributes | FileAttributes.Hidden);

        var deleter = new Mock<IFileDeleter>(MockBehavior.Strict);
        var repo = new Mock<IDuplicateRepository>(MockBehavior.Strict);
        var service = CreateService(
            deleter.Object,
            Mock.Of<ICleanupLogRepository>(),
            repo.Object);

        var processed = await service.DeleteSelectedAsync(
            MakeGroup(),
            [keeper, selected],
            DeletionMethod.Permanent);

        processed.Should().Be(0);
        File.Exists(deletePath).Should().BeTrue();
        deleter.VerifyNoOtherCalls();
        repo.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static DuplicateDeletionService CreateService(
        IFileDeleter deleter,
        ICleanupLogRepository cleanupLog,
        IDuplicateRepository repository)
    {
        var snapshots = new FileSnapshotProvider();
        var strategy = new ExactSha256Strategy(new FileContentHasher(), snapshots);
        return new DuplicateDeletionService(
            deleter,
            cleanupLog,
            repository,
            snapshots,
            [strategy]);
    }

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void ReplaceFilePreservingContentAndTimestamp(
        string path,
        string contents,
        DateTime modifiedUtc)
    {
        var replacement = path + $".{Guid.NewGuid():N}.replacement";
        File.WriteAllText(replacement, contents);
        File.SetLastWriteTimeUtc(replacement, modifiedUtc);
        File.Delete(path);
        File.Move(replacement, path);
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

    private static DuplicateGroupMember MakeMember(
        long id,
        long groupId,
        string path,
        bool isKeeper,
        bool isSelected)
    {
        var snapshot = TakeSnapshot(path);
        return new DuplicateGroupMember
        {
            Id = id,
            GroupId = groupId,
            FileEntryId = id + 100,
            FullPath = path,
            FileName = Path.GetFileName(path),
            SizeBytes = snapshot.SizeBytes,
            ModifiedUtc = snapshot.LastWriteUtc,
            Attributes = snapshot.Attributes,
            Identity = snapshot.Identity,
            Score = 1.0,
            IsKeeper = isKeeper,
            IsSelected = isSelected,
            RecommendationReason = isKeeper ? "Keeper" : "Duplicate",
            ExistsNow = true,
        };
    }

    private static FileSnapshot TakeSnapshot(string path) =>
        new FileSnapshotProvider()
            .TakeSnapshotAsync(path)
            .GetAwaiter()
            .GetResult()
        ?? throw new InvalidOperationException($"Could not snapshot test file: {path}");
}
