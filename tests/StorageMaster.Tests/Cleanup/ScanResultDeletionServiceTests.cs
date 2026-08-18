using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.Cleanup;

public sealed class ScanResultDeletionServiceTests
{
    [Fact]
    public async Task DeleteAsync_Success_RemovesEntryMarksSessionAndLogsAudit()
    {
        var deleter = new Mock<IFileDeleter>();
        var cleanupLog = new Mock<ICleanupLogRepository>();
        var scanRepo = new Mock<IScanRepository>();
        var file = MakeFileEntry();

        deleter.Setup(d => d.DeleteAsync(
                It.IsAny<DeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeletionOutcome(file.FullPath, true, file.SizeBytes));

        CleanupSuggestion? loggedSuggestion = null;
        cleanupLog.Setup(l => l.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                It.IsAny<CancellationToken>()))
            .Callback<CleanupResult, CleanupSuggestion, CancellationToken>((_, suggestion, _) => loggedSuggestion = suggestion)
            .Returns(Task.CompletedTask);

        var service = new ScanResultDeletionService(deleter.Object, cleanupLog.Object, scanRepo.Object);
        var outcome = await service.DeleteAsync(file, DeletionMethod.RecycleBin);

        outcome.Success.Should().BeTrue();
        deleter.Verify(d => d.DeleteAsync(
            It.Is<DeletionRequest>(request =>
                request.ExpectedSnapshot != null &&
                request.ExpectedSnapshot.Identity == file.Identity &&
                request.ExpectedSnapshot.SizeBytes == file.SizeBytes &&
                request.ExpectedSnapshot.LastWriteUtc == file.ModifiedUtc),
            It.IsAny<CancellationToken>()), Times.Once);
        scanRepo.Verify(r => r.DeleteFileEntryAsync(file.Id, It.IsAny<CancellationToken>()), Times.Once);
        scanRepo.Verify(r => r.MarkSessionStaleAsync(
            file.SessionId,
            It.Is<string>(reason => reason.Contains(file.FullPath, StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        cleanupLog.Verify(l => l.LogResultAsync(It.IsAny<CleanupResult>(), It.IsAny<CleanupSuggestion>(), It.IsAny<CancellationToken>()), Times.Once);

        loggedSuggestion.Should().NotBeNull();
        loggedSuggestion!.Category.Should().Be(CleanupCategory.Custom);
        using var audit = JsonDocument.Parse(loggedSuggestion.AuditDataJson!);
        audit.RootElement.GetProperty("Source").GetString().Should().Be("ResultsPage");
        audit.RootElement.GetProperty("FileEntryId").GetInt64().Should().Be(file.Id);
        audit.RootElement.GetProperty("Method").GetString().Should().Be(nameof(DeletionMethod.RecycleBin));
    }

    [Fact]
    public async Task DeleteAsync_Failure_DoesNotTouchRepositories()
    {
        var deleter = new Mock<IFileDeleter>();
        var cleanupLog = new Mock<ICleanupLogRepository>();
        var scanRepo = new Mock<IScanRepository>();
        var file = MakeFileEntry();

        deleter.Setup(d => d.DeleteAsync(
                It.IsAny<DeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeletionOutcome(file.FullPath, false, 0, "Locked"));

        var service = new ScanResultDeletionService(deleter.Object, cleanupLog.Object, scanRepo.Object);
        var outcome = await service.DeleteAsync(file, DeletionMethod.Permanent);

        outcome.Success.Should().BeFalse();
        scanRepo.Verify(r => r.DeleteFileEntryAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        scanRepo.Verify(r => r.MarkSessionStaleAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        cleanupLog.Verify(l => l.LogResultAsync(It.IsAny<CleanupResult>(), It.IsAny<CleanupSuggestion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_LegacyEntryWithoutIdentity_RequiresRescanAndNeverCallsDeleter()
    {
        var deleter = new Mock<IFileDeleter>(MockBehavior.Strict);
        var service = new ScanResultDeletionService(
            deleter.Object,
            Mock.Of<ICleanupLogRepository>(),
            Mock.Of<IScanRepository>());

        var outcome = await service.DeleteAsync(
            MakeFileEntry() with { Identity = null },
            DeletionMethod.Permanent);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("Re-run the scan");
        deleter.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(999ul)]
    [InlineData(null)]
    public async Task DeleteAsync_SameMetadataButDifferentOrMissingLiveIdentity_RefusesReplacement(
        ulong? liveFileIndex)
    {
        var file = MakeFileEntry();
        var snapshots = new Mock<IFileSnapshotProvider>();
        snapshots
            .Setup(provider => provider.TakeSnapshotAsync(file.FullPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSnapshot(
                file.FullPath,
                liveFileIndex is { } fileIndex
                    ? new FileIdentity("TESTVOL", fileIndex)
                    : null,
                file.SizeBytes,
                file.ModifiedUtc,
                file.Attributes));
        var service = new ScanResultDeletionService(
            new FileDeleter(NullLogger<FileDeleter>.Instance, snapshots.Object),
            Mock.Of<ICleanupLogRepository>(),
            Mock.Of<IScanRepository>());

        var outcome = await service.DeleteAsync(file, DeletionMethod.Permanent);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("changed or was replaced");
        snapshots.Verify(provider => provider.TakeSnapshotAsync(
            file.FullPath,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_AuditFailure_StillReportsDeletionAndUpdatesScanState()
    {
        var deleter = new Mock<IFileDeleter>();
        var cleanupLog = new Mock<ICleanupLogRepository>();
        var scanRepo = new Mock<IScanRepository>();
        var file = MakeFileEntry();
        deleter.Setup(d => d.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeletionOutcome(file.FullPath, true, file.SizeBytes));
        cleanupLog.Setup(l => l.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                CancellationToken.None))
            .ThrowsAsync(new IOException("database unavailable"));

        var service = new ScanResultDeletionService(deleter.Object, cleanupLog.Object, scanRepo.Object);
        var outcome = await service.DeleteAsync(file, DeletionMethod.RecycleBin);

        outcome.Success.Should().BeTrue();
        outcome.Error.Should().Contain("write deletion audit");
        scanRepo.Verify(r => r.DeleteFileEntryAsync(file.Id, CancellationToken.None), Times.Once);
        scanRepo.Verify(r => r.MarkSessionStaleAsync(file.SessionId, It.IsAny<string>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_ScanUpdateFailure_StillWritesAuditAndReturnsAccurateOutcome()
    {
        var deleter = new Mock<IFileDeleter>();
        var cleanupLog = new Mock<ICleanupLogRepository>();
        var scanRepo = new Mock<IScanRepository>();
        var file = MakeFileEntry();
        deleter.Setup(d => d.DeleteAsync(It.IsAny<DeletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeletionOutcome(file.FullPath, true, file.SizeBytes));
        scanRepo.Setup(r => r.DeleteFileEntryAsync(file.Id, CancellationToken.None))
            .ThrowsAsync(new IOException("database unavailable"));

        var service = new ScanResultDeletionService(deleter.Object, cleanupLog.Object, scanRepo.Object);
        var outcome = await service.DeleteAsync(file, DeletionMethod.RecycleBin);

        outcome.Success.Should().BeTrue();
        outcome.Error.Should().Contain("remove stale scan entry");
        cleanupLog.Verify(l => l.LogResultAsync(
            It.IsAny<CleanupResult>(),
            It.IsAny<CleanupSuggestion>(),
            CancellationToken.None), Times.Once);
        scanRepo.Verify(r => r.MarkSessionStaleAsync(file.SessionId, It.IsAny<string>(), CancellationToken.None), Times.Once);
    }

    private static FileEntry MakeFileEntry() => new()
    {
        Id = 44,
        SessionId = 9,
        FullPath = @"C:\data\sample.bin",
        FileName = "sample.bin",
        Extension = ".bin",
        SizeBytes = 8192,
        CreatedUtc = DateTime.UtcNow,
        ModifiedUtc = DateTime.UtcNow,
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Unknown,
        Identity = new FileIdentity("TESTVOL", 44),
    };
}
