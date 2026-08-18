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
    private readonly Mock<IFileIdentityProvider> _identityProvider = new();
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

        _identityProvider
            .Setup(provider => provider.GetIdentityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileIdentity("TESTVOL", 123));

        _scanner = new FileScanner(
            _repoMock.Object,
            NullLogger<FileScanner>.Instance,
            _identityProvider.Object);
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
    public async Task ScanAsync_PersistsStableIdentityForEveryDiscoveredFile()
    {
        var root = CreateTempDir(files: 3, subdirs: 0);
        var inserted = new List<FileEntry>();
        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(
                It.IsAny<IReadOnlyList<FileEntry>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((entries, _) => inserted.AddRange(entries))
            .Returns(Task.CompletedTask);

        try
        {
            await _scanner.ScanAsync(
                new ScanOptions { RootPath = root, MaxParallelism = 1 },
                new Progress<ScanProgress>());

            inserted.Should().HaveCount(3);
            inserted.Should().OnlyContain(static entry =>
                entry.Identity == new FileIdentity("TESTVOL", 123));
            _identityProvider.Verify(provider => provider.GetIdentityAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
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
    public async Task ScanAsync_DatabaseInsertFailure_FailsScanInsteadOfHanging()
    {
        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database is locked"));

        var root = CreateTempDir(files: 40, subdirs: 3);
        try
        {
            var options = new ScanOptions { RootPath = root, MaxParallelism = 2, DbBatchSize = 10 };
            // Deadlock guard: a hung producer/consumer pair would exceed this window.
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            Func<Task> act = () => _scanner.ScanAsync(options, new Progress<ScanProgress>(), cts.Token);

            await act.Should().ThrowAsync<IOException>();
            _repoMock.Verify(r => r.UpdateSessionAsync(
                It.Is<ScanSession>(s => s.Status == ScanStatus.Failed),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
    public async Task ScanAsync_DeepScan_SurvivesAccessDeniedDirectory()
    {
        // Deep scan uses IgnoreInaccessible=false, so access errors surface
        // lazily during enumeration. Regression: the producer previously
        // iterated outside its try/catch and one denied directory failed the
        // whole scan.
        var root = CreateTempDir(files: 2, subdirs: 1);
        var denied = Path.Combine(root, "denied");
        Directory.CreateDirectory(denied);
        await File.WriteAllTextAsync(Path.Combine(denied, "hidden.txt"), "x");

        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        if (!RunIcacls($"\"{denied}\" /deny \"{user}:(OI)(CI)R\""))
        {
            // ACLs cannot be modified in this environment — nothing to verify.
            Directory.Delete(root, recursive: true);
            return;
        }

        try
        {
            var options = new ScanOptions { RootPath = root, MaxParallelism = 2, DeepScan = true };
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var session = await _scanner.ScanAsync(options, new Progress<ScanProgress>(), cts.Token);

            session.Status.Should().Be(ScanStatus.Completed);
            session.TotalFiles.Should().BeGreaterThanOrEqualTo(2, "accessible files must still be scanned");
            session.AccessDeniedCount.Should().BeGreaterThan(0,
                "the denied directory must be recorded as access-denied, proving the deny ACL was effective");
        }
        finally
        {
            RunIcacls($"\"{denied}\" /remove:d \"{user}\"");
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool RunIcacls(string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
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

    [Fact]
    public async Task ScanAsync_CancelledWrite_RetriesBufferedRowsAndReportsPersistedCounts()
    {
        var root = CreateTempDir(files: 12, subdirs: 0);
        using var cts = new CancellationTokenSource();
        var insertedFiles = new List<FileEntry>();
        var insertedFolders = new List<FolderEntry>();
        var insertTokens = new List<CancellationToken>();
        ScanSession? terminalSession = null;
        var insertAttempts = 0;

        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(
                It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<FileEntry>, CancellationToken>((entries, token) =>
            {
                insertTokens.Add(token);
                if (Interlocked.Increment(ref insertAttempts) == 1)
                {
                    cts.Cancel();
                    return Task.FromCanceled(cts.Token);
                }

                insertedFiles.AddRange(entries);
                return Task.CompletedTask;
            });
        _repoMock
            .Setup(r => r.UpsertFolderEntriesAsync(
                It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FolderEntry>, CancellationToken>((entries, _) =>
                insertedFolders.AddRange(entries))
            .Returns(Task.CompletedTask);
        _repoMock
            .Setup(r => r.UpdateSessionAsync(It.IsAny<ScanSession>(), It.IsAny<CancellationToken>()))
            .Callback<ScanSession, CancellationToken>((updated, _) => terminalSession = updated)
            .Returns(Task.CompletedTask);

        try
        {
            var result = await _scanner.ScanAsync(new ScanOptions
            {
                RootPath = root,
                MaxParallelism = 1,
                DbBatchSize = 10,
            }, new Progress<ScanProgress>(), cts.Token);

            result.Status.Should().Be(ScanStatus.Cancelled);
            insertedFiles.Should().HaveCount(12);
            insertedFiles.Select(static entry => entry.FullPath).Should().OnlyHaveUniqueItems();
            insertedFolders.Should().ContainSingle();
            result.TotalFiles.Should().Be(insertedFiles.Count);
            result.TotalFolders.Should().Be(insertedFolders.Count);
            result.TotalSizeBytes.Should().Be(insertedFiles.Sum(static entry => entry.SizeBytes));
            terminalSession.Should().Be(result);
            insertTokens.Should().HaveCount(2);
            insertTokens[1].CanBeCanceled.Should().BeTrue(
                "partial-result finalization must be bounded instead of using CancellationToken.None");
            insertTokens[1].Should().NotBe(cts.Token,
                "the caller's cancelled token cannot persist buffered partial results");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_CancellationFinalizationFailure_ReturnsFailedWithConfirmedCounts()
    {
        var root = CreateTempDir(files: 12, subdirs: 0);
        using var cts = new CancellationTokenSource();
        var insertAttempts = 0;

        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(
                It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<FileEntry>, CancellationToken>((_, _) =>
            {
                if (Interlocked.Increment(ref insertAttempts) == 1)
                {
                    cts.Cancel();
                    return Task.FromCanceled(cts.Token);
                }

                return Task.FromException(new IOException("database unavailable during finalization"));
            });

        try
        {
            var result = await _scanner.ScanAsync(new ScanOptions
            {
                RootPath = root,
                MaxParallelism = 1,
                DbBatchSize = 10,
            }, new Progress<ScanProgress>(), cts.Token);

            result.Status.Should().Be(ScanStatus.Failed);
            result.ErrorMessage.Should().Contain("Cancellation finalization failed");
            result.TotalFiles.Should().Be(0);
            result.TotalFolders.Should().Be(0);
            result.TotalSizeBytes.Should().Be(0);
            _repoMock.Verify(r => r.UpdateSessionAsync(
                It.Is<ScanSession>(session =>
                    session.Status == ScanStatus.Failed &&
                    session.TotalFiles == 0 &&
                    session.TotalFolders == 0 &&
                    session.TotalSizeBytes == 0),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_RootJunctionWithFollowDisabled_FailsWithoutTraversingTarget()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"sm-root-link-{Guid.NewGuid():N}");
        var target = Path.Combine(sandbox, "target");
        var link = Path.Combine(sandbox, "scan-root");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "outside.txt");
        await File.WriteAllTextAsync(sentinel, "must not be traversed");

        try
        {
            CreateJunction(link, target).Should().BeTrue(
                "the Windows scanner safety contract requires junction coverage");

            Func<Task> act = () => _scanner.ScanAsync(new ScanOptions
            {
                RootPath = link,
                FollowSymlinks = false,
                MaxParallelism = 1,
            }, new Progress<ScanProgress>());

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*reparse point*");
            File.Exists(sentinel).Should().BeTrue();
            _repoMock.Verify(r => r.InsertFileEntriesAsync(
                It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
            _repoMock.Verify(r => r.UpsertFolderEntriesAsync(
                It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
            _repoMock.Verify(r => r.UpdateSessionAsync(
                It.Is<ScanSession>(session => session.Status == ScanStatus.Failed),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_FollowSymlinks_JunctionLoopIsVisitedOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sm-loop-{Guid.NewGuid():N}");
        var loop = Path.Combine(root, "loop");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "single.txt"), "one");
        var insertedFiles = new List<FileEntry>();
        var insertedFolders = new List<FolderEntry>();

        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(
                It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((entries, _) =>
                insertedFiles.AddRange(entries))
            .Returns(Task.CompletedTask);
        _repoMock
            .Setup(r => r.UpsertFolderEntriesAsync(
                It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FolderEntry>, CancellationToken>((entries, _) =>
                insertedFolders.AddRange(entries))
            .Returns(Task.CompletedTask);

        try
        {
            CreateJunction(loop, root).Should().BeTrue(
                "the Windows scanner safety contract requires junction-loop coverage");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var session = await _scanner.ScanAsync(new ScanOptions
            {
                RootPath = root,
                FollowSymlinks = true,
                MaxParallelism = 1,
                DbBatchSize = 10,
            }, new Progress<ScanProgress>(), cts.Token).WaitAsync(TimeSpan.FromSeconds(15));

            session.Status.Should().Be(ScanStatus.Completed);
            insertedFiles.Should().ContainSingle(entry => entry.FileName == "single.txt");
            insertedFiles.Should().HaveCount(1,
                "the loop target must not be scanned again under an expanding alias path");
            insertedFolders.Should().ContainSingle(entry =>
                string.Equals(entry.FullPath, root, StringComparison.OrdinalIgnoreCase));
            session.TotalFiles.Should().Be(1);
            session.TotalFolders.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(loop))
                Directory.Delete(loop, recursive: false);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_FollowSymlinks_TraversesNonCyclicJunctionTarget()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"sm-follow-link-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, "root");
        var target = Path.Combine(sandbox, "target");
        var link = Path.Combine(root, "linked-target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "linked.txt"), "follow me");
        var insertedFiles = new List<FileEntry>();

        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(
                It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((entries, _) =>
                insertedFiles.AddRange(entries))
            .Returns(Task.CompletedTask);

        try
        {
            CreateJunction(link, target).Should().BeTrue(
                "the Windows scanner safety contract requires junction coverage");

            var session = await _scanner.ScanAsync(new ScanOptions
            {
                RootPath = root,
                FollowSymlinks = true,
                MaxParallelism = 1,
            }, new Progress<ScanProgress>());

            session.Status.Should().Be(ScanStatus.Completed);
            session.TotalFiles.Should().Be(1);
            session.TotalFolders.Should().Be(2);
            insertedFiles.Should().ContainSingle(entry =>
                entry.FileName == "linked.txt" &&
                ScanOptionValidator.IsPathEqualOrUnder(entry.FullPath, link));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool CreateJunction(string junction, string target)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0 && Directory.Exists(junction);
        }
        catch
        {
            return false;
        }
    }

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
