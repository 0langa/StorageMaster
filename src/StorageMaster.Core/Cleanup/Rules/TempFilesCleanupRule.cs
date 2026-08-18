using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Suggests deleting files in canonical Windows temp folders.
/// Risk is Low — temp files should not be referenced by running processes,
/// but we cannot guarantee this without kernel-level handle checks (v2 concern).
/// </summary>
public sealed class TempFilesCleanupRule : ICleanupRule
{
    private readonly IScanRepository _repo;

    public string RuleId => "core.temp-files";
    public string DisplayName => "Temporary Files";
    public CleanupCategory Category => CleanupCategory.TempFiles;

    // Known safe temp locations, canonicalized once so path traversal aliases
    // cannot make an outside file appear to be beneath a temp root.
    private static readonly string[] TempRoots = CreateTempRoots();

    public TempFilesCleanupRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var files = await ScanFilePager.LoadAllAsync(_repo, sessionId, cancellationToken);

        var targets = files
            .Where(static file => file.Identity is not null)
            .Where(IsTemp)
            .ToList();

        if (targets.Count == 0) yield break;

        long totalBytes = targets.Sum(f => f.SizeBytes);
        var paths = targets.Select(f => f.FullPath).ToList();

        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = $"Temporary files ({targets.Count:N0} files)",
            Description = $"Files in canonical Windows temp folders. " +
                             $"Estimated savings: {FormatBytes(totalBytes)}.",
            Category = Category,
            Risk = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths = paths,
            ExpectedFileSnapshots = CreateSnapshots(targets),
            IsSystemPath = false,
        };
    }

    private static bool IsTemp(FileEntry file)
    {
        try
        {
            return TempRoots.Any(root => ScanOptionValidator.IsPathEqualOrUnder(file.FullPath, root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Malformed persisted scan paths fail closed instead of aborting cleanup analysis.
            return false;
        }
    }

    private static string[] CreateTempRoots()
    {
        var candidates = new List<string>();

        AddFolderTempRoot(candidates, Environment.SpecialFolder.Windows);
        AddFolderTempRoot(candidates, Environment.SpecialFolder.LocalApplicationData);

        return candidates
            .Where(Path.IsPathFullyQualified)
            .Select(ScanOptionValidator.NormalizeDirectoryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, FileSnapshot> CreateSnapshots(
        IEnumerable<FileEntry> files) => files.ToDictionary(
        static file => file.FullPath,
        static file => new FileSnapshot(
            file.FullPath,
            file.Identity!,
            file.SizeBytes,
            file.ModifiedUtc,
            file.Attributes),
        StringComparer.OrdinalIgnoreCase);

    private static void AddFolderTempRoot(List<string> roots, Environment.SpecialFolder folder)
    {
        var folderPath = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(folderPath))
            roots.Add(Path.Combine(folderPath, "Temp"));
    }

    private static string FormatBytes(long bytes) => ByteFormat.Format(bytes);
}
