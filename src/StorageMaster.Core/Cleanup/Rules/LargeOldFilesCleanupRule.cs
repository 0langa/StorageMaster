using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Identifies files that are both large (> LargeFileSizeMb) and old (> OldFileAgeDays).
/// Risk is Medium — these are user files that may be intentionally kept.
/// We yield one suggestion per file so the user can pick-and-choose in the UI.
/// </summary>
public sealed class LargeOldFilesCleanupRule : ICleanupRule
{
    private readonly IScanRepository _repo;

    public string RuleId => "core.large-old-files";
    public string DisplayName => "Large Old Files";
    public CleanupCategory Category => CleanupCategory.LargeOldFiles;

    // Rows read on the first pass, and the ceiling if that pass turns out to have been
    // cut short. Both bound work; neither bounds correctness on a normal session.
    internal const int InitialCandidateCount = 1_000;
    internal const int MaxCandidateCount = 100_000;

    // Ceiling on emitted suggestions. Every match becomes its own list entry, so this
    // is a UI bound — the previous behaviour capped rows *read* instead, which is what
    // made the under-reporting invisible.
    internal const int MaxSuggestions = 1_000;

    // Paths we refuse to suggest regardless of size/age — protect the user from accidents.
    private static readonly string[] ProtectedPrefixes =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
    ];

    public LargeOldFilesCleanupRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long thresholdBytes = (long)settings.LargeFileSizeMb * 1024 * 1024;
        var cutoff = DateTime.UtcNow.AddDays(-settings.OldFileAgeDays);

        // The age, identity and protected-prefix filters below run *after* this fetch,
        // so a fixed top-N silently hid large old files behind large recent ones: lower
        // the threshold on a media or VM drive and more than InitialCandidateCount files
        // clear it, and everything past the cut disappears with no indication.
        // The first fetch stays small because almost every session has far fewer files
        // above the threshold than that; only when the page comes back full and its
        // smallest row is still above the threshold can rows have been cut off, and
        // only then do we pay for the wider one.
        var candidates = await _repo.GetLargestFilesAsync(
            sessionId,
            InitialCandidateCount,
            cancellationToken);
        if (candidates.Count >= InitialCandidateCount &&
            candidates[candidates.Count - 1].SizeBytes >= thresholdBytes)
        {
            candidates = await _repo.GetLargestFilesAsync(
                sessionId,
                MaxCandidateCount,
                cancellationToken);
        }

        var surfaced = 0;
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.SizeBytes < thresholdBytes)
                break; // results are sorted descending by size

            if (file.ModifiedUtc > cutoff)
                continue;

            if (file.Identity is null)
                continue;

            if (IsProtected(file.FullPath))
                continue;

            yield return new CleanupSuggestion
            {
                Id = Guid.NewGuid(),
                RuleId = RuleId,
                Title = $"Large old file: {file.FileName}",
                Description = $"{FormatBytes(file.SizeBytes)}, last modified {file.ModifiedUtc:yyyy-MM-dd}. " +
                                 $"Path: {file.FullPath}",
                Category = Category,
                Risk = CleanupRisk.Medium,
                EstimatedBytes = file.SizeBytes,
                TargetPaths = [file.FullPath],
                ExpectedFileSnapshots = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    [file.FullPath] = new FileSnapshot(
                        file.FullPath,
                        file.Identity,
                        file.SizeBytes,
                        file.ModifiedUtc,
                        file.Attributes),
                },
                IsSystemPath = false,
            };

            // One suggestion per file: cap what is surfaced, not what is examined.
            if (++surfaced >= MaxSuggestions)
                yield break;
        }
    }

    private static bool IsProtected(string path) =>
        ProtectedPrefixes.Any(p =>
            !string.IsNullOrEmpty(p) &&
            path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string FormatBytes(long bytes) => ByteFormat.Format(bytes);
}
