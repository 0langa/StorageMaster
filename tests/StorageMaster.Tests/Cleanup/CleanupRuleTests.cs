using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class LargeOldFilesRuleTests
{
    private readonly Mock<IScanRepository> _repoMock = new();
    private readonly LargeOldFilesCleanupRule _rule;
    private readonly AppSettings _settings = new() { LargeFileSizeMb = 100, OldFileAgeDays = 30 };

    public LargeOldFilesRuleTests()
    {
        _rule = new LargeOldFilesCleanupRule(_repoMock.Object);
    }

    [Fact]
    public async Task AnalyseAsync_ReturnsLargeOldFiles()
    {
        var oldLargeFile = MakeFile(@"C:\Users\user\Downloads\big.iso",
            sizeBytes: 200 * 1024 * 1024L,  // 200 MB
            modifiedDaysAgo: 60);

        var recentFile = MakeFile(@"C:\Users\user\Downloads\recent.zip",
            sizeBytes: 200 * 1024 * 1024L,
            modifiedDaysAgo: 5);

        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([oldLargeFile, recentFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().HaveCount(1);
        suggestions[0].TargetPaths.Should().Contain(oldLargeFile.FullPath);
        suggestions[0].Risk.Should().Be(CleanupRisk.Medium);
        suggestions[0].ExpectedFileSnapshots[oldLargeFile.FullPath].Identity.Should().Be(oldLargeFile.Identity);
    }

    [Fact]
    public async Task AnalyseAsync_SkipsProtectedWindowsPaths()
    {
        var systemFile = MakeFile(@"C:\Windows\System32\big.sys",
            sizeBytes: 500 * 1024 * 1024L,
            modifiedDaysAgo: 400);

        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([systemFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("system paths must never be suggested for deletion");
    }

    [Fact]
    public async Task AnalyseAsync_SkipsSmallFiles()
    {
        var smallFile = MakeFile(@"C:\Users\user\old-small.txt",
            sizeBytes: 1024,  // 1 KB — well below threshold
            modifiedDaysAgo: 400);

        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([smallFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("files below the size threshold must not be suggested");
    }

    [Fact]
    public async Task AnalyseAsync_ExactlyAtThreshold_IsIncluded()
    {
        // File is exactly at the size threshold and exactly at the age threshold.
        var thresholdFile = MakeFile(
            @"C:\Users\user\Documents\exact.iso",
            sizeBytes: 100 * 1024 * 1024L,  // exactly 100 MB
            modifiedDaysAgo: 30);            // exactly 30 days

        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([thresholdFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().ContainSingle("a file at exactly the threshold should be included");
    }

    [Fact]
    public async Task AnalyseAsync_MultipleEligibleFiles_EachGetsSeparateSuggestion()
    {
        var files = Enumerable.Range(0, 5)
            .Select(i => MakeFile(
                $@"C:\Users\user\Downloads\big{i}.iso",
                sizeBytes: 200 * 1024 * 1024L,
                modifiedDaysAgo: 60))
            .ToList();

        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().HaveCount(5, "each eligible file gets its own suggestion");
    }

    [Fact]
    public async Task AnalyseAsync_FirstPageFullOfRecentFiles_StillFindsOlderMatchesBehindTheCut()
    {
        // Age, identity and protected-prefix filtering all happen after the top-N cut,
        // so a first page made entirely of large *recent* files used to hide every
        // large old file sitting behind it.
        var recent = Enumerable.Range(0, LargeOldFilesCleanupRule.InitialCandidateCount)
            .Select(i => MakeFile(
                $@"C:\media\recent{i}.mkv",
                sizeBytes: 300 * 1024 * 1024L,
                modifiedDaysAgo: 1))
            .ToList();
        var oldFile = MakeFile(
            @"C:\media\archive.mkv",
            sizeBytes: 200 * 1024 * 1024L,
            modifiedDaysAgo: 400);

        _repoMock
            .Setup(r => r.GetLargestFilesAsync(
                1,
                LargeOldFilesCleanupRule.InitialCandidateCount,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(recent);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(
                1,
                LargeOldFilesCleanupRule.MaxCandidateCount,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(recent.Append(oldFile).ToList());

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().ContainSingle()
            .Which.TargetPaths.Should().ContainSingle()
            .Which.Should().Be(oldFile.FullPath);
    }

    [Fact]
    public async Task AnalyseAsync_FirstPageEndsBelowThreshold_DoesNotWidenTheQuery()
    {
        var files = Enumerable.Range(0, LargeOldFilesCleanupRule.InitialCandidateCount)
            .Select(i => MakeFile(
                $@"C:\media\small{i}.bin",
                sizeBytes: 1024L,
                modifiedDaysAgo: 400))
            .ToList();

        _repoMock
            .Setup(r => r.GetLargestFilesAsync(
                1,
                LargeOldFilesCleanupRule.InitialCandidateCount,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty();
        _repoMock.Verify(
            r => r.GetLargestFilesAsync(
                1,
                LargeOldFilesCleanupRule.MaxCandidateCount,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "the cheap first page already proves nothing was cut off");
    }

    [Fact]
    public async Task AnalyseAsync_LegacyFileWithoutIdentity_RequiresRescan()
    {
        var legacy = MakeFile(
            @"C:\Users\user\Downloads\legacy.iso",
            sizeBytes: 200 * 1024 * 1024L,
            modifiedDaysAgo: 60) with
        {
            Identity = null,
        };
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([legacy]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(suggestion);

        suggestions.Should().BeEmpty("identity-less historical rows must be rescanned before deletion");
    }

    private static FileEntry MakeFile(string path, long sizeBytes, int modifiedDaysAgo) => new()
    {
        Id = 1,
        SessionId = 1,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = sizeBytes,
        CreatedUtc = DateTime.UtcNow.AddDays(-modifiedDaysAgo - 1),
        ModifiedUtc = DateTime.UtcNow.AddDays(-modifiedDaysAgo),
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Unknown,
        Identity = new FileIdentity("TESTVOL", 1),
    };
}

public sealed class TempFilesRuleTests
{
    private readonly Mock<IScanRepository> _repoMock = new();
    private readonly TempFilesCleanupRule _rule;

    public TempFilesRuleTests()
    {
        _rule = new TempFilesCleanupRule(_repoMock.Object);
    }

    [Fact]
    public async Task AnalyseAsync_DoesNotSuggestTemporaryExtensionOutsideTempRoots()
    {
        var tmpFile = MakeFile(@"C:\SomeApp\leftover.tmp", sizeBytes: 50_000);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 50_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tmpFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("an extension alone is not proof that a file is safe to delete");
    }

    [Fact]
    public async Task AnalyseAsync_RedirectedProcessTempOutsideCanonicalRoots_IsIgnored()
    {
        var redirectedTempFile = MakeFile(
            @"C:\Users\user\RedirectedTemp\session.tmp",
            sizeBytes: 50_000);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 50_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([redirectedTempFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(suggestion);

        suggestions.Should().BeEmpty(
            "process TEMP/TMP can be redirected by the user and is not a trusted cleanup root");
    }

    [Fact]
    public async Task AnalyseAsync_SuggestsAnyFileInsideCanonicalTempRoot()
    {
        var tempFile = MakeFile(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "StorageMasterTests",
                "cache.bin"),
            sizeBytes: 50_000);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 50_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tempFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(suggestion);

        suggestions.Should().ContainSingle();
        suggestions[0].TargetPaths.Should().ContainSingle().Which.Should().Be(tempFile.FullPath);
        suggestions[0].Category.Should().Be(CleanupCategory.TempFiles);
        suggestions[0].ExpectedFileSnapshots[tempFile.FullPath].Identity.Should().Be(tempFile.Identity);
    }

    [Fact]
    public async Task AnalyseAsync_DoesNotSuggestTraversalAliasEscapingTempRoot()
    {
        var escapedFile = MakeFile(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "..",
                "valuable.tmp"),
            sizeBytes: 50_000);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 50_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([escapedFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(suggestion);

        suggestions.Should().BeEmpty("canonical path containment must be checked before suggesting deletion");
    }

    [Fact]
    public async Task AnalyseAsync_LegacyTempFileWithoutIdentity_RequiresRescan()
    {
        var legacy = MakeFile(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "StorageMasterTests",
                "legacy.tmp"),
            sizeBytes: 50_000) with
        {
            Identity = null,
        };
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 50_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([legacy]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(suggestion);

        suggestions.Should().BeEmpty("identity-less historical rows must be rescanned before deletion");
    }

    private static FileEntry MakeFile(string path, long sizeBytes) => new()
    {
        Id = 1,
        SessionId = 1,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = sizeBytes,
        CreatedUtc = DateTime.UtcNow,
        ModifiedUtc = DateTime.UtcNow,
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Temporary,
        Identity = new FileIdentity("TESTVOL", 1),
    };
}

public sealed class AppSettingsCleanupDefaultsTests
{
    [Fact]
    public void CleanProgramLeftovers_IsDisabledByDefault()
    {
        new AppSettings().CleanProgramLeftovers.Should().BeFalse();
    }
}

public sealed class UninstalledProgramLeftoversSafetyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"sm_leftovers_{Guid.NewGuid():N}");

    [Fact]
    public async Task AnalyzeAsync_EmptyInstalledProgramInventory_FailsClosed()
    {
        var provider = new Mock<IInstalledProgramProvider>();
        provider.Setup(static item => item.GetInstalledPrograms())
            .Returns([]);
        var rule = new UninstalledProgramLeftoversRule(provider.Object);
        var suggestions = new List<CleanupSuggestion>();

        await foreach (var suggestion in rule.AnalyzeAsync(1, new AppSettings
        {
            CleanProgramLeftovers = true,
        }))
        {
            suggestions.Add(suggestion);
        }

        suggestions.Should().BeEmpty(
            "an empty inventory may mean registry discovery failed, not that every app is uninstalled");
    }

    [Fact]
    public void TryInspectDirectoryTree_RecentDescendant_PreventsCandidate()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_tempDir, "nested"));
        var recentFile = Path.Combine(nested.FullName, "active.dat");
        File.WriteAllText(recentFile, "active");

        var inspected = UninstalledProgramLeftoversRule.TryInspectDirectoryTree(
            _tempDir,
            DateTime.UtcNow.AddDays(-90),
            CancellationToken.None,
            out _,
            out var hasRecentDescendant);

        inspected.Should().BeTrue();
        hasRecentDescendant.Should().BeTrue(
            "top-level directory timestamps do not prove descendants are inactive");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

public sealed class RecycleBinRuleTests
{
    private readonly Mock<IRecycleBinInfoProvider> _providerMock = new();
    private readonly RecycleBinCleanupRule _rule;

    public RecycleBinRuleTests()
    {
        _rule = new RecycleBinCleanupRule(_providerMock.Object);
    }

    [Fact]
    public async Task AnalyzeAsync_BinHasItems_ReturnsSuggestion()
    {
        _providerMock
            .Setup(p => p.GetRecycleBinInfo())
            .Returns(new RecycleBinInfo(SizeBytes: 500_000_000L, ItemCount: 42));

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(s);

        suggestions.Should().ContainSingle();
        suggestions[0].Category.Should().Be(CleanupCategory.RecycleBin);
        suggestions[0].Risk.Should().Be(CleanupRisk.Medium);
        suggestions[0].EstimatedBytes.Should().Be(500_000_000L);
        suggestions[0].SupportsPermanentDelete.Should().BeTrue();
        suggestions[0].SupportsRecycleBin.Should().BeFalse();
        suggestions[0].SupportsQuarantine.Should().BeFalse();
        suggestions[0].SafetyNotes.Should().Contain("cannot be undone");
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyBin_NoSuggestion()
    {
        _providerMock
            .Setup(p => p.GetRecycleBinInfo())
            .Returns(new RecycleBinInfo(SizeBytes: 0, ItemCount: 0));

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("empty recycle bin should not be suggested");
    }

    [Fact]
    public async Task AnalyzeAsync_TargetPathIsSentinel()
    {
        _providerMock
            .Setup(p => p.GetRecycleBinInfo())
            .Returns(new RecycleBinInfo(SizeBytes: 1_000_000L, ItemCount: 5));

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(s);

        suggestions[0].TargetPaths.Should().Contain("::RecycleBin::");
    }
}
