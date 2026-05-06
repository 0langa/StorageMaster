using FluentAssertions;
using Moq;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Settings;

public sealed class FfmpegToolResolverTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sm_ffmpeg_{Guid.NewGuid():N}");
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public FfmpegToolResolverTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public void Resolve_BundledToolsFolder_PicksBundledExecutables()
    {
        var appBase = Path.Combine(_tempDir, "app");
        var toolsDir = Path.Combine(appBase, "tools", "ffmpeg");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "ffmpeg.exe"), string.Empty);
        File.WriteAllText(Path.Combine(toolsDir, "ffprobe.exe"), string.Empty);

        var resolved = FfmpegToolResolver.Resolve(
            configuredPath: string.Empty,
            appBaseDirectory: appBase,
            pathEnvironment: string.Empty);

        resolved.IsComplete.Should().BeTrue();
        resolved.Source.Should().Be("Bundled tools");
        resolved.FfmpegPath.Should().Be(Path.Combine(toolsDir, "ffmpeg.exe"));
    }

    [Fact]
    public void VideoPHashStrategy_IsAvailable_TracksLatestSettingsValue()
    {
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        var toolsDir = Path.Combine(_tempDir, "dynamic-tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "ffmpeg.exe"), string.Empty);
        File.WriteAllText(Path.Combine(toolsDir, "ffprobe.exe"), string.Empty);

        var configuredPath = string.Empty;
        var repo = new Mock<ISettingsRepository>();
        repo.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppSettings
            {
                FfmpegPath = configuredPath,
                DuplicateMaxVideoDurationSeconds = 1800,
            });

        var strategy = new VideoPHashStrategy(
            repo.Object,
            appBaseDirectory: Path.Combine(_tempDir, "empty-app"));

        strategy.IsAvailable.Should().BeFalse();

        configuredPath = toolsDir;

        strategy.IsAvailable.Should().BeTrue();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
