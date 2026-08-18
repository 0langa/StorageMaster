using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class DownloadedInstallersRuleTests
{
    private const string FakeDownloads = @"C:\Users\TestUser\Downloads";

    private readonly Mock<IScanRepository> _repoMock = new();
    private readonly DownloadedInstallersRule _rule;
    private readonly AppSettings _settings = new();

    public DownloadedInstallersRuleTests()
    {
        _rule = new DownloadedInstallersRule(_repoMock.Object, () => FakeDownloads);
    }

    [Fact]
    public async Task AnalyzeAsync_ExeInDownloads_ReturnsSuggestion()
    {
        var installer = MakeFile($@"{FakeDownloads}\setup.exe", sizeBytes: 50_000_000L);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([installer]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().ContainSingle();
        suggestions[0].Category.Should().Be(CleanupCategory.DownloadedInstallers);
        suggestions[0].TargetPaths.Should().Contain(installer.FullPath);
        suggestions[0].ExpectedFileSnapshots[installer.FullPath].Identity.Should().Be(installer.Identity);
    }

    [Fact]
    public async Task AnalyzeAsync_MsiInDownloads_IncludedInSuggestion()
    {
        var exe = MakeFile($@"{FakeDownloads}\app.exe", sizeBytes: 20_000_000L);
        var msi = MakeFile($@"{FakeDownloads}\patch.msi", sizeBytes: 10_000_000L);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([exe, msi]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().ContainSingle();
        suggestions[0].TargetPaths.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnalyzeAsync_TextFileInDownloads_NotIncluded()
    {
        var txt = MakeFile($@"{FakeDownloads}\readme.txt", sizeBytes: 1_000_000L);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([txt]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("non-installer extensions are not suggested");
    }

    [Fact]
    public async Task AnalyzeAsync_InstallerOutsideDownloads_NotIncluded()
    {
        var installer = MakeFile(@"C:\Users\TestUser\Desktop\setup.exe", sizeBytes: 50_000_000L);
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([installer]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty("installer files outside Downloads are not suggested");
    }

    [Fact]
    public async Task AnalyzeAsync_NoFiles_ReturnsNoSuggestions()
    {
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_LegacyInstallerWithoutIdentity_RequiresRescan()
    {
        var installer = MakeFile($@"{FakeDownloads}\setup.exe", sizeBytes: 50_000_000L) with
        {
            Identity = null,
        };
        _repoMock
            .Setup(r => r.GetLargestFilesAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([installer]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in _rule.AnalyzeAsync(1, _settings))
            suggestions.Add(suggestion);

        suggestions.Should().BeEmpty("identity-less historical rows must be rescanned before deletion");
    }

    [Fact]
    public async Task AnalyzeAsync_ClearEntireDownloads_WithAnyLegacyRow_RequiresRescan()
    {
        var downloads = Path.Combine(Path.GetTempPath(), $"sm_downloads_{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloads);
        try
        {
            var trusted = MakeFile(Path.Combine(downloads, "trusted.txt"), 100);
            var legacy = MakeFile(Path.Combine(downloads, "legacy.txt"), 200) with { Identity = null };
            _repoMock
                .Setup(r => r.GetLargestFilesAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([trusted, legacy]);
            var rule = new DownloadedInstallersRule(_repoMock.Object, () => downloads);

            var suggestions = new List<CleanupSuggestion>();
            await foreach (var suggestion in rule.AnalyzeAsync(1, new AppSettings
            {
                ClearEntireDownloads = true,
            }))
            {
                suggestions.Add(suggestion);
            }

            suggestions.Should().NotContain(static suggestion =>
                suggestion.RuleId == "core.clear-downloads-folder");
        }
        finally
        {
            Directory.Delete(downloads);
        }
    }

    private static FileEntry MakeFile(string path, long sizeBytes) => new()
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
}
