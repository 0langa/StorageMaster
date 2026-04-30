using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C1: Verifies that concurrent buffer flushes don't lose or duplicate entries.
/// Multiple consumer tasks can hit the flush threshold simultaneously — the
/// SemaphoreSlim serialises drains so every entry is written exactly once.
/// </summary>
public sealed class C1_FlushLockTests
{
    [Fact]
    public async Task ConcurrentFlushes_NoLostOrDuplicateEntries()
    {
        // Arrange: tiny batch size so flushes trigger on every directory.
        var insertedFiles   = new List<List<FileEntry>>();
        var insertedFolders = new List<List<FolderEntry>>();
        var insertLock      = new object();

        var repoMock = new Mock<IScanRepository>();
        repoMock.Setup(r => r.CreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanSession { Id = 1, RootPath = "C:\\", StartedUtc = DateTime.UtcNow, Status = ScanStatus.Running });
        repoMock.Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<FileEntry>, CancellationToken>((entries, _) =>
            {
                lock (insertLock) insertedFiles.Add(entries.ToList());
                return Task.CompletedTask;
            });
        repoMock.Setup(r => r.UpsertFolderEntriesAsync(It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<FolderEntry>, CancellationToken>((entries, _) =>
            {
                lock (insertLock) insertedFolders.Add(entries.ToList());
                return Task.CompletedTask;
            });
        repoMock.Setup(r => r.UpdateSessionAsync(It.IsAny<ScanSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoMock.Setup(r => r.GetAllFolderPathsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FolderEntry>());
        repoMock.Setup(r => r.UpdateFolderTotalsAsync(It.IsAny<long>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var scanner = new FileScanner(repoMock.Object, NullLogger<FileScanner>.Instance);

        // Create a directory with enough files to trigger multiple flushes under concurrency.
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (int d = 0; d < 4; d++)
        {
            var sub = Path.Combine(root, $"dir{d}");
            Directory.CreateDirectory(sub);
            for (int f = 0; f < 10; f++)
                File.WriteAllText(Path.Combine(sub, $"f{f}.txt"), "x");
        }

        try
        {
            // Act: scan with high concurrency + tiny batch size → maximises flush contention.
            var options = new ScanOptions { RootPath = root, MaxParallelism = 4, DbBatchSize = 5 };
            var session = await scanner.ScanAsync(options, new Progress<ScanProgress>());

            // Assert: every file written to disk was inserted exactly once.
            var allInsertedFiles = insertedFiles.SelectMany(b => b).ToList();
            allInsertedFiles.Select(f => f.FullPath).Should().OnlyHaveUniqueItems(
                "each file must appear in exactly one batch (no duplicates)");

            session.Status.Should().Be(ScanStatus.Completed);
            session.TotalFiles.Should().Be(40, "4 dirs × 10 files = 40");
            allInsertedFiles.Should().HaveCount(40, "no entries should be lost");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
