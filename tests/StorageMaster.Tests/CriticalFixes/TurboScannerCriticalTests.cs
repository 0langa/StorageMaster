using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;
using StorageMaster.Platform.Windows;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C2/C3: TurboFileScanner must kill the process on cancellation and treat
/// non-zero exit codes as failure (never mark Completed).
///
/// Native-path tests inject a deterministic PowerShell producer so process,
/// cancellation, persistence, and terminal-session semantics are exercised
/// even when turbo-scanner.exe has not been built beside the test assembly.
/// </summary>
public sealed class TurboScannerCriticalTests
{
    private readonly Mock<IScanRepository> _repoMock = new();

    public TurboScannerCriticalTests()
    {
        _repoMock.Setup(r => r.CreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanSession { Id = 1, RootPath = "C:\\", StartedUtc = DateTime.UtcNow, Status = ScanStatus.Running });
        _repoMock.Setup(r => r.UpdateSessionAsync(It.IsAny<ScanSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoMock.Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoMock.Setup(r => r.UpsertFolderEntriesAsync(It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoMock.Setup(r => r.GetAllFolderPathsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FolderEntry>());
        _repoMock.Setup(r => r.UpdateFolderTotalsAsync(It.IsAny<long>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Cancellation_ThroughFallback_ReturnsCancelledSession()
    {
        // When turbo-scanner.exe is unavailable, TurboFileScanner delegates to
        // the managed FileScanner. Cancellation must propagate correctly.
        var root = CreateTempDir(files: 2);
        var managed = new FileScanner(
            _repoMock.Object,
            NullLogger<FileScanner>.Instance,
            Mock.Of<IFileIdentityProvider>());
        var turbo = new TurboFileScanner(
            _repoMock.Object,
            NullLogger<TurboFileScanner>.Instance,
            managed);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel before starting.

        try
        {
            var session = await turbo.ScanAsync(
                new ScanOptions { RootPath = root },
                new Progress<ScanProgress>(),
                cts.Token);

            session.Status.Should().Be(ScanStatus.Cancelled,
                "cancelled scan must never be marked Completed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FallbackScanner_Completes_WhenBinaryMissing()
    {
        var root = CreateTempDir(files: 3);
        var managed = new FileScanner(
            _repoMock.Object,
            NullLogger<FileScanner>.Instance,
            Mock.Of<IFileIdentityProvider>());
        var turbo = new TurboFileScanner(
            _repoMock.Object,
            NullLogger<TurboFileScanner>.Instance,
            managed);

        try
        {
            // TurboFileScanner.IsAvailable will be false in test → falls back.
            var session = await turbo.ScanAsync(
                new ScanOptions { RootPath = root, MaxParallelism = 1 },
                new Progress<ScanProgress>());

            session.Status.Should().Be(ScanStatus.Completed);
            session.TotalFiles.Should().BeGreaterThanOrEqualTo(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenBinaryMissing()
    {
        // In test environment, turbo-scanner.exe is not next to the test DLL.
        TurboFileScanner.IsAvailable.Should().BeFalse(
            "turbo-scanner.exe should not be in test output directory");
    }

    [Fact]
    public void DefaultProcessContract_EnablesLinkFollowingOnlyWhenRequested()
    {
        var root = CreateTempDir(files: 0);
        try
        {
            var safe = TurboFileScanner.CreateDefaultProcessStartInfo(new ScanOptions
            {
                RootPath = root,
                FollowSymlinks = false,
                IncludeHiddenFiles = true,
            });
            var optedIn = TurboFileScanner.CreateDefaultProcessStartInfo(new ScanOptions
            {
                RootPath = root,
                FollowSymlinks = true,
                IncludeHiddenFiles = true,
            });

            safe.ArgumentList.Should().NotContain("--follow-links");
            optedIn.ArgumentList.Should().ContainSingle(argument => argument == "--follow-links");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadTraversalGuard_BlocksRenameAndReparseReplacement()
    {
        var container = CreateTempDir(files: 0);
        var guardedRoot = Path.Combine(container, "guarded-root");
        var movedRoot = Path.Combine(container, "moved-root");
        var target = CreateTempDir(files: 1);
        Directory.CreateDirectory(guardedRoot);

        try
        {
            using (var guard = DirectoryTraversalInterop.TryOpenNoFollowForReadTraversal(guardedRoot))
            {
                guard.Should().NotBeNull();
                guard!.IsReparsePoint.Should().BeFalse();
                guard.BlocksReplacement.Should().BeTrue(
                    "user-owned scan roots should receive a strong rename/delete lock");
                Directory.EnumerateFileSystemEntries(guardedRoot).Should().BeEmpty(
                    "read-only traversal must remain available while guarded");

                var rename = () => Directory.Move(guardedRoot, movedRoot);
                rename.Should().Throw<IOException>(
                    "no-delete sharing must prevent root replacement during native traversal");

                var replacement = () =>
                {
                    Directory.Delete(guardedRoot);
                    CreateJunction(guardedRoot, target).Should().BeTrue();
                };
                replacement.Should().Throw<IOException>(
                    "write/delete denial must prevent conversion into a junction");
                Directory.Exists(guardedRoot).Should().BeTrue();
                File.Exists(Path.Combine(guardedRoot, "f0.txt")).Should().BeFalse(
                    "guarded path must not resolve into replacement target");
            }

            Directory.Move(guardedRoot, movedRoot);
            Directory.Exists(movedRoot).Should().BeTrue(
                "rename should work after guard release, proving guard caused earlier failure");
            Directory.Move(movedRoot, guardedRoot);
            Directory.Delete(guardedRoot);
            CreateJunction(guardedRoot, target).Should().BeTrue(
                "reparse replacement should work after guard release");
            File.Exists(Path.Combine(guardedRoot, "f0.txt")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(guardedRoot))
                Directory.Delete(guardedRoot);
            if (Directory.Exists(movedRoot))
                Directory.Delete(movedRoot);
            Directory.Delete(container, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task NativeScan_PersistsFolderMetricsAndMatchingSessionCounts()
    {
        var root = CreateTempDir(files: 0);
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);

        var records = new[]
        {
            JsonRecord(root, size: 0, isDirectory: true),
            JsonRecord(Path.Combine(root, "root.bin"), size: 10, isDirectory: false),
            JsonRecord(child, size: 0, isDirectory: true),
            JsonRecord(Path.Combine(child, "one.bin"), size: 20, isDirectory: false),
            JsonRecord(Path.Combine(child, "two.bin"), size: 5, isDirectory: false),
        };

        var persistedFiles = new List<FileEntry>();
        var persistedFolders = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, long>? folderTotals = null;
        var terminalSessions = new List<ScanSession>();
        ConfigurePersistenceCapture(persistedFiles, persistedFolders, terminalSessions,
            totals => folderTotals = totals);

        var turbo = CreateInjectedTurboScanner(
            _ => CreatePowerShellStartInfo(BuildRecordScript(records)));

        try
        {
            var result = await turbo.ScanAsync(
                new ScanOptions { RootPath = root, MaxParallelism = 1 },
                new Progress<ScanProgress>());

            result.Status.Should().Be(ScanStatus.Completed);
            result.TotalFiles.Should().Be(3);
            result.TotalFolders.Should().Be(2);
            result.TotalSizeBytes.Should().Be(35);
            persistedFiles.Should().HaveCount(3);

            persistedFolders[root].DirectSizeBytes.Should().Be(10);
            persistedFolders[root].FileCount.Should().Be(1);
            persistedFolders[root].SubFolderCount.Should().Be(1);
            persistedFolders[child].DirectSizeBytes.Should().Be(25);
            persistedFolders[child].FileCount.Should().Be(2);
            persistedFolders[child].SubFolderCount.Should().Be(0);

            folderTotals.Should().NotBeNull();
            folderTotals![root].Should().Be(35);
            folderTotals[child].Should().Be(25);
            terminalSessions.Should().ContainSingle().Which.Should().Be(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeScan_PersistsExactContractMetadata()
    {
        var root = CreateTempDir(files: 0);
        var filePath = Path.Combine(root, "metadata.bin");
        var modifiedUtc = new DateTime(2026, 8, 18, 12, 34, 56, DateTimeKind.Utc).AddTicks(7);
        var createdUtc = modifiedUtc.AddDays(-1).AddTicks(3);
        const FileAttributes attributes = FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly;
        var records = new[]
        {
            JsonRecord(root, size: 0, isDirectory: true),
            JsonSerializer.Serialize(new
            {
                path = filePath,
                size = 17,
                modified_unix = 1,
                created_unix = 1,
                modified_utc_ticks = modifiedUtc.Ticks,
                created_utc_ticks = createdUtc.Ticks,
                attributes = (uint)attributes,
                volume_serial = 0xA1B2_C3D4u,
                file_index = 0x0123_4567_89AB_CDEFul,
                is_dir = false,
                is_hidden = true,
            }),
        };

        var persistedFiles = new List<FileEntry>();
        var persistedFolders = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
        var terminalSessions = new List<ScanSession>();
        ConfigurePersistenceCapture(
            persistedFiles,
            persistedFolders,
            terminalSessions,
            onTotals: null);
        var turbo = CreateInjectedTurboScanner(
            _ => CreatePowerShellStartInfo(BuildRecordScript(records)));

        try
        {
            var result = await turbo.ScanAsync(
                new ScanOptions { RootPath = root, MaxParallelism = 1, IncludeHiddenFiles = true },
                new Progress<ScanProgress>());

            result.Status.Should().Be(ScanStatus.Completed);
            var persisted = persistedFiles.Should().ContainSingle().Subject;
            persisted.ModifiedUtc.Should().Be(modifiedUtc);
            persisted.CreatedUtc.Should().Be(createdUtc);
            persisted.Attributes.Should().Be(attributes);
            persisted.Identity.Should().Be(
                new FileIdentity("A1B2C3D4", 0x0123_4567_89AB_CDEFul));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeScan_RejectsReparsePointInRootAncestryBeforeProcessLaunch()
    {
        var container = CreateTempDir(files: 0);
        var target = CreateTempDir(files: 0);
        var targetRoot = Path.Combine(target, "scan-root");
        Directory.CreateDirectory(targetRoot);
        var junction = Path.Combine(container, "ancestor-link");
        CreateJunction(junction, target).Should().BeTrue("junction support is required for this Windows safety test");
        var linkedRoot = Path.Combine(junction, "scan-root");
        var processFactoryCalled = false;
        var turbo = CreateInjectedTurboScanner(_ =>
        {
            processFactoryCalled = true;
            return CreatePowerShellStartInfo(string.Empty);
        });

        try
        {
            var act = () => turbo.ScanAsync(
                new ScanOptions { RootPath = linkedRoot, FollowSymlinks = false },
                new Progress<ScanProgress>());

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*reparse point*");
            processFactoryCalled.Should().BeFalse("unsafe ancestry must be rejected before native launch");
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            Directory.Delete(container, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task NativeScan_CancellationKillsChildAndFlushesMatchingPartialCounts()
    {
        var root = CreateTempDir(files: 0);
        var pidPath = Path.Combine(root, "native.pid");
        var persistedFiles = new List<FileEntry>();
        var persistedFolders = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
        var terminalSessions = new List<ScanSession>();
        var firstFileBatch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ConfigurePersistenceCapture(
            persistedFiles,
            persistedFolders,
            terminalSessions,
            onTotals: null,
            onFileBatch: () => firstFileBatch.TrySetResult(true));

        var script = BuildLongRunningRecordScript(root, pidPath, fileCount: 500);
        var turbo = CreateInjectedTurboScanner(_ => CreatePowerShellStartInfo(script));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<ScanSession>? scanTask = null;

        try
        {
            scanTask = turbo.ScanAsync(
                new ScanOptions { RootPath = root, MaxParallelism = 1 },
                new Progress<ScanProgress>(),
                cts.Token);

            await firstFileBatch.Task.WaitAsync(TimeSpan.FromSeconds(20));
            cts.Cancel();

            var result = await scanTask.WaitAsync(TimeSpan.FromSeconds(20));
            var processId = int.Parse(await File.ReadAllTextAsync(pidPath));

            result.Status.Should().Be(ScanStatus.Cancelled, result.ErrorMessage);
            result.TotalFiles.Should().Be(persistedFiles.Count);
            result.TotalFolders.Should().Be(persistedFolders.Count);
            result.TotalSizeBytes.Should().Be(persistedFiles.Sum(static file => file.SizeBytes));
            persistedFolders[root].DirectSizeBytes.Should().Be(result.TotalSizeBytes);
            persistedFolders[root].FileCount.Should().Be(checked((int)result.TotalFiles));
            terminalSessions.Last().Should().Be(result);

            IsProcessExited(processId).Should().BeTrue(
                "cancellation must terminate the native child before returning");
        }
        finally
        {
            cts.Cancel();
            if (scanTask is { IsCompleted: false })
            {
                try { await scanTask.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch { /* assertion reports the primary failure */ }
            }

            if (File.Exists(pidPath) && int.TryParse(await File.ReadAllTextAsync(pidPath), out var processId))
                KillOwnedProcess(processId);

            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir(int files)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (int i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(root, $"f{i}.txt"), "data");
        return root;
    }

    private static bool CreateJunction(string junction, string target)
    {
        var startInfo = new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{junction}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(junction);
    }

    private TurboFileScanner CreateInjectedTurboScanner(
        Func<ScanOptions, ProcessStartInfo> processStartInfoFactory) =>
        new(
            _repoMock.Object,
            NullLogger<TurboFileScanner>.Instance,
            Mock.Of<IFileScanner>(),
            errorRepo: null,
            processStartInfoFactory: processStartInfoFactory);

    private void ConfigurePersistenceCapture(
        List<FileEntry> files,
        Dictionary<string, FolderEntry> folders,
        List<ScanSession> terminalSessions,
        Action<IReadOnlyDictionary<string, long>>? onTotals,
        Action? onFileBatch = null)
    {
        _repoMock
            .Setup(r => r.InsertFileEntriesAsync(
                It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((batch, _) =>
            {
                files.AddRange(batch);
                onFileBatch?.Invoke();
            })
            .Returns(Task.CompletedTask);

        _repoMock
            .Setup(r => r.UpsertFolderEntriesAsync(
                It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FolderEntry>, CancellationToken>((batch, _) =>
            {
                foreach (var entry in batch)
                {
                    folders[entry.FullPath] = folders.TryGetValue(entry.FullPath, out var existing)
                        ? existing with
                        {
                            DirectSizeBytes = existing.DirectSizeBytes + entry.DirectSizeBytes,
                            TotalSizeBytes = existing.TotalSizeBytes + entry.TotalSizeBytes,
                            FileCount = existing.FileCount + entry.FileCount,
                            SubFolderCount = entry.SubFolderCount,
                        }
                        : entry;
                }
            })
            .Returns(Task.CompletedTask);

        _repoMock
            .Setup(r => r.GetAllFolderPathsForSessionAsync(
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (IReadOnlyList<FolderEntry>)folders.Values.ToArray());

        _repoMock
            .Setup(r => r.UpdateFolderTotalsAsync(
                It.IsAny<long>(), It.IsAny<IReadOnlyDictionary<string, long>>(),
                It.IsAny<CancellationToken>()))
            .Callback<long, IReadOnlyDictionary<string, long>, CancellationToken>(
                (_, totals, _) => onTotals?.Invoke(totals))
            .Returns(Task.CompletedTask);

        _repoMock
            .Setup(r => r.UpdateSessionAsync(
                It.IsAny<ScanSession>(), It.IsAny<CancellationToken>()))
            .Callback<ScanSession, CancellationToken>((session, _) => terminalSessions.Add(session))
            .Returns(Task.CompletedTask);
    }

    private static string JsonRecord(string path, long size, bool isDirectory) =>
        JsonSerializer.Serialize(new
        {
            path,
            size,
            modified_unix = 1,
            created_unix = 1,
            is_dir = isDirectory,
            is_hidden = false,
        });

    private static string BuildRecordScript(IEnumerable<string> records)
    {
        var encodedRecords = records
            .Select(record => $"'{Convert.ToBase64String(Encoding.UTF8.GetBytes(record))}'");

        return $$"""
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $records = @({{string.Join(",", encodedRecords)}})
            foreach ($record in $records) {
                [Console]::Out.WriteLine([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($record)))
            }
            [Console]::Out.Flush()
            """;
    }

    private static string BuildLongRunningRecordScript(string root, string pidPath, int fileCount)
    {
        var rootBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(root));
        var pidBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pidPath));

        return $$"""
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $root = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{rootBase64}}'))
            $pidPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{pidBase64}}'))
            [IO.File]::WriteAllText($pidPath, [string]$PID)
            $folder = [ordered]@{ path = $root; size = 0; modified_unix = 1; created_unix = 1; is_dir = $true; is_hidden = $false }
            [Console]::Out.WriteLine(($folder | ConvertTo-Json -Compress))
            for ($index = 0; $index -lt {{fileCount}}; $index++) {
                $file = [ordered]@{ path = [IO.Path]::Combine($root, "file-$index.bin"); size = 1; modified_unix = 1; created_unix = 1; is_dir = $false; is_hidden = $false }
                [Console]::Out.WriteLine(($file | ConvertTo-Json -Compress))
            }
            [Console]::Out.Flush()
            Start-Sleep -Seconds 60
            """;
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string script)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        return startInfo;
    }

    private static bool IsProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void KillOwnedProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
    }
}
