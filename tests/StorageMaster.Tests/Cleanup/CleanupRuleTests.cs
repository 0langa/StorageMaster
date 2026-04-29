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

    private static FileEntry MakeFile(string path, long sizeBytes, int modifiedDaysAgo) => new()
    {
        Id           = 1,
        SessionId    = 1,
        FullPath     = path,
        FileName     = Path.GetFileName(path),
        Extension    = Path.GetExtension(path),
        SizeBytes    = sizeBytes,
        CreatedUtc   = DateTime.UtcNow.AddDays(-modifiedDaysAgo - 1),
        ModifiedUtc  = DateTime.UtcNow.AddDays(-modifiedDaysAgo),
        AccessedUtc  = DateTime.UtcNow,
        Attributes   = FileAttributes.Normal,
        Category     = FileTypeCategory.Unknown,
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
    public async Task AnalyseAsync_DetectsTmpExtensions()
    {
        var tmpFile = MakeFile(@"C:\SomeApp\leftover.tmp", sizeBytes: 50_000);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, 50_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tmpFile]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, new AppSettings()))
            suggestions.Add(s);

        suggestions.Should().ContainSingle();
        suggestions[0].Category.Should().Be(CleanupCategory.TempFiles);
    }

    private static FileEntry MakeFile(string path, long sizeBytes) => new()
    {
        Id           = 1,
        SessionId    = 1,
        FullPath     = path,
        FileName     = Path.GetFileName(path),
        Extension    = Path.GetExtension(path),
        SizeBytes    = sizeBytes,
        CreatedUtc   = DateTime.UtcNow,
        ModifiedUtc  = DateTime.UtcNow,
        AccessedUtc  = DateTime.UtcNow,
        Attributes   = FileAttributes.Normal,
        Category     = FileTypeCategory.Temporary,
    };
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
        suggestions[0].Risk.Should().Be(CleanupRisk.Safe);
        suggestions[0].EstimatedBytes.Should().Be(500_000_000L);
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
