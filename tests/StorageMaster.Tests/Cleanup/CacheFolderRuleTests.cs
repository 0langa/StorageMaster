using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class CacheFolderRuleTests
{
    private readonly Mock<IScanRepository> _repoMock = new();
    private readonly CacheFolderCleanupRule _rule;
    private readonly AppSettings _settings = new();
    private readonly string _localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public CacheFolderRuleTests()
    {
        _rule = new CacheFolderCleanupRule(_repoMock.Object);
    }

    [Fact]
    public async Task AnalyzeAsync_KnownCacheWithSize_ReturnsSuggestion()
    {
        var inetCachePath = Path.Combine(_localAppData, @"Microsoft\Windows\INetCache");
        var folder = MakeFolder(inetCachePath, totalBytes: 500_000_000L);

        _repoMock
            .Setup(r => r.GetLargestFoldersAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([folder]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().ContainSingle();
        suggestions[0].Category.Should().Be(CleanupCategory.CacheFolders);
        suggestions[0].Risk.Should().Be(CleanupRisk.Safe);
        suggestions[0].TargetPaths.Should().Contain(inetCachePath);
    }

    [Fact]
    public async Task AnalyzeAsync_KnownCacheWithZeroSize_NoSuggestion()
    {
        var inetCachePath = Path.Combine(_localAppData, @"Microsoft\Windows\INetCache");
        var folder = MakeFolder(inetCachePath, totalBytes: 0);

        _repoMock
            .Setup(r => r.GetLargestFoldersAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([folder]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("zero-size cache folders should not be suggested");
    }

    [Fact]
    public async Task AnalyzeAsync_UnknownFolder_NoSuggestion()
    {
        var randomFolder = MakeFolder(@"C:\SomeRandomApp\cache", totalBytes: 100_000_000L);

        _repoMock
            .Setup(r => r.GetLargestFoldersAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([randomFolder]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("unknown paths are not suggested");
    }

    [Fact]
    public async Task AnalyzeAsync_MultipleKnownCaches_OneSuggestionEach()
    {
        var inetCachePath = Path.Combine(_localAppData, @"Microsoft\Windows\INetCache");
        var npmCachePath = Path.Combine(_localAppData, "npm-cache");

        _repoMock
            .Setup(r => r.GetLargestFoldersAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeFolder(inetCachePath, totalBytes: 200_000_000L),
                MakeFolder(npmCachePath,  totalBytes: 150_000_000L),
            ]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().HaveCount(2, "one suggestion per matched known cache folder");
    }

    [Fact]
    public async Task AnalyzeAsync_ChromiumDefaultProfileCache_IsLeftToTheBrowserCacheRule()
    {
        _repoMock
            .Setup(r => r.GetLargestFoldersAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeFolder(
                    Path.Combine(_localAppData, @"Google\Chrome\User Data\Default\Cache"),
                    totalBytes: 200_000_000L),
                MakeFolder(
                    Path.Combine(_localAppData, @"Microsoft\Edge\User Data\Default\Cache"),
                    totalBytes: 150_000_000L),
            ]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty(
            "BrowserCacheCleanupRule already covers every Chrome/Edge profile cache; " +
            "emitting a second suggestion for the same directory double-counted the savings estimate");
    }

    private static FolderEntry MakeFolder(string path, long totalBytes) => new()
    {
        Id = 1,
        SessionId = 1,
        FullPath = path,
        FolderName = Path.GetFileName(path) ?? path,
        DirectSizeBytes = totalBytes,
        TotalSizeBytes = totalBytes,
        FileCount = 100,
        SubFolderCount = 2,
        IsReparsePoint = false,
        WasAccessDenied = false,
    };
}
