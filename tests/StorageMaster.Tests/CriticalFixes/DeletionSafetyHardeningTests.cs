using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// Adversarial tests for deletion-safety and filesystem-correctness hardening.
///
/// Acceptance target: no code path can silently delete outside the selected scope,
/// cross a reparse point accidentally, or report success when only partial deletion happened.
/// </summary>
public sealed class DeletionSafetyHardeningTests
{
    private readonly FileDeleter _deleter = new(NullLogger<FileDeleter>.Instance);

    // ── IsRootOrUncPrefix — pure logic ────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]          // no trailing separator
    [InlineData(@"c:\")]         // case-insensitive
    [InlineData(@"D:\")]
    [InlineData(@"Z:\")]
    [InlineData(@"C:\Temp\..")]
    [InlineData(@"C:\.")]
    public void IsRootOrUncPrefix_DriveRoot_ReturnsTrue(string path)
        => FileDeleter.IsRootOrUncPrefix(path).Should().BeTrue($"'{path}' is a drive root");

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\SERVER\SHARE")]   // UNC, case-insensitive
    [InlineData(@"\\nas\backup")]
    [InlineData(@"\\server\share\folder\..")]
    [InlineData(@"\\server\share\.")]
    public void IsRootOrUncPrefix_UncShareRoot_ReturnsTrue(string path)
        => FileDeleter.IsRootOrUncPrefix(path).Should().BeTrue($"'{path}' is a UNC share root");

    [Theory]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\juliu")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"\\server\share\a\b\c")]
    public void IsRootOrUncPrefix_SubPath_ReturnsFalse(string path)
        => FileDeleter.IsRootOrUncPrefix(path).Should().BeFalse($"'{path}' is below a root");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsRootOrUncPrefix_EmptyOrWhitespace_ReturnsFalse(string path)
        => FileDeleter.IsRootOrUncPrefix(path).Should().BeFalse("empty/whitespace is not a root");

    // ── Root guard in DeleteAsync — all methods refused ───────────────────────

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    public async Task DeleteAsync_DriveRoot_ReturnsFailureWithoutThrowing(string root)
    {
        var outcome = await _deleter.DeleteAsync(
            new DeletionRequest(root, DeletionMethod.Permanent, DryRun: false));

        outcome.Success.Should().BeFalse("drive roots must be refused");
        outcome.Error.Should().Contain("Refusing to delete a filesystem root");
        outcome.BytesFreed.Should().Be(0);
    }

    [Theory]
    [InlineData(DeletionMethod.Permanent)]
    [InlineData(DeletionMethod.RecycleBin)]
    public async Task DeleteAsync_DriveRoot_RefusedForAllDeletionMethods(DeletionMethod method)
    {
        var outcome = await _deleter.DeleteAsync(
            new DeletionRequest(@"C:\", method, DryRun: false));

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("Refusing to delete a filesystem root");
    }

    [Fact]
    public async Task DeleteAsync_DryRunDriveRoot_IsStillRefused()
    {
        var outcome = await _deleter.DeleteAsync(
            new DeletionRequest(@"C:\", DeletionMethod.RecycleBin, DryRun: true));

        outcome.Success.Should().BeFalse("dry-run must use same root guard as real deletion");
        outcome.Error.Should().Contain("Refusing to delete a filesystem root");
        outcome.BytesFreed.Should().Be(0);
    }

    [Theory]
    [InlineData(@"C:\Temp\..")]
    [InlineData(@"C:\.")]
    [InlineData(@"\\server\share\folder\..")]
    [InlineData(@"\\server\share\.")]
    public async Task DeleteAsync_PathCanonicalizingToRoot_IsRefused(string path)
    {
        var outcome = await _deleter.DeleteAsync(
            new DeletionRequest(path, DeletionMethod.Permanent, DryRun: true));

        outcome.Success.Should().BeFalse("canonical filesystem roots must never reach deletion");
        outcome.Error.Should().Contain("Refusing to delete a filesystem root");
        outcome.Path.Should().Be(path);
    }

    [Theory]
    [InlineData("relative.txt")]
    [InlineData(@".\relative.txt")]
    [InlineData(@"..\relative.txt")]
    [InlineData(@"C:relative.txt")]
    public async Task DeleteAsync_RelativeOrDriveRelativePath_IsRefused(string path)
    {
        var outcome = await _deleter.DeleteAsync(
            new DeletionRequest(path, DeletionMethod.Permanent, DryRun: true));

        outcome.Success.Should().BeFalse("deletion must never resolve a path against process state");
        outcome.Error.Should().Contain("an absolute filesystem path is required");
        outcome.Path.Should().Be(path);
    }

    [Fact]
    public async Task DeleteManyAsync_AllInvalidRecycleBinRoots_AllAreRefused()
    {
        var requests = new[]
        {
            new DeletionRequest(@"C:\Temp\..", DeletionMethod.RecycleBin, DryRun: false),
            new DeletionRequest(@"\\server\share\folder\..", DeletionMethod.RecycleBin, DryRun: false),
        };

        var outcomes = await CollectOutcomesAsync(_deleter.DeleteManyAsync(requests));

        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => !o.Success);
        outcomes.Should().OnlyContain(o => o.Error!.Contains("Refusing to delete a filesystem root"));
    }

    [Fact]
    public void RecycleDeleteOperationFlags_ExplicitlyForceRecycleBin()
    {
        FileOperationInterop.FOFX_RECYCLEONDELETE.Should().Be(0x00080000u);
        FileOperationInterop.RecycleDeleteOperationFlags.Should().Be(
            FileOperationInterop.FOF_ALLOWUNDO |
            FileOperationInterop.FOF_NOCONFIRMATION |
            FileOperationInterop.FOF_NOERRORUI |
            FileOperationInterop.FOFX_RECYCLEONDELETE);
    }

    [Fact]
    public async Task DeleteManyAsync_MixedValidAndInvalidRecycleBinBatch_RefusesOnlyRootPath()
    {
        var file = Path.Combine(Path.GetTempPath(), $"smhard_batch_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "batch me");

        try
        {
            var requests = new[]
            {
                new DeletionRequest(@"C:\", DeletionMethod.RecycleBin, DryRun: false),
                new DeletionRequest(file, DeletionMethod.RecycleBin, DryRun: false),
            };

            var outcomes = await CollectOutcomesAsync(_deleter.DeleteManyAsync(requests));

            outcomes.Should().HaveCount(2);
            outcomes.Should().ContainSingle(o => o.Path == @"C:\" && !o.Success);
            outcomes.Should().ContainSingle(o => o.Path == file && o.Success);
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Fact]
    public async Task DeleteManyAsync_UncChildPath_IsNotRejectedAsShareRoot()
    {
        var requests = new[]
        {
            new DeletionRequest(@"\\server\share\folder\file.txt", DeletionMethod.RecycleBin, DryRun: true),
        };

        var outcomes = await CollectOutcomesAsync(_deleter.DeleteManyAsync(requests));

        outcomes.Should().ContainSingle();
        outcomes[0].Success.Should().BeTrue("a UNC child path is below the share root and must not be blocked by root guard");
        outcomes[0].Error.Should().BeNull();
    }

    // ── DeletePermanently hardening ───────────────────────────────────────────

    [Fact]
    public void DeletePermanently_BeforeDirectoryGuardFails_RefusesRecursiveDeletion()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smhard_attrs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var protectedFile = Path.Combine(dir, "keep.txt");
        File.WriteAllText(protectedFile, "must survive");

        try
        {
            var act = () => FileDeleter.DeletePermanently(
                dir,
                _ => throw new IOException("simulated guard failure"));

            act.Should().Throw<IOException>()
                .WithMessage("*simulated guard failure*");
            File.Exists(protectedFile).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DeletePermanently_ReadOnlyFilesInsideDirectory_DeletesAll()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smhard_ro_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var files = new[]
        {
            Path.Combine(dir, "a.txt"),
            Path.Combine(dir, "b.txt"),
            Path.Combine(sub.FullName, "c.txt"),
        };
        foreach (var f in files)
        {
            File.WriteAllText(f, "content");
            File.SetAttributes(f, FileAttributes.ReadOnly);
        }

        try
        {
            FileDeleter.DeletePermanently(dir);

            Directory.Exists(dir).Should().BeFalse("entire directory tree should be deleted");
        }
        finally
        {
            // Best-effort cleanup if the test assertion fails
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void DeletePermanently_FileAlreadyGone_DoesNotThrow()
    {
        // Never create the file — simulates a race where it vanished before our delete.
        var path = Path.Combine(Path.GetTempPath(), $"smhard_gone_{Guid.NewGuid():N}.txt");

        Action act = () => FileDeleter.DeletePermanently(path);

        act.Should().NotThrow("a missing file should be treated as already deleted");
    }

    [Fact]
    public void DeletePermanently_DirectoryVanishesDuringDelete_DoesNotThrow()
    {
        // Simulate a race where the OS cleans up the folder between snapshot and delete.
        var dir = Path.Combine(Path.GetTempPath(), $"smhard_vanish_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.Delete(dir, recursive: false); // already gone before our call

        Action act = () => FileDeleter.DeletePermanently(dir);

        act.Should().NotThrow("a vanished directory should be treated as already deleted");
    }

    // ── EstimateSize edge cases ───────────────────────────────────────────────

    [Fact]
    public void EstimateSize_NonexistentPath_ReturnsZero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smhard_noexist_{Guid.NewGuid():N}");

        FileDeleter.EstimateSize(path).Should().Be(0);
    }

    [Fact]
    public void EstimateSize_SingleFile_ReturnsExactLength()
    {
        var file = Path.Combine(Path.GetTempPath(), $"smhard_size_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(file, new byte[12_345]);
        try
        {
            FileDeleter.EstimateSize(file).Should().Be(12_345);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void EstimateSize_DirectoryWithFiles_ReturnsSumOfSizes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smhard_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a"), new byte[1_000]);
            File.WriteAllBytes(Path.Combine(dir, "b"), new byte[2_000]);
            File.WriteAllBytes(Path.Combine(dir, "c"), new byte[3_000]);

            FileDeleter.EstimateSize(dir).Should().Be(6_000);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EstimateSize_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => FileDeleter.EstimateSize(Path.GetTempPath(), cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    // ── Quarantine collision handling ─────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_QuarantineCollision_AppendsCounterSuffix()
    {
        var sourceFile = Path.Combine(Path.GetTempPath(), $"smhard_qsrc_{Guid.NewGuid():N}.txt");
        File.WriteAllText(sourceFile, "quarantine me");

        // Compute the expected quarantine destination using the same algorithm as FileDeleter.
        var quarantineRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster", "Quarantine");
        const long runId = 99_001;
        var relative = sourceFile
            .Replace(':', '_')
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedDest = Path.Combine(quarantineRoot, runId.ToString(), relative);

        // Pre-create a file at the expected destination to force a collision.
        Directory.CreateDirectory(Path.GetDirectoryName(expectedDest)!);
        File.WriteAllText(expectedDest, "collision blocker");

        try
        {
            var outcome = await _deleter.DeleteAsync(new DeletionRequest(
                sourceFile,
                DeletionMethod.Quarantine,
                DryRun: false,
                QuarantineRunId: runId));

            outcome.Success.Should().BeTrue(outcome.Error);
            outcome.QuarantinePath.Should().Be(expectedDest + ".1",
                "collision with primary destination should produce a .1 suffix");
            File.Exists(sourceFile).Should().BeFalse("source file should have been moved");
        }
        finally
        {
            if (File.Exists(sourceFile)) File.Delete(sourceFile);
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
            if (File.Exists(expectedDest + ".1")) File.Delete(expectedDest + ".1");
        }
    }

    [Fact]
    public async Task DeleteAsync_QuarantineCanonicalizesSourceAndKeepsDestinationContained()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"smhard_qcanon_{Guid.NewGuid():N}");
        var dotSegmentDirectory = Path.Combine(sourceRoot, "segment");
        Directory.CreateDirectory(dotSegmentDirectory);
        var canonicalSource = Path.Combine(sourceRoot, "payload.txt");
        File.WriteAllText(canonicalSource, "quarantine me");
        var requestedSource = Path.Combine(dotSegmentDirectory, "..", "payload.txt");
        var runId = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue;
        var quarantineRunRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster", "Quarantine", runId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        DeletionOutcome? outcome = null;

        try
        {
            outcome = await _deleter.DeleteAsync(new DeletionRequest(
                requestedSource,
                DeletionMethod.Quarantine,
                DryRun: false,
                QuarantineRunId: runId));

            outcome.Success.Should().BeTrue(outcome.Error);
            outcome.Path.Should().Be(requestedSource, "callers receive the path spelling they submitted");
            outcome.QuarantinePath.Should().NotBeNull();
            var destination = outcome.QuarantinePath!;
            destination.Should().Be(Path.GetFullPath(destination),
                "the persisted restore path must itself be canonical");
            destination.Should().StartWith(
                Path.TrimEndingDirectorySeparator(quarantineRunRoot) + Path.DirectorySeparatorChar,
                "the destination must remain inside its quarantine run directory");
            File.Exists(destination).Should().BeTrue();
            File.Exists(canonicalSource).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(canonicalSource)) File.Delete(canonicalSource);
            if (outcome?.QuarantinePath is { } destination && File.Exists(destination))
                File.Delete(destination);
            if (Directory.Exists(quarantineRunRoot))
                Directory.Delete(quarantineRunRoot, recursive: true);
            if (Directory.Exists(sourceRoot))
                Directory.Delete(sourceRoot, recursive: true);
        }
    }

    // ── TempFilesCleanupRule path boundary ────────────────────────────────────

    [Fact]
    public async Task TempFilesCleanupRule_FileInTempDirectory_IsIncluded()
    {
        var tempPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp");
        var fileInTemp = MakeFileEntry(Path.Combine(tempPath, "junk.dat"), sizeBytes: 100);
        var repo = MockRepoWith([fileInTemp]);
        var rule = new TempFilesCleanupRule(repo.Object);

        var suggestions = await CollectAsync(rule.AnalyzeAsync(1, new AppSettings()));

        suggestions.Should().ContainSingle()
            .Which.TargetPaths.Should().Contain(fileInTemp.FullPath);
    }

    [Fact]
    public async Task TempFilesCleanupRule_FileInSimilarlyNamedDirectory_IsExcluded()
    {
        // "C:\Windows\Temp" must NOT match "C:\Windows\Temporary Internet Files".
        // This was broken before the separator fix: StartsWith("C:\Windows\Temp") matched
        // "C:\Windows\Temporary..." because the prefix comparison had no boundary guard.
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var similarDir = Path.Combine(windowsFolder, "Temporary Internet Files");
        var fileInSimilar = MakeFileEntry(Path.Combine(similarDir, "secret.dat"), sizeBytes: 500);
        var repo = MockRepoWith([fileInSimilar]);
        var rule = new TempFilesCleanupRule(repo.Object);

        var suggestions = await CollectAsync(rule.AnalyzeAsync(1, new AppSettings()));

        suggestions.Should().BeEmpty(
            "a file in 'Windows\\Temporary Internet Files' must not match the 'Windows\\Temp' root");
    }

    // ── DownloadedInstallersRule path boundary ────────────────────────────────

    [Fact]
    public async Task DownloadedInstallersRule_InstallerInSimilarlyNamedSiblingDir_IsExcluded()
    {
        // "Downloads" must NOT match "Downloads Backup".
        // This was broken before the separator fix: StartsWith("C:\...\Downloads") matched
        // "C:\...\Downloads Backup\..." because there was no separator boundary check.
        const string downloads = @"C:\Users\TestUser\Downloads";
        const string downloadsBackup = @"C:\Users\TestUser\Downloads Backup";
        var installer = MakeFileEntry(
            Path.Combine(downloadsBackup, "setup.exe"), sizeBytes: 50_000_000);
        var repo = MockRepoWith([installer]);
        var rule = new DownloadedInstallersRule(repo.Object, () => downloads);

        var suggestions = await CollectAsync(rule.AnalyzeAsync(1, new AppSettings()));

        suggestions.Should().BeEmpty(
            "an installer in 'Downloads Backup' must not match the 'Downloads' root");
    }

    [Fact]
    public async Task DownloadedInstallersRule_ClearEntireDownloads_TargetsIndividualFilesNotFolder()
    {
        // TargetPaths must contain individual file paths, not the Downloads folder path itself,
        // so FileDeleter can report per-file success/failure and the folder is preserved.
        var downloadsDir = Path.Combine(Path.GetTempPath(), $"smhard_dl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloadsDir);
        try
        {
            var files = new[]
            {
                MakeFileEntry(Path.Combine(downloadsDir, "movie.mkv"), sizeBytes: 5_000_000_000L),
                MakeFileEntry(Path.Combine(downloadsDir, "docs", "report.pdf"), sizeBytes: 1_000_000L),
            };
            var repo = MockRepoWith(files);
            var settings = new AppSettings { ClearEntireDownloads = true };
            var rule = new DownloadedInstallersRule(repo.Object, () => downloadsDir);

            var suggestions = await CollectAsync(rule.AnalyzeAsync(1, settings));

            var clearSuggestion = suggestions.FirstOrDefault(s =>
                s.RuleId == "core.clear-downloads-folder");
            clearSuggestion.Should().NotBeNull("ClearEntireDownloads=true should produce a suggestion");

            var targetPaths = clearSuggestion!.TargetPaths;
            targetPaths.Should().NotContain(downloadsDir,
                "the Downloads folder itself must not be a target — only individual file paths");
            targetPaths.Should().Contain(files[0].FullPath);
            targetPaths.Should().Contain(files[1].FullPath);
        }
        finally
        {
            if (Directory.Exists(downloadsDir))
                Directory.Delete(downloadsDir, recursive: true);
        }
    }

    // ── Duplicate generic-cleanup isolation ──────────────────────────────────

    [Fact]
    public async Task DuplicateFilesCleanupRule_NeverReturnsGenericDeletionTargets()
    {
        var repo = new Mock<IDuplicateRepository>(MockBehavior.Strict);
        var rule = new DuplicateFilesCleanupRule(repo.Object);

        var suggestions = await CollectAsync(rule.AnalyzeAsync(1, new AppSettings()));

        suggestions.Should().BeEmpty(
            "duplicate deletion must stay in the keeper-validating, journaled workflow");
        repo.VerifyNoOtherCalls();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Mock<IScanRepository> MockRepoWith(IEnumerable<FileEntry> files)
    {
        var mock = new Mock<IScanRepository>();
        mock.Setup(r => r.GetLargestFilesAsync(
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(files.ToList());
        return mock;
    }

    private static FileEntry MakeFileEntry(string path, long sizeBytes) => new()
    {
        Id = 1,
        SessionId = 1,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = sizeBytes,
        CreatedUtc = DateTime.UtcNow.AddDays(-30),
        ModifiedUtc = DateTime.UtcNow.AddDays(-30),
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Unknown,
        Identity = new FileIdentity("TESTVOL", 1),
    };

    private static async Task<List<CleanupSuggestion>> CollectAsync(
        IAsyncEnumerable<CleanupSuggestion> source)
    {
        var list = new List<CleanupSuggestion>();
        await foreach (var s in source)
            list.Add(s);
        return list;
    }

    private static async Task<List<DeletionOutcome>> CollectOutcomesAsync(
        IAsyncEnumerable<DeletionOutcome> source)
    {
        var list = new List<DeletionOutcome>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
