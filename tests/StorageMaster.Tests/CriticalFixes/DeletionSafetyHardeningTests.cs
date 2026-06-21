using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

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
    public void IsRootOrUncPrefix_DriveRoot_ReturnsTrue(string path)
        => FileDeleter.IsRootOrUncPrefix(path).Should().BeTrue($"'{path}' is a drive root");

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\SERVER\SHARE")]   // UNC, case-insensitive
    [InlineData(@"\\nas\backup")]
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

    [Fact]
    public async Task DeleteManyAsync_AllInvalidRecycleBinRoots_AllAreRefused()
    {
        var requests = new[]
        {
            new DeletionRequest(@"C:\", DeletionMethod.RecycleBin, DryRun: false),
            new DeletionRequest(@"\\server\share", DeletionMethod.RecycleBin, DryRun: false),
        };

        var outcomes = await CollectOutcomesAsync(_deleter.DeleteManyAsync(requests));

        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => !o.Success);
        outcomes.Should().OnlyContain(o => o.Error!.Contains("Refusing to delete a filesystem root"));
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
    public void DeletePermanently_AttributeProbeFails_RefusesRecursiveDeletion()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smhard_attrs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var protectedFile = Path.Combine(dir, "keep.txt");
        File.WriteAllText(protectedFile, "must survive");

        try
        {
            var act = () => FileDeleter.DeletePermanently(
                dir,
                _ => throw new UnauthorizedAccessException("simulated attribute failure"));

            act.Should().Throw<IOException>()
                .WithMessage("*verify whether the path is a reparse point*");
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

    // ── TempFilesCleanupRule path boundary ────────────────────────────────────

    [Fact]
    public async Task TempFilesCleanupRule_FileInTempDirectory_IsIncluded()
    {
        var tempPath = Path.GetTempPath().TrimEnd('\\', '/');
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

    // ── DuplicateFilesCleanupRule keeper-gone safety ──────────────────────────

    [Fact]
    public async Task DuplicateFilesCleanupRule_KeeperPathDoesNotExistOnDisk_GroupIsSkipped()
    {
        // If the keeper file was removed between scan time and cleanup analysis,
        // deleting the "duplicates" would cause total data loss for this group.
        // The rule must skip the group entirely.
        var nonexistentKeeper = Path.Combine(
            Path.GetTempPath(), $"smhard_keeper_{Guid.NewGuid():N}", "keep.txt");
        // Do NOT create this file — it must not exist on disk.

        var repo = BuildDuplicateRepo(keeperPath: nonexistentKeeper);
        var rule = new DuplicateFilesCleanupRule(repo.Object);

        var suggestions = await CollectAsync(rule.AnalyzeAsync(1, new AppSettings()));

        suggestions.Should().BeEmpty(
            "group must be skipped when keeper file does not exist on disk");
    }

    [Fact]
    public async Task DuplicateFilesCleanupRule_KeeperExists_GroupIsIncluded()
    {
        // Happy path: keeper exists on disk → suggestion is produced.
        var keeperFile = Path.Combine(Path.GetTempPath(), $"smhard_keeper_{Guid.NewGuid():N}.txt");
        File.WriteAllText(keeperFile, "I am the keeper");
        try
        {
            var repo = BuildDuplicateRepo(keeperPath: keeperFile);
            var rule = new DuplicateFilesCleanupRule(repo.Object);

            var suggestions = await CollectAsync(rule.AnalyzeAsync(1, new AppSettings()));

            suggestions.Should().ContainSingle(
                "when keeper exists and duplicates are selected, a suggestion is produced");
            suggestions[0].TargetPaths.Should().Contain(@"C:\docs\copy-a.txt");
        }
        finally
        {
            if (File.Exists(keeperFile)) File.Delete(keeperFile);
        }
    }

    [Fact]
    public async Task DuplicateFilesCleanupRule_NoKeeperDesignated_GroupIsSkipped()
    {
        // A group with no keeper member is also unsafe — skip it.
        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(r => r.GetRunsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeCompletedRun(runId: 1)]);
        repo.Setup(r => r.GetGroupsForRunAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeGroup(groupId: 10, runId: 1)]);
        repo.Setup(r => r.GetMembersForGroupAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeMember(id: 1, groupId: 10, path: @"C:\docs\file-a.txt",
                    isKeeper: false, isSelected: true),
                MakeMember(id: 2, groupId: 10, path: @"C:\docs\file-b.txt",
                    isKeeper: false, isSelected: true),
            ]);

        var rule = new DuplicateFilesCleanupRule(repo.Object);
        var suggestions = await CollectAsync(rule.AnalyzeAsync(1, new AppSettings()));

        suggestions.Should().BeEmpty("no keeper = cannot safely determine what to preserve");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Mock<IDuplicateRepository> BuildDuplicateRepo(string keeperPath)
    {
        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(r => r.GetRunsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeCompletedRun(runId: 1)]);
        repo.Setup(r => r.GetGroupsForRunAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeGroup(groupId: 10, runId: 1)]);
        repo.Setup(r => r.GetMembersForGroupAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeMember(1, 10, keeperPath, isKeeper: true, isSelected: false),
                MakeMember(2, 10, @"C:\docs\copy-a.txt", isKeeper: false, isSelected: true),
            ]);
        return repo;
    }

    private static DuplicateRun MakeCompletedRun(long runId) => new()
    {
        Id = runId,
        SessionId = 1,
        StartedUtc = DateTime.UtcNow.AddMinutes(-5),
        CompletedUtc = DateTime.UtcNow.AddMinutes(-4),
        Status = DuplicateRunStatus.Completed,
        ConfigJson = "{}",
        GroupCount = 1,
    };

    private static DuplicateGroup MakeGroup(long groupId, long runId) => new()
    {
        Id = groupId,
        RunId = runId,
        Method = DuplicateMethod.ExactSha256,
        Algorithm = "SHA256",
        Confidence = 1.0,
        TotalBytes = 600,
        ReclaimableBytes = 300,
        RepresentativeFileEntryId = 1,
    };

    private static DuplicateGroupMember MakeMember(
        long id, long groupId, string path, bool isKeeper, bool isSelected) => new()
        {
            Id = id,
            GroupId = groupId,
            FileEntryId = id,
            FullPath = path,
            FileName = Path.GetFileName(path),
            SizeBytes = 300,
            ModifiedUtc = DateTime.UtcNow,
            Score = 1.0,
            IsKeeper = isKeeper,
            IsSelected = isSelected,
            RecommendationReason = isKeeper ? "Kept" : "Duplicate",
            ExistsNow = true,
        };

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
