using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
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

    // ── Why a selected file was not deleted ─────────────────────────────────

    [Fact]
    public async Task DeleteSelectedWithResultAsync_ReportsAReasonForEverySelectedFileLeftInPlace()
    {
        var keeperPath = WriteFile("reason-keeper.txt", "same");
        var changedPath = WriteFile("reason-changed.txt", "same");
        var differentPath = WriteFile("reason-different.txt", "diff");
        var lockedPath = WriteFile("reason-locked.txt", "same");
        var refusedPath = WriteFile("reason-refused.txt", "same");
        var deletedPath = WriteFile("reason-deleted.txt", "same");

        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var changed = MakeMember(2, 10, changedPath, isKeeper: false, isSelected: true);
        var different = MakeMember(3, 10, differentPath, isKeeper: false, isSelected: true);
        var locked = MakeMember(4, 10, lockedPath, isKeeper: false, isSelected: true);
        var refused = MakeMember(5, 10, refusedPath, isKeeper: false, isSelected: true);
        var deleted = MakeMember(6, 10, deletedPath, isKeeper: false, isSelected: true);
        var noIdentityPath = WriteFile("reason-noidentity.txt", "same");
        var noIdentity = MakeMember(7, 10, noIdentityPath, isKeeper: false, isSelected: true) with { Identity = null };
        await File.AppendAllTextAsync(changedPath, " changed");

        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeletionRequest request, CancellationToken _) =>
                request.Path == lockedPath
                    ? new DeletionOutcome(request.Path, false, 0, "The process cannot access the file.")
                    : request.Path == refusedPath
                        ? new DeletionOutcome(request.Path, false, 0, "Not deleted: no Recycle Bin.", RecycleBinRefused: true)
                        : new DeletionOutcome(request.Path, true, 4));
        var service = CreateService(deleter.Object, Mock.Of<ICleanupLogRepository>(), CreatePermissiveRepository().Object);

        var result = await service.DeleteSelectedWithResultAsync(
            MakeGroup(),
            [keeper, changed, different, locked, refused, deleted, noIdentity],
            DeletionMethod.RecycleBin);

        result.DeletedFileCount.Should().Be(1);
        result.Skipped.Should().BeEquivalentTo(new[]
        {
            new DuplicateDeletionSkip(changed.Id, changedPath, DuplicateDeletionSkipReason.ChangedSinceAnalysis),
            new DuplicateDeletionSkip(different.Id, differentPath, DuplicateDeletionSkipReason.NoLongerDuplicate),
            new DuplicateDeletionSkip(locked.Id, lockedPath, DuplicateDeletionSkipReason.DeleteFailed, "The process cannot access the file."),
            new DuplicateDeletionSkip(refused.Id, refusedPath, DuplicateDeletionSkipReason.RecycleBinUnavailable, "Not deleted: no Recycle Bin."),
            new DuplicateDeletionSkip(noIdentity.Id, noIdentity.FullPath, DuplicateDeletionSkipReason.NoStableIdentity),
        });
        result.Warnings.Should().BeEmpty(
            "a file left in place is not a bookkeeping warning; it is reported with its reason");
    }

    [Fact]
    public async Task DeleteSelectedWithResultAsync_MemberThatCannotBeReread_IsReportedAsNotVerified()
    {
        var keeperPath = WriteFile("verify-keeper.txt", "same");
        var memberPath = WriteFile("verify-member.txt", "same");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var member = MakeMember(2, 10, memberPath, isKeeper: false, isSelected: true);

        var strategy = new Mock<IDuplicateDetectionStrategy>();
        strategy.SetupGet(x => x.Method).Returns(DuplicateMethod.ExactSha256);
        strategy.SetupGet(x => x.Algorithm).Returns("SHA-256");
        strategy.SetupGet(x => x.IsAvailable).Returns(true);
        strategy.Setup(x => x.ComputeSignatureAsync(It.IsAny<DuplicateCandidate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateCandidate candidate, CancellationToken _) => candidate.File.FullPath == keeperPath
                ? Signature(candidate, "Ready", text: "AA")
                : Signature(candidate, "Error", error: "sharing violation"));

        var service = new DuplicateDeletionService(
            new Mock<IFileDeleter>(MockBehavior.Strict).Object,
            Mock.Of<ICleanupLogRepository>(),
            new Mock<IDuplicateRepository>(MockBehavior.Strict).Object,
            new FileSnapshotProvider(),
            [strategy.Object]);

        var result = await service.DeleteSelectedWithResultAsync(MakeGroup(), [keeper, member], DeletionMethod.Permanent);

        result.Skipped.Should().ContainSingle().Which.Should().Be(
            new DuplicateDeletionSkip(member.Id, memberPath, DuplicateDeletionSkipReason.CouldNotVerify, "sharing violation"));
        File.Exists(memberPath).Should().BeTrue();

        static DuplicateSignature Signature(DuplicateCandidate candidate, string status, string? text = null, string? error = null) =>
            new()
            {
                Id = 0,
                SessionId = 0,
                FileEntryId = candidate.File.Id,
                Method = DuplicateMethod.ExactSha256,
                Algorithm = "SHA-256",
                AlgorithmVersion = 1,
                ComputedUtc = DateTime.UtcNow,
                Status = status,
                SignatureText = text,
                ErrorMessage = error,
            };
    }

    [Fact]
    public void EveryNotDeletedReasonHasAStringInEveryLanguage()
    {
        foreach (var language in LocalizationCatalog.ShippedLanguages)
        {
            var strings = LocalizationCatalog.Strings(language);
            foreach (var reason in Enum.GetValues<DuplicateDeletionSkipReason>())
            {
                strings.Should().ContainKey(DuplicateDeletionSkips.ResourceKey(reason),
                    "the Duplicates page explains every file it did not delete, in {0} too", language);
            }
        }
    }

    [Fact]
    public void ForUnprocessedGroup_ListsOnlyTheSelectedNonKeepers()
    {
        var keeper = MakeMember(1, 10, WriteFile("group-keeper.txt", "x"), isKeeper: true, isSelected: true);
        var selected = MakeMember(2, 10, WriteFile("group-selected.txt", "x"), isKeeper: false, isSelected: true);
        var unselected = MakeMember(3, 10, WriteFile("group-unselected.txt", "x"), isKeeper: false, isSelected: false);

        var skips = DuplicateDeletionSkips.ForUnprocessedGroup([keeper, selected, unselected], "keeper changed");

        skips.Should().ContainSingle().Which.Should().Be(new DuplicateDeletionSkip(
            selected.Id, selected.FullPath, DuplicateDeletionSkipReason.KeeperNotVerified, "keeper changed"));
    }

    [Fact]
    public void SelectOnlyRecycleBinRefusals_SelectsNothingTheUserWasNotAskedAbout()
    {
        var keeper = MakeMember(1, 10, WriteFile("retry-keeper.txt", "x"), isKeeper: true, isSelected: false);
        var refused = MakeMember(2, 10, WriteFile("retry-refused.txt", "x"), isKeeper: false, isSelected: true);
        var locked = MakeMember(3, 10, WriteFile("retry-locked.txt", "x"), isKeeper: false, isSelected: true);
        var recycled = MakeMember(4, 10, WriteFile("retry-recycled.txt", "x"), isKeeper: false, isSelected: true);
        var unselected = MakeMember(5, 10, WriteFile("retry-unselected.txt", "x"), isKeeper: false, isSelected: false);

        var members = DuplicateDeletionSkips.SelectOnlyRecycleBinRefusals(
            [keeper, refused, locked, recycled, unselected],
            [
                new DuplicateDeletionSkip(refused.Id, refused.FullPath, DuplicateDeletionSkipReason.RecycleBinUnavailable),
                new DuplicateDeletionSkip(locked.Id, locked.FullPath, DuplicateDeletionSkipReason.DeleteFailed),
                // A refusal for a member the user had not selected must not select it.
                new DuplicateDeletionSkip(unselected.Id, unselected.FullPath, DuplicateDeletionSkipReason.RecycleBinUnavailable),
            ]);

        members.Should().HaveCount(5, "the keeper stays in the plan so the retry can verify against it");
        members.Where(static member => member.IsSelected).Select(static member => member.Id)
            .Should().Equal(refused.Id);
        members.Single(member => member.Id == keeper.Id).IsKeeper.Should().BeTrue();
    }

    [Fact]
    public async Task PermanentRetryOfARefusal_DeletesOnlyTheRefusedFileAndRechecksIt()
    {
        var keeperPath = WriteFile("again-keeper.txt", "same");
        var refusedPath = WriteFile("again-refused.txt", "same");
        var otherPath = WriteFile("again-other.txt", "same");
        var keeper = MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false);
        var refused = MakeMember(2, 10, refusedPath, isKeeper: false, isSelected: true);
        var other = MakeMember(3, 10, otherPath, isKeeper: false, isSelected: true);

        var requests = new List<DeletionRequest>();
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(x => x.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeletionRequest request, CancellationToken _) =>
            {
                requests.Add(request);
                return request.Method == DeletionMethod.RecycleBin && request.Path == refusedPath
                    ? new DeletionOutcome(request.Path, false, 0, "no bin", RecycleBinRefused: true)
                    : new DeletionOutcome(request.Path, true, 4);
            });
        var service = CreateService(deleter.Object, Mock.Of<ICleanupLogRepository>(), CreatePermissiveRepository().Object);

        var first = await service.DeleteSelectedWithResultAsync(MakeGroup(), [keeper, refused, other], DeletionMethod.RecycleBin);
        var retry = await service.DeleteSelectedWithResultAsync(
            MakeGroup(),
            DuplicateDeletionSkips.SelectOnlyRecycleBinRefusals([keeper, refused, other], first.Skipped),
            DeletionMethod.Permanent);

        first.Skipped.Should().ContainSingle().Which.Reason.Should().Be(DuplicateDeletionSkipReason.RecycleBinUnavailable);
        retry.DeletedFileCount.Should().Be(1);
        retry.Skipped.Should().BeEmpty();
        var permanent = requests.Where(static request => request.Method == DeletionMethod.Permanent).ToArray();
        permanent.Should().ContainSingle();
        permanent[0].Path.Should().Be(refusedPath);
        permanent[0].ExpectedSnapshot.Should().NotBeNull("the retry re-validates the file like any deletion");
    }

    private static Mock<IDuplicateRepository> CreatePermissiveRepository()
    {
        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(x => x.MarkMembersDeletedAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.Setup(x => x.RecordDuplicateOperationIntentAsync(It.IsAny<DuplicateOperationJournalEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DuplicateOperationJournalEntry entry, CancellationToken _) => entry with { Id = 1 });
        repo.Setup(x => x.UpdateDuplicateOperationOutcomeAsync(
                It.IsAny<long>(),
                It.IsAny<DuplicateOperationStatus>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return repo;
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
