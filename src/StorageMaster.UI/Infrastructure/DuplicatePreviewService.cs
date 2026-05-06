using System.Diagnostics;
using System.Text;
using SixLabors.ImageSharp;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Infrastructure;

public sealed class DuplicatePreviewService(
    ISettingsRepository settingsRepository) : IDuplicatePreviewService
{
    public async Task<DuplicatePreviewResult> BuildPreviewAsync(
        DuplicateMethod method,
        IReadOnlyList<DuplicateGroupMember> members,
        CancellationToken ct = default)
    {
        if (members.Count == 0)
            return new DuplicatePreviewResult();

        return method switch
        {
            DuplicateMethod.ImagePHash => await BuildImagePreviewAsync(members, ct),
            DuplicateMethod.VideoPHash => await BuildVideoPreviewAsync(members, ct),
            DuplicateMethod.NormalizedText => await BuildTextPreviewAsync(members, ct),
            _ => BuildExactPreview(members),
        };
    }

    private static DuplicatePreviewResult BuildExactPreview(IReadOnlyList<DuplicateGroupMember> members) =>
        new()
        {
            Summary = "Exact-content duplicates. Review file names and paths before deleting.",
            Items = members.Select(static member => new DuplicatePreviewItem
            {
                Path = member.FullPath,
                Title = member.FileName,
                Subtitle = $"{member.SizeBytes:N0} bytes",
                PreviewPath = member.FullPath,
            }).ToList(),
        };

    private static async Task<DuplicatePreviewResult> BuildImagePreviewAsync(
        IReadOnlyList<DuplicateGroupMember> members,
        CancellationToken ct)
    {
        var items = new List<DuplicatePreviewItem>(members.Count);
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            var subtitle = $"{member.SizeBytes:N0} bytes";
            try
            {
                var info = await Image.IdentifyAsync(member.FullPath, ct);
                if (info is not null)
                    subtitle = $"{info.Width}x{info.Height} • {member.SizeBytes:N0} bytes";
            }
            catch
            {
                // Best-effort metadata only.
            }

            items.Add(new DuplicatePreviewItem
            {
                Path = member.FullPath,
                Title = member.FileName,
                Subtitle = subtitle,
                PreviewPath = member.FullPath,
            });
        }

        return new DuplicatePreviewResult
        {
            Summary = "Perceptual image match. Compare thumbnail, dimensions, and path before selecting duplicates.",
            Items = items,
        };
    }

    private async Task<DuplicatePreviewResult> BuildVideoPreviewAsync(
        IReadOnlyList<DuplicateGroupMember> members,
        CancellationToken ct)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        var tools = FfmpegToolResolver.Resolve(settings.FfmpegPath, AppContext.BaseDirectory);
        var items = new List<DuplicatePreviewItem>(members.Count);

        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            var previewPath = string.Empty;
            var subtitle = $"{member.SizeBytes:N0} bytes";

            if (tools.HasFfmpeg)
            {
                previewPath = await GenerateVideoPreviewAsync(tools.FfmpegPath, member.FullPath, ct);
                if (!string.IsNullOrWhiteSpace(previewPath))
                    subtitle = $"Keyframe preview • {member.SizeBytes:N0} bytes";
            }

            items.Add(new DuplicatePreviewItem
            {
                Path = member.FullPath,
                Title = member.FileName,
                Subtitle = subtitle,
                PreviewPath = previewPath,
            });
        }

        return new DuplicatePreviewResult
        {
            Summary = tools.HasFfmpeg
                ? "Perceptual video match. Review sampled keyframes and metadata before deleting."
                : "Perceptual video match. FFmpeg preview unavailable on this device.",
            Items = items,
        };
    }

    private static async Task<DuplicatePreviewResult> BuildTextPreviewAsync(
        IReadOnlyList<DuplicateGroupMember> members,
        CancellationToken ct)
    {
        var first = members[0];
        var second = members.Count > 1 ? members[1] : members[0];

        var firstLines = await ReadPreviewLinesAsync(first.FullPath, ct);
        var secondLines = await ReadPreviewLinesAsync(second.FullPath, ct);
        var mismatch = FindFirstDifference(firstLines, secondLines);
        var summary = mismatch is null
            ? "Normalized-equivalent text. Differences are limited to whitespace, line endings, or normalization."
            : $"Normalized-equivalent text. First visible difference: line {mismatch.Value.LineNumber}.";

        return new DuplicatePreviewResult
        {
            Summary = summary,
            Items =
            [
                new DuplicatePreviewItem
                {
                    Path = first.FullPath,
                    Title = first.FileName,
                    Subtitle = mismatch is null ? "No visible line differences in preview window." : $"Line {mismatch.Value.LineNumber}: {mismatch.Value.Left}",
                    PreviewPath = string.Empty,
                },
                new DuplicatePreviewItem
                {
                    Path = second.FullPath,
                    Title = second.FileName,
                    Subtitle = mismatch is null ? "No visible line differences in preview window." : $"Line {mismatch.Value.LineNumber}: {mismatch.Value.Right}",
                    PreviewPath = string.Empty,
                }
            ],
        };
    }

    private static async Task<IReadOnlyList<string>> ReadPreviewLinesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>(32);
        while (!reader.EndOfStream && lines.Count < 32)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct) ?? string.Empty;
            lines.Add(line.TrimEnd());
        }

        return lines;
    }

    private static (int LineNumber, string Left, string Right)? FindFirstDifference(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var max = Math.Max(left.Count, right.Count);
        for (var i = 0; i < max; i++)
        {
            var leftLine = i < left.Count ? left[i] : string.Empty;
            var rightLine = i < right.Count ? right[i] : string.Empty;
            if (!string.Equals(leftLine, rightLine, StringComparison.Ordinal))
                return (i + 1, leftLine, rightLine);
        }

        return null;
    }

    private static async Task<string> GenerateVideoPreviewAsync(string ffmpegPath, string sourcePath, CancellationToken ct)
    {
        var previewDirectory = Path.Combine(
            Path.GetTempPath(),
            "StorageMaster",
            "VideoPreviews");
        Directory.CreateDirectory(previewDirectory);

        var outputPath = Path.Combine(previewDirectory, $"{Path.GetFileNameWithoutExtension(sourcePath)}-{Math.Abs(sourcePath.GetHashCode())}.jpg");
        if (File.Exists(outputPath))
            return outputPath;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add("00:00:03");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add("scale=320:-1");
        psi.ArgumentList.Add(outputPath);
        using var process = new Process { StartInfo = psi };

        process.Start();
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 && File.Exists(outputPath)
            ? outputPath
            : string.Empty;
    }
}
