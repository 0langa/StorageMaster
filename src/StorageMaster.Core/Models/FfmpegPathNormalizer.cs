namespace StorageMaster.Core.Models;

public static class FfmpegPathNormalizer
{
    public static string Normalize(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return string.Empty;

        var raw = configuredPath.Trim().Trim('"');

        var looksLikeExe = string.Equals(
            Path.GetExtension(raw),
            ".exe",
            StringComparison.OrdinalIgnoreCase);

        string normalized = looksLikeExe
            ? raw
            : Path.Combine(raw, "ffmpeg.exe");

        try
        {
            return Path.GetFullPath(normalized);
        }
        catch
        {
            return normalized;
        }
    }
}
