using System.Security.Cryptography;
using System.Text;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Detection strategy for text/code files that are equivalent after normalization
/// (line-endings, trailing whitespace, Unicode NFC, optional BOM removal).
///
/// IMPORTANT: <see cref="DuplicateCandidateQuery.RequireSameSizeBucket"/> is false
/// because whitespace normalization changes byte counts — the original same-size
/// SQL optimisation would miss many valid text duplicates.
///
/// Default: review-only (SupportsAutoSelection = false) because whitespace
/// normalization may hide meaningful differences.
/// </summary>
public sealed class NormalizedTextStrategy : IDuplicateDetectionStrategy
{
    // Supported extensions configurable at construction; default covers common text/code.
    private readonly HashSet<string> _supportedExtensions;
    private readonly IFileSnapshotProvider? _snapshotProvider;

    /// <summary>Maximum normalized bytes before a file is skipped (avoids huge log/generated files).</summary>
    private const long MaxNormalizedBytes = 50 * 1024 * 1024; // 50 MB

    public static readonly IReadOnlySet<string> DefaultExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".config",
        ".cs", ".csproj", ".sln", ".ts", ".js", ".jsx", ".tsx", ".py", ".java",
        ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".ps1", ".cmd", ".bat",
        ".html", ".htm", ".css", ".scss", ".sql", ".toml", ".log", ".rtf",
        ".rb", ".php", ".swift", ".kt", ".scala", ".dart", ".lua",
        ".sh", ".zsh", ".fish", ".dockerfile",
    };

    public NormalizedTextStrategy(
        IFileSnapshotProvider? snapshotProvider = null,
        IReadOnlySet<string>? customExtensions = null)
    {
        _snapshotProvider = snapshotProvider;
        _supportedExtensions = customExtensions is { Count: > 0 }
            ? new HashSet<string>(customExtensions, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(DefaultExtensions, StringComparer.OrdinalIgnoreCase);
    }

    public DuplicateMethod Method => DuplicateMethod.NormalizedText;
    public string Algorithm => "TEXT-NORM-SHA256";
    public int AlgorithmVersion => 2; // v2: extended extension list
    public bool SupportsAutoSelection => false; // always review
    public double DefaultConfidence => 0.8d;
    public string DisplayName => "Normalized text";

    public DuplicateCandidateQuery BuildCandidateQuery(DuplicateScanOptions options) =>
        new()
        {
            SessionId = options.SessionId,
            MinimumSizeBytes = options.MinimumSizeBytes,
            RequireSameSizeBucket = false,  // P0.1 fix: do NOT require same raw size
            Extensions = options.IncludeExtensions.Count > 0
                ? options.IncludeExtensions
                : _supportedExtensions.ToList(),
            Categories = options.IncludeCategories,
            IncludedPaths = options.IncludedPaths,
            ExcludedPaths = options.ExcludedPaths,
            IncludeReparsePoints = options.IncludeReparsePoints,
            IncludeHiddenFiles = options.IncludeHiddenFiles,
        };

    public async Task<DuplicateSignature> ComputeSignatureAsync(
        DuplicateCandidate candidate,
        CancellationToken ct = default)
    {
        if (!_supportedExtensions.Contains(candidate.File.Extension))
            return ErrorSignature(candidate, "UnsupportedExtension",
                $"Extension {candidate.File.Extension} is not supported for normalized text review.");

        if (candidate.File.SizeBytes > MaxNormalizedBytes)
            return ErrorSignature(candidate, "FileTooLarge",
                $"File exceeds {MaxNormalizedBytes / 1024 / 1024} MB limit for text normalization.");

        try
        {
            var before = _snapshotProvider is null
                ? null
                : await _snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
            if (_snapshotProvider is not null && before is null)
                return ErrorSignature(candidate, "FileNotFound", "File no longer exists before normalization.");

            using var stream = new FileStream(
                candidate.File.FullPath,
                FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                useAsync: true);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            var capacity = (int)Math.Min(candidate.File.SizeBytes, 1_000_000);
            var sb = new StringBuilder(capacity);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct) ?? string.Empty;
                // TrimEnd removes trailing whitespace; Normalize(FormC) canonicalises Unicode.
                sb.Append(line.TrimEnd().Normalize(NormalizationForm.FormC));
                sb.Append('\n');
            }

            var normalized = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant();
            var after = _snapshotProvider is null
                ? null
                : await _snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
            if (_snapshotProvider is not null && (after is null || !before!.IsIdenticalTo(after)))
                return ErrorSignature(candidate, "FileChangedDuringHash", "File was modified or removed while being normalized.");

            return new DuplicateSignature
            {
                Id = 0,
                SessionId = candidate.File.SessionId,
                FileEntryId = candidate.File.Id,
                Method = Method,
                Algorithm = Algorithm,
                AlgorithmVersion = AlgorithmVersion,
                SignatureText = hash,
                MetadataJson = $"{{\"normalizedBytes\":{normalized.Length}}}",
                ComputedUtc = DateTime.UtcNow,
                Status = "Ready",
                SourceSizeBytes = before?.SizeBytes ?? candidate.File.SizeBytes,
                SourceModifiedUtc = before?.LastWriteUtc ?? candidate.File.ModifiedUtc,
                SourceFileIdentity = before?.Identity is { } id
                    ? $"{id.VolumeSerial}:{id.FileIndex}"
                    : null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorSignature(candidate, "NormalizationError", ex.Message);
        }
    }

    public IEnumerable<DuplicateStrategyMatch> BuildMatches(
        IReadOnlyDictionary<string, IReadOnlyList<DuplicateCandidate>> signatureGroups)
    {
        foreach (var (_, candidates) in signatureGroups)
        {
            if (candidates.Count < 2) continue;

            // De-duplicate by full path (case-insensitive) — same file appearing twice.
            var distinct = candidates
                .GroupBy(static c => c.File.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(static g => g.First())
                .ToList();

            if (distinct.Count < 2) continue;

            yield return new DuplicateStrategyMatch(
                distinct,
                DefaultConfidence,
                "Normalized text review");
        }
    }

    // ── Legacy static helper (kept for backward compat with old tests) ───────

    /// <summary>Returns true when this file's extension is in the default supported set.</summary>
    public static bool CanProcess(FileEntry file) =>
        DefaultExtensions.Contains(file.Extension);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DuplicateSignature ErrorSignature(DuplicateCandidate c, string errorType, string message) =>
        new()
        {
            Id = 0,
            SessionId = c.File.SessionId,
            FileEntryId = c.File.Id,
            Method = DuplicateMethod.NormalizedText,
            Algorithm = "TEXT-NORM-SHA256",
            AlgorithmVersion = 2,
            ComputedUtc = DateTime.UtcNow,
            Status = "Error",
            ErrorMessage = message,
            SourceSizeBytes = c.File.SizeBytes,
            SourceModifiedUtc = c.File.ModifiedUtc,
        };
}
