using System.Text.Json;
using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

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
    };
}
