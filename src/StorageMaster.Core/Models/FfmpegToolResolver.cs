namespace StorageMaster.Core.Models;

public sealed record FfmpegToolPaths(
    string FfmpegPath,
    string FfprobePath,
    string Source)
{
    public bool HasFfmpeg =>
        !string.IsNullOrWhiteSpace(FfmpegPath) &&
        File.Exists(FfmpegPath);

    public bool HasFfprobe =>
        !string.IsNullOrWhiteSpace(FfprobePath) &&
        File.Exists(FfprobePath);

    public bool IsComplete => HasFfmpeg && HasFfprobe;
}

public static class FfmpegToolResolver
{
    public static FfmpegToolPaths Resolve(
        string? configuredPath,
        string? appBaseDirectory = null,
        string? pathEnvironment = null)
    {
        foreach (var candidate in GetCandidates(configuredPath, appBaseDirectory, pathEnvironment))
        {
            var normalized = FfmpegPathNormalizer.Normalize(candidate.Path);
            if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(normalized))
                continue;

            var ffprobePath = Path.Combine(
                Path.GetDirectoryName(normalized) ?? string.Empty,
                "ffprobe.exe");

            return new FfmpegToolPaths(normalized, ffprobePath, candidate.Source);
        }

        return new FfmpegToolPaths(string.Empty, string.Empty, "Not found");
    }

    private static IEnumerable<(string Path, string Source)> GetCandidates(
        string? configuredPath,
        string? appBaseDirectory,
        string? pathEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            yield return (configuredPath, "Settings");

        if (!string.IsNullOrWhiteSpace(appBaseDirectory))
        {
            yield return (Path.Combine(appBaseDirectory, "tools", "ffmpeg"), "Bundled tools");
            yield return (Path.Combine(appBaseDirectory, "ffmpeg", "bin"), "Bundled ffmpeg/bin");
            yield return (Path.Combine(appBaseDirectory, "ffmpeg"), "Bundled ffmpeg");
            yield return (Path.Combine(appBaseDirectory, "ffmpeg.exe"), "App folder");
        }

        var pathValue = pathEnvironment ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            yield break;

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return (segment, "PATH");
    }
}
