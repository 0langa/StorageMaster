using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Perceptual fingerprint strategy for video files using FFmpeg-extracted keyframes.
///
/// Algorithm:
///   1. Probe duration + metadata via ffprobe.
///   2. Extract <see cref="FrameSampleCount"/> frames at deterministic timestamps
///      (evenly spaced at 10%, 20%, … 100% of duration).
///   3. Compute DCT-64 pHash for each frame using <see cref="ImagePHashStrategy"/>.
///   4. Fingerprint = list of <c>FrameSampleCount</c> 64-bit hashes.
///   5. Two videos match if ≥ <see cref="MinMatchingFrameFraction"/> of their
///      frame-hash pairs are within <see cref="FrameHammingThreshold"/> bits.
///
/// Gate conditions (returns error signature when not met):
///   • FFmpeg path configured (<see cref="AppSettings.FfmpegPath"/>).
///   • ffprobe.exe present beside ffmpeg.exe.
///   • File extension in <see cref="SupportedExtensions"/>.
///   • Duration ≤ <see cref="MaxDurationSeconds"/>.
///
/// Default: review-only (<see cref="SupportsAutoSelection"/> = false).
/// </summary>
public sealed class VideoPHashStrategy : IDuplicateDetectionStrategy
{
    public const int    FrameSampleCount          = 10;
    public const int    FrameHammingThreshold     = 10;
    public const double MinMatchingFrameFraction  = 0.8;   // 80 % of frames must match
    public const int    DefaultMaxDurationSeconds = 3600;  // 1 hour

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v",
            ".wmv", ".flv", ".ts", ".mts", ".m2ts",
        };

    private readonly string? _ffmpegPath;
    private readonly int     _maxDurationSeconds;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly string  _appBaseDirectory;
    private readonly IFileSnapshotProvider? _snapshotProvider;

    public VideoPHashStrategy(
        string ffmpegPath,
        IFileSnapshotProvider? snapshotProvider = null,
        int maxDurationSeconds = DefaultMaxDurationSeconds)
    {
        _ffmpegPath         = ffmpegPath;
        _snapshotProvider   = snapshotProvider;
        _maxDurationSeconds = maxDurationSeconds;
        _appBaseDirectory   = AppContext.BaseDirectory;
    }

    public VideoPHashStrategy(
        ISettingsRepository settingsRepository,
        IFileSnapshotProvider? snapshotProvider = null,
        string? appBaseDirectory = null)
    {
        _settingsRepository = settingsRepository;
        _snapshotProvider   = snapshotProvider;
        _maxDurationSeconds = DefaultMaxDurationSeconds;
        _appBaseDirectory   = string.IsNullOrWhiteSpace(appBaseDirectory)
            ? AppContext.BaseDirectory
            : appBaseDirectory;
    }

    public DuplicateMethod Method           => DuplicateMethod.VideoPHash;
    public string          Algorithm        => "VIDEO-PHASH-FRAMES10";
    public int             AlgorithmVersion => 1;
    public bool            SupportsAutoSelection => false;
    public double          DefaultConfidence     => 0.0d;   // computed per-pair
    public string          DisplayName           => "Video perceptual hash";

    /// <summary>
    /// Returns true when FFmpeg is configured and the binary exists.
    /// Should be called before registering this strategy in DI to avoid
    /// registering an always-failing strategy.
    /// </summary>
    public bool IsAvailable => ResolveTools().IsComplete;

    public string? UnavailableReason => IsAvailable
        ? null
        : "FFmpeg/ffprobe not found. Bundle them with app, put them on PATH, or set ffmpeg.exe in Settings.";

    public DuplicateCandidateQuery BuildCandidateQuery(DuplicateScanOptions options) =>
        new()
        {
            SessionId             = options.SessionId,
            MinimumSizeBytes      = options.MinimumSizeBytes,
            RequireSameSizeBucket = false,  // transcoded videos differ in size
            Extensions            = options.IncludeExtensions.Count > 0
                ? options.IncludeExtensions
                      .Where(e => SupportedExtensions.Contains(e))
                      .ToList()
                : SupportedExtensions.ToList(),
            Categories            = options.IncludeCategories.Count > 0
                ? options.IncludeCategories
                : [FileTypeCategory.Video],
            IncludedPaths         = options.IncludedPaths,
            ExcludedPaths         = options.ExcludedPaths,
            IncludeReparsePoints  = options.IncludeReparsePoints,
            IncludeHiddenFiles    = options.IncludeHiddenFiles,
        };

    public async Task<DuplicateSignature> ComputeSignatureAsync(
        DuplicateCandidate candidate,
        CancellationToken  ct = default)
    {
        if (!SupportedExtensions.Contains(candidate.File.Extension))
            return ErrorSig(candidate, "UnsupportedExtension",
                $"{candidate.File.Extension} is not a supported video format.");

        var tools = await ResolveToolsAsync(ct);
        if (!tools.IsComplete)
            return ErrorSig(candidate, "FfmpegNotFound",
                "FFmpeg/ffprobe not found. Bundle them with app, put them on PATH, or set ffmpeg.exe in Settings.");

        try
        {
            var before = _snapshotProvider is null
                ? null
                : await _snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
            if (_snapshotProvider is not null && before is null)
                return ErrorSig(candidate, "FileNotFound", "File no longer exists before video hashing.");

            // 1. Probe duration
            var maxDurationSeconds = await ResolveMaxDurationSecondsAsync(ct);
            var duration = await ProbeDurationAsync(tools.FfprobePath, candidate.File.FullPath, ct);
            if (duration <= 0)
                return ErrorSig(candidate, "ProbeError", "Could not determine video duration.");
            if (duration > maxDurationSeconds)
                return ErrorSig(candidate, "TooLong",
                    $"Video duration {duration:F0}s exceeds limit {maxDurationSeconds}s.");

            // 2. Extract frames at deterministic timestamps
            var timestamps = Enumerable
                .Range(1, FrameSampleCount)
                .Select(i => duration * i / FrameSampleCount)
                .ToArray();

            var frameHashes = new ulong[FrameSampleCount];
            var tempDir     = Path.Combine(Path.GetTempPath(), $"sm_vphash_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                for (var fi = 0; fi < FrameSampleCount; fi++)
                {
                    ct.ThrowIfCancellationRequested();
                    var framePath = Path.Combine(tempDir, $"frame_{fi:D2}.png");
                    await ExtractFrameAsync(tools.FfmpegPath, candidate.File.FullPath,
                        timestamps[fi], framePath, ct);

                    if (File.Exists(framePath))
                    {
                        frameHashes[fi] = await Task.Run(
                            () => ComputeFrameHash(framePath), ct);
                        File.Delete(framePath);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
            }

            // Encode fingerprint as array of hex hashes separated by ','
            var fingerprint = string.Join(",", frameHashes.Select(h => h.ToString("X16")));
            var after = _snapshotProvider is null
                ? null
                : await _snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
            if (_snapshotProvider is not null && (after is null || !before!.IsIdenticalTo(after)))
                return ErrorSig(candidate, "FileChangedDuringHash", "Video changed while fingerprint was being computed.");

            var meta = JsonSerializer.Serialize(new
            {
                duration,
                frameCount    = FrameSampleCount,
                timestamps,
                frameHashes   = frameHashes.Select(static h => h.ToString("X16")).ToArray(),
                hammingThreshold = FrameHammingThreshold,
            });

            return new DuplicateSignature
            {
                Id               = 0,
                SessionId        = candidate.File.SessionId,
                FileEntryId      = candidate.File.Id,
                Method           = Method,
                Algorithm        = Algorithm,
                AlgorithmVersion = AlgorithmVersion,
                SignatureText    = fingerprint,
                MetadataJson     = meta,
                ComputedUtc      = DateTime.UtcNow,
                Status           = "Ready",
                SourceSizeBytes  = before?.SizeBytes ?? candidate.File.SizeBytes,
                SourceModifiedUtc = before?.LastWriteUtc ?? candidate.File.ModifiedUtc,
                SourceFileIdentity = before?.Identity is { } id
                    ? $"{id.VolumeSerial}:{id.FileIndex}"
                    : null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorSig(candidate, "VideoHashError", ex.Message);
        }
    }

    /// <summary>
    /// Clusters videos by frame-fingerprint similarity.
    /// Two fingerprints match when ≥ MinMatchingFrameFraction of frame pairs
    /// are within FrameHammingThreshold bits.
    /// </summary>
    public IEnumerable<DuplicateStrategyMatch> BuildMatches(
        IReadOnlyDictionary<string, IReadOnlyList<DuplicateCandidate>> signatureGroups)
    {
        // Flatten: (fingerprint, candidate)
        var items = signatureGroups
            .SelectMany(kv =>
            {
                var fps = ParseFingerprint(kv.Key);
                return kv.Value.Select(c => (Fingerprint: fps, Candidate: c));
            })
            .ToList();

        if (items.Count < 2) yield break;

        var assigned = new bool[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            if (assigned[i]) continue;

            var group       = new List<DuplicateCandidate> { items[i].Candidate };
            assigned[i]     = true;
            var totalSim    = 0d;
            var groupCount  = 0;

            for (var j = i + 1; j < items.Count; j++)
            {
                if (assigned[j]) continue;
                var sim = FingerprintSimilarity(items[i].Fingerprint, items[j].Fingerprint);
                if (sim >= MinMatchingFrameFraction)
                {
                    group.Add(items[j].Candidate);
                    assigned[j]  = true;
                    totalSim    += sim;
                    groupCount++;
                }
            }

            if (group.Count < 2) continue;

            var avgSim     = groupCount > 0 ? totalSim / groupCount : 1d;
            var confidence = Math.Clamp(avgSim, 0.5d, 1.0d);

            yield return new DuplicateStrategyMatch(
                group,
                confidence,
                $"Video perceptual match ({avgSim * 100:F0}% frames match)");
        }
    }

    // ── FFmpeg helpers ────────────────────────────────────────────────────────

    private static async Task<double> ProbeDurationAsync(
        string ffprobePath, string videoPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffprobePath,
            $"-v error -show_entries format=duration -of csv=p=0 \"{videoPath}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return double.TryParse(stdout.Trim(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0d;
    }

    private static async Task ExtractFrameAsync(
        string ffmpegPath, string videoPath, double timestamp, string outPath, CancellationToken ct)
    {
        var ts  = timestamp.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(ffmpegPath,
            $"-ss {ts} -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{outPath}\" -y")
        {
            UseShellExecute       = false,
            RedirectStandardError = true,
            CreateNoWindow        = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        await proc.WaitForExitAsync(ct);
    }

    // ── Frame hash (reuse ImagePHashStrategy DCT logic) ───────────────────────

    private static ulong ComputeFrameHash(string framePath)
    {
        using var img = Image.Load<L8>(framePath);
        img.Mutate(x => x.Resize(32, 32));

        var pixels = new float[32 * 32];
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
            pixels[y * 32 + x] = img[x, y].PackedValue / 255f;

        var dct    = ComputeDct2D(pixels, 32);
        var coeffs = new double[64];
        for (var ky = 0; ky < 8; ky++)
        for (var kx = 0; kx < 8; kx++)
            coeffs[ky * 8 + kx] = dct[ky * 32 + kx];

        var sum = 0d;
        for (var i = 1; i < 64; i++) sum += coeffs[i];
        var avg = sum / 63d;

        var hash = 0UL;
        for (var i = 0; i < 64; i++)
            if (coeffs[i] >= avg)
                hash |= 1UL << i;

        return hash;
    }

    private static double[] ComputeDct2D(float[] input, int n)
    {
        var temp   = new double[n * n];
        var output = new double[n * n];
        for (var row = 0; row < n; row++)
        {
            for (var k = 0; k < n; k++)
            {
                var s = 0d;
                for (var t = 0; t < n; t++)
                    s += input[row * n + t] * Math.Cos(Math.PI * k * (2 * t + 1) / (2d * n));
                temp[row * n + k] = s;
            }
        }
        var col    = new double[n];
        var colOut = new double[n];
        for (var c = 0; c < n; c++)
        {
            for (var row = 0; row < n; row++) col[row] = temp[row * n + c];
            for (var k = 0; k < n; k++)
            {
                var s = 0d;
                for (var t = 0; t < n; t++) s += col[t] * Math.Cos(Math.PI * k * (2 * t + 1) / (2d * n));
                colOut[k] = s;
            }
            for (var row = 0; row < n; row++) output[row * n + c] = colOut[row];
        }
        return output;
    }

    // ── Fingerprint comparison ────────────────────────────────────────────────

    private static ulong[] ParseFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return [];
        return fingerprint.Split(',')
            .Select(s => ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0UL)
            .ToArray();
    }

    private static double FingerprintSimilarity(ulong[] a, ulong[] b)
    {
        var count = Math.Min(a.Length, b.Length);
        if (count == 0) return 0d;
        var matches = 0;
        for (var i = 0; i < count; i++)
            if (BitOperations.PopCount(a[i] ^ b[i]) <= FrameHammingThreshold)
                matches++;
        return (double)matches / count;
    }

    private static DuplicateSignature ErrorSig(DuplicateCandidate c, string type, string msg) =>
        new()
        {
            Id = 0, SessionId = c.File.SessionId, FileEntryId = c.File.Id,
            Method = DuplicateMethod.VideoPHash, Algorithm = "VIDEO-PHASH-FRAMES10",
            AlgorithmVersion = 1,
            ComputedUtc = DateTime.UtcNow, Status = "Error", ErrorMessage = msg,
            SourceSizeBytes = c.File.SizeBytes, SourceModifiedUtc = c.File.ModifiedUtc,
        };

    private FfmpegToolPaths ResolveTools()
    {
        if (_settingsRepository is null)
        {
            return FfmpegToolResolver.Resolve(
                _ffmpegPath,
                _appBaseDirectory);
        }

        var settings = _settingsRepository.LoadAsync().GetAwaiter().GetResult();
        return FfmpegToolResolver.Resolve(
            settings.FfmpegPath,
            _appBaseDirectory);
    }

    private async Task<FfmpegToolPaths> ResolveToolsAsync(CancellationToken ct)
    {
        if (_settingsRepository is null)
            return FfmpegToolResolver.Resolve(_ffmpegPath, _appBaseDirectory);

        var settings = await _settingsRepository.LoadAsync(ct);
        return FfmpegToolResolver.Resolve(settings.FfmpegPath, _appBaseDirectory);
    }

    private async Task<int> ResolveMaxDurationSecondsAsync(CancellationToken ct)
    {
        if (_settingsRepository is null)
            return _maxDurationSeconds;

        var settings = await _settingsRepository.LoadAsync(ct);
        return Math.Max(60, settings.DuplicateMaxVideoDurationSeconds);
    }
}
