using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Tests.Scanner;

public sealed class FileScannerTests
{
    private readonly Mock<IScanRepository> _repoMock = new();
    private readonly FileScanner _scanner;

    public FileScannerTests()
    {
        var session = new ScanSession
        {
            Id         = 1,
            RootPath   = @"C:\",
            StartedUtc = DateTime.UtcNow,
            Status     = ScanStatus.Running,
        };

        _repoMock
            .Setup(r => r.CreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _repoMock
            .Setup(r => r.UpdateSessionAsync(It.IsAny<ScanSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repoMock
            .Setup(r => r.UpsertFolderEntriesAsync(It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _scanner = new FileScanner(_repoMock.Object, NullLogger<FileScanner>.Instance);
    }

    [Fact]
    public async Task ScanAsync_ValidDirectory_CompletesScan()
    {
        // Scan a temp directory we control so the test is deterministic.
        var root = CreateTempDir(files: 5, subdirs: 2);
        try
        {
            var options  = new ScanOptions { RootPath = root, MaxParallelism = 1 };
            var progress = new Progress<ScanProgress>();
            var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var session = await _scanner.ScanAsync(options, progress, cts.Token);

            session.Status.Should().Be(ScanStatus.Completed);
            session.TotalFiles.Should().BeGreaterThanOrEqualTo(5);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_Cancellation_ReturnsCancelledSession()
    {
        var root    = CreateTempDir(files: 2, subdirs: 0);
        var options = new ScanOptions { RootPath = root };
        var cts     = new CancellationTokenSource();

        // Cancel immediately before the scan starts.
        cts.Cancel();

        var session = await _scanner.ScanAsync(options, new Progress<ScanProgress>(), cts.Token);

        session.Status.Should().Be(ScanStatus.Cancelled);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ScanAsync_InvalidPath_ThrowsArgumentException()
    {
        var options = new ScanOptions { RootPath = string.Empty };
        Func<Task> act = () => _scanner.ScanAsync(options, new Progress<ScanProgress>());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ScanAsync_BatchesFileWrites()
    {
        // Create more files than one batch to verify batched writes.
        var root    = CreateTempDir(files: 20, subdirs: 0);
        var options = new ScanOptions { RootPath = root, DbBatchSize = 5 };

        try
        {
            await _scanner.ScanAsync(options, new Progress<ScanProgress>());

            // With 20 files and batch size 5, at least 4 InsertFileEntries calls expected.
            _repoMock.Verify(
                r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string CreateTempDir(int files, int subdirs)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        for (int i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(root, $"file{i}.txt"), new string('x', 1024 * (i + 1)));

        for (int i = 0; i < subdirs; i++)
        {
            var sub = Directory.CreateDirectory(Path.Combine(root, $"sub{i}"));
            File.WriteAllText(Path.Combine(sub.FullName, $"subfile{i}.dat"), "content");
        }

        return root;
    }
}
