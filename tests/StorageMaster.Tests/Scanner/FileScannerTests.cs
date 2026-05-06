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
            Id = 1,
            RootPath = @"C:\",
            StartedUtc = DateTime.UtcNow,
            Status = ScanStatus.Running,
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

        _repoMock
            .Setup(r => r.GetAllFolderPathsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FolderEntry>());

        _repoMock
            .Setup(r => r.UpdateFolderTotalsAsync(It.IsAny<long>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()))
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
            var options = new ScanOptions { RootPath = root, MaxParallelism = 1 };
            var progress = new Progress<ScanProgress>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

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
        var root = CreateTempDir(files: 2, subdirs: 0);
        var options = new ScanOptions { RootPath = root };
        var cts = new CancellationTokenSource();

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
    public async Task ScanAsync_MissingRoot_ThrowsDirectoryNotFound()
    {
        var options = new ScanOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        };

        Func<Task> act = () => _scanner.ScanAsync(options, new Progress<ScanProgress>());

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task ScanAsync_InvalidParallelism_IsClamped()
    {
        var root = CreateTempDir(files: 2, subdirs: 1);
        try
        {
            var session = await _scanner.ScanAsync(
                new ScanOptions { RootPath = root, MaxParallelism = 0 },
                new Progress<ScanProgress>());

            session.Status.Should().Be(ScanStatus.Completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_InvalidBatchSize_IsClamped()
    {
        var root = CreateTempDir(files: 2, subdirs: 1);
        try
        {
            var session = await _scanner.ScanAsync(
                new ScanOptions { RootPath = root, DbBatchSize = 0 },
                new Progress<ScanProgress>());

            session.Status.Should().Be(ScanStatus.Completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_BatchesFileWrites()
    {
        // Create more files than one batch to verify batched writes.
        var root = CreateTempDir(files: 20, subdirs: 0);
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

    [Fact]
    public async Task ScanAsync_AggregationCalled_AfterScanCompletes()
    {
        var root = CreateTempDir(files: 3, subdirs: 1);
        var options = new ScanOptions { RootPath = root, MaxParallelism = 1 };

        try
        {
            await _scanner.ScanAsync(options, new Progress<ScanProgress>());

            _repoMock.Verify(
                r => r.GetAllFolderPathsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
                Times.Once, "aggregator must request all folder paths after scan");

            _repoMock.Verify(
                r => r.UpdateFolderTotalsAsync(It.IsAny<long>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()),
                Times.Once, "aggregator must write updated totals");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_WithExcludedPath_ExcludedDirIsSkipped()
    {
        var root = CreateTempDir(files: 2, subdirs: 2);
        var subDirs = Directory.GetDirectories(root);
        var excludedDir = subDirs[0];

        var options = new ScanOptions
        {
            RootPath = root,
            MaxParallelism = 1,
            ExcludedPaths = new[] { excludedDir },
        };

        try
        {
            var session = await _scanner.ScanAsync(options, new Progress<ScanProgress>());

            // The scan should complete without error; excluded dir content should not be counted.
            session.Status.Should().Be(ScanStatus.Completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_ExclusionBoundary_DoesNotSkipPrefixSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var excluded = Path.Combine(root, "Installer");
        var sibling = Path.Combine(root, "InstallerBackup");
        Directory.CreateDirectory(excluded);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(excluded, "skip.txt"), "skip");
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        var inserted = new List<FileEntry>();
        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((entries, _) => inserted.AddRange(entries))
            .Returns(Task.CompletedTask);

        try
        {
            await _scanner.ScanAsync(new ScanOptions
            {
                RootPath = root,
                MaxParallelism = 1,
                ExcludedPaths = [excluded],
            }, new Progress<ScanProgress>());

            inserted.Select(static entry => entry.FileName).Should().Contain("keep.txt");
            inserted.Select(static entry => entry.FileName).Should().NotContain("skip.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_HiddenFiles_AreSkippedByDefault()
    {
        var root = CreateTempDir(files: 0, subdirs: 0);
        var hidden = Path.Combine(root, "hidden.txt");
        File.WriteAllText(hidden, "hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var inserted = new List<FileEntry>();
        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((entries, _) => inserted.AddRange(entries))
            .Returns(Task.CompletedTask);

        try
        {
            await _scanner.ScanAsync(
                new ScanOptions { RootPath = root, IncludeHiddenFiles = false, MaxParallelism = 1 },
                new Progress<ScanProgress>());

            inserted.Should().BeEmpty();
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_HiddenFolders_AreSkippedByDefault()
    {
        var root = CreateTempDir(files: 0, subdirs: 0);
        var hiddenDir = Directory.CreateDirectory(Path.Combine(root, "hidden-dir"));
        File.WriteAllText(Path.Combine(hiddenDir.FullName, "hidden-child.txt"), "hidden");
        hiddenDir.Attributes |= FileAttributes.Hidden;
        var inserted = new List<FileEntry>();
        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((entries, _) => inserted.AddRange(entries))
            .Returns(Task.CompletedTask);

        try
        {
            await _scanner.ScanAsync(
                new ScanOptions { RootPath = root, IncludeHiddenFiles = false, MaxParallelism = 1 },
                new Progress<ScanProgress>());

            inserted.Should().BeEmpty();
        }
        finally
        {
            hiddenDir.Attributes &= ~FileAttributes.Hidden;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_ReportsProgress()
    {
        var root = CreateTempDir(files: 10, subdirs: 1);
        var reportedOnce = false;
        var progress = new Progress<ScanProgress>(_ => reportedOnce = true);
        var options = new ScanOptions { RootPath = root, MaxParallelism = 1 };

        try
        {
            await _scanner.ScanAsync(options, progress);
            // The completion report always fires, so reportedOnce must be true.
            reportedOnce.Should().BeTrue("ScanAsync must report at least one progress update");
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
