using System.Numerics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Perceptual hash (pHash) strategy for image files.
///
/// Algorithm: DCT-based 64-bit pHash
///   1. Load image and correct EXIF orientation.
///   2. Resize to 32×32 grayscale.
///   3. Compute 2-D separable DCT.
///   4. Take top-left 8×8 coefficients (64 values); exclude DC component at [0,0].
///   5. Compute average of those 63 values.
///   6. Set bit i when coefficient[i] ≥ average; store as 8-byte big-endian blob.
///
/// Comparison: Hamming distance ≤ <see cref="DefaultHammingThreshold"/> (10/64).
/// Confidence = 1 − (hammingDist / 64).
///
/// Default: review-only (<see cref="SupportsAutoSelection"/> = false).
/// </summary>
public sealed class ImagePHashStrategy : IDuplicateDetectionStrategy
{
    public const int DefaultHammingThreshold = 10;    // out of 64 bits
    public const double AutoSelectConfidenceThreshold = 0.98d; // future opt-in

    private const int DctSize = 32;
    private const int HashBits = 8;   // top-left 8×8 block of DCT

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
            ".tif", ".tiff",
            ".heic", ".heif",   // requires platform decoder; errors surface gracefully
        };

    private readonly int _hammingThreshold;
    private readonly IFileSnapshotProvider? _snapshotProvider;
    private readonly ISettingsSnapshotProvider? _settingsSnapshotProvider;

    public ImagePHashStrategy(
        IFileSnapshotProvider? snapshotProvider = null,
        int hammingThreshold = DefaultHammingThreshold)
    {
        _snapshotProvider = snapshotProvider;
        _hammingThreshold = hammingThreshold;
    }

    public ImagePHashStrategy(
        ISettingsSnapshotProvider settingsSnapshotProvider,
        IFileSnapshotProvider? snapshotProvider = null)
    {
        _settingsSnapshotProvider = settingsSnapshotProvider;
        _snapshotProvider = snapshotProvider;
        _hammingThreshold = DefaultHammingThreshold;
    }

    public DuplicateMethod Method => DuplicateMethod.ImagePHash;
    public string Algorithm => "IMAGE-PHASH-DCT64";
    public int AlgorithmVersion => 1;
    public bool SupportsAutoSelection => false;
    public double DefaultConfidence => 0.0d;   // computed per-pair
    public string DisplayName => "Image perceptual hash";

    public DuplicateCandidateQuery BuildCandidateQuery(DuplicateScanOptions options) =>
        new()
        {
            SessionId = options.SessionId,
            MinimumSizeBytes = options.MinimumSizeBytes,
            RequireSameSizeBucket = false,  // resized/transcoded images differ in size
            Extensions = options.IncludeExtensions.Count > 0
                ? options.IncludeExtensions
                      .Where(e => SupportedExtensions.Contains(e))
                      .ToList()
                : SupportedExtensions.ToList(),
            Categories = options.IncludeCategories.Count > 0
                ? options.IncludeCategories
                : [FileTypeCategory.Image],
            IncludedPaths = options.IncludedPaths,
            ExcludedPaths = options.ExcludedPaths,
            IncludeReparsePoints = options.IncludeReparsePoints,
            IncludeHiddenFiles = options.IncludeHiddenFiles,
        };

    public async Task<DuplicateSignature> ComputeSignatureAsync(
        DuplicateCandidate candidate,
        CancellationToken ct = default)
    {
        if (!SupportedExtensions.Contains(candidate.File.Extension))
            return ErrorSig(candidate, "UnsupportedExtension",
                $"{candidate.File.Extension} is not a supported image format.");

        try
        {
            var before = _snapshotProvider is null
                ? null
                : await _snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
            if (_snapshotProvider is not null && before is null)
                return ErrorSig(candidate, "FileNotFound", "File no longer exists before image hashing.");

            // Run synchronous ImageSharp I/O on thread-pool thread
            var (hash64, width, height, orientation) = await Task.Run(
                () => ComputePHash(candidate.File.FullPath), ct);
            var after = _snapshotProvider is null
                ? null
                : await _snapshotProvider.TakeSnapshotAsync(candidate.File.FullPath, ct);
            if (_snapshotProvider is not null && (after is null || !before!.IsIdenticalTo(after)))
                return ErrorSig(candidate, "FileChangedDuringHash", "Image changed while perceptual hash was being computed.");

            var meta = JsonSerializer.Serialize(new
            {
                width,
                height,
                orientation,
                hammingThreshold = ResolveHammingThreshold(),
            });

            return new DuplicateSignature
            {
                Id = 0,
                SessionId = candidate.File.SessionId,
                FileEntryId = candidate.File.Id,
                Method = Method,
                Algorithm = Algorithm,
                AlgorithmVersion = AlgorithmVersion,
                SignatureBlob = BitConverter.GetBytes(hash64),
                SignatureText = hash64.ToString("X16"),
                MetadataJson = meta,
                ComputedUtc = DateTime.UtcNow,
                Status = "Ready",
                SourceSizeBytes = before?.SizeBytes ?? candidate.File.SizeBytes,
                SourceModifiedUtc = before?.LastWriteUtc ?? candidate.File.ModifiedUtc,
                SourceFileIdentity = before?.Identity is { } id
                    ? $"{id.VolumeSerial}:{id.FileIndex}"
                    : null,
            };
        }
        catch (Exception ex)
        {
            return ErrorSig(candidate, "PHashError", ex.Message);
        }
    }

    /// <summary>
    /// Clusters images by Hamming-distance proximity using greedy single-linkage.
    /// Images are compared pairwise; a new group is seeded whenever an unassigned
    /// image is within threshold of the current seed.
    ///
    /// Input dictionary keys are hex-encoded 64-bit hashes; exact-duplicate images
    /// (same hash) arrive in the same bucket and are always grouped together.
    /// Near-duplicates in different buckets are merged during the clustering pass.
    /// </summary>
    public IEnumerable<DuplicateStrategyMatch> BuildMatches(
        IReadOnlyDictionary<string, IReadOnlyList<DuplicateCandidate>> signatureGroups)
    {
        // Flatten to (hash, candidate) pairs — each candidate has exactly one hash.
        var items = signatureGroups
            .SelectMany(kv =>
            {
                var hash = ParseHash(kv.Key);
                return kv.Value.Select(c => (Hash: hash, Candidate: c));
            })
            .ToList();

        if (items.Count < 2) yield break;

        var assigned = new bool[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            if (assigned[i]) continue;

            var group = new List<(int Index, DuplicateCandidate Candidate)> { (i, items[i].Candidate) };
            assigned[i] = true;
            var totalDist = 0;
            var comparisons = 0;

            for (var j = i + 1; j < items.Count; j++)
            {
                if (assigned[j]) continue;

                // Compare against the seed (items[i]) — single-linkage
                var threshold = ResolveHammingThreshold();
                var dist = HammingDistance(items[i].Hash, items[j].Hash);
                if (dist <= threshold)
                {
                    group.Add((j, items[j].Candidate));
                    assigned[j] = true;
                    totalDist += dist;
                    comparisons++;
                }
            }

            if (group.Count < 2) continue;

            var avgDist = comparisons > 0 ? (double)totalDist / comparisons : 0d;
            var confidence = Math.Clamp(1d - avgDist / 64d, 0.5d, 1.0d);

            yield return new DuplicateStrategyMatch(
                group.Select(static g => g.Candidate).ToList(),
                confidence,
                $"Perceptual image match (Hamming ≤ {ResolveHammingThreshold()})");
        }
    }

    // ── DCT pHash ─────────────────────────────────────────────────────────────

    private static (ulong Hash, int Width, int Height, ushort Orientation)
        ComputePHash(string path)
    {
        using var img = Image.Load<L8>(path);

        // Correct EXIF orientation before hashing so that the same photo
        // stored in different orientations gets the same hash.
        ushort orientation = 1;
        if (img.Metadata.ExifProfile?.TryGetValue(ExifTag.Orientation, out var orientTag) == true)
            orientation = orientTag?.Value ?? 1;
        img.Mutate(x => x.AutoOrient());

        var width = img.Width;
        var height = img.Height;

        img.Mutate(x => x.Resize(DctSize, DctSize));

        // Build float grayscale matrix
        var pixels = new float[DctSize * DctSize];
        for (var y = 0; y < DctSize; y++)
            for (var x = 0; x < DctSize; x++)
                pixels[y * DctSize + x] = img[x, y].PackedValue / 255f;

        var dct = ComputeDct2D(pixels, DctSize);

        // Extract top-left HashBits×HashBits block
        var coeffs = new double[HashBits * HashBits];
        for (var ky = 0; ky < HashBits; ky++)
            for (var kx = 0; kx < HashBits; kx++)
                coeffs[ky * HashBits + kx] = dct[ky * DctSize + kx];

        // Mean excluding DC [0,0] at index 0
        var sum = 0d;
        for (var i = 1; i < coeffs.Length; i++) sum += coeffs[i];
        var avg = sum / (coeffs.Length - 1);

        // 64-bit hash
        var hash = 0UL;
        for (var i = 0; i < coeffs.Length; i++)
            if (coeffs[i] >= avg)
                hash |= 1UL << i;

        return (hash, width, height, orientation);
    }

    private static double[] ComputeDct2D(float[] input, int n)
    {
        var temp = new double[n * n];
        var output = new double[n * n];

        // Row-wise DCT-II
        for (var row = 0; row < n; row++)
            Dct1D(input, row * n, n, temp, row * n);

        // Column-wise DCT-II
        var col = new double[n];
        var colOut = new double[n];
        for (var c = 0; c < n; c++)
        {
            for (var row = 0; row < n; row++)
                col[row] = temp[row * n + c];
            Dct1D(col, colOut, n);
            for (var row = 0; row < n; row++)
                output[row * n + c] = colOut[row];
        }
        return output;
    }

    private static void Dct1D(float[] src, int srcOffset, int n, double[] dst, int dstOffset)
    {
        for (var k = 0; k < n; k++)
        {
            var s = 0d;
            for (var t = 0; t < n; t++)
                s += src[srcOffset + t] * Math.Cos(Math.PI * k * (2 * t + 1) / (2d * n));
            dst[dstOffset + k] = s;
        }
    }

    private static void Dct1D(double[] src, double[] dst, int n)
    {
        for (var k = 0; k < n; k++)
        {
            var s = 0d;
            for (var t = 0; t < n; t++)
                s += src[t] * Math.Cos(Math.PI * k * (2 * t + 1) / (2d * n));
            dst[k] = s;
        }
    }

    private static int HammingDistance(ulong a, ulong b) =>
        BitOperations.PopCount(a ^ b);

    private static ulong ParseHash(string? hex) =>
        ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0UL;

    private static DuplicateSignature ErrorSig(DuplicateCandidate c, string type, string msg) =>
        new()
        {
            Id = 0,
            SessionId = c.File.SessionId,
            FileEntryId = c.File.Id,
            Method = DuplicateMethod.ImagePHash,
            Algorithm = "IMAGE-PHASH-DCT64",
            AlgorithmVersion = 1,
            ComputedUtc = DateTime.UtcNow,
            Status = "Error",
            ErrorMessage = msg,
            SourceSizeBytes = c.File.SizeBytes,
            SourceModifiedUtc = c.File.ModifiedUtc,
        };

    private int ResolveHammingThreshold()
    {
        if (_settingsSnapshotProvider is null)
            return _hammingThreshold;

        return Math.Clamp(_settingsSnapshotProvider.Current.DuplicateImagePHashThreshold, 2, 16);
    }
}
