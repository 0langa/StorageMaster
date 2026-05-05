using FluentAssertions;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Settings;

public sealed class FfmpegPathNormalizerTests
{
    [Fact]
    public void Normalize_DirectoryPath_AppendsFfmpegExe()
    {
        var input = @"C:\tools\ffmpeg\bin";

        var normalized = FfmpegPathNormalizer.Normalize(input);

        normalized.Should().Be(Path.GetFullPath(@"C:\tools\ffmpeg\bin\ffmpeg.exe"));
    }

    [Fact]
    public void Normalize_ExePath_PreservesExecutableTarget()
    {
        var input = @"""C:\tools\ffmpeg\bin\ffmpeg.exe""";

        var normalized = FfmpegPathNormalizer.Normalize(input);

        normalized.Should().Be(Path.GetFullPath(@"C:\tools\ffmpeg\bin\ffmpeg.exe"));
    }
}
