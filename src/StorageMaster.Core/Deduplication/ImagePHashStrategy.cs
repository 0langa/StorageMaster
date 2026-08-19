using System.Numerics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
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

    /// <summary>
    /// Decode-time size hint. Everything past this is thrown away by the 32×32
    /// resize, and for JPEG the decoder serves it straight from the scaled IDCT
    /// instead of materialising the full-resolution surface. 256 rather than 64
    /// because the hint is aspect-preserving (ResizeMode.Max): a panorama at a
    /// 64-px box would arrive with fewer than 32 usable rows.
    /// </summary>
    private const int DecodeTargetSize = 256;

    /// <summary>
    /// cos(π·k·(2t+1) / 2·DctSize) for k &lt; <see cref="HashBits"/>, t &lt; <see cref="DctSize"/>.
    /// Only the top-left 8×8 block of the DCT is ever read, so the higher
    /// frequencies never needed computing; tabulating the rest removes 65 536
    /// Math.Cos calls per image without changing a single coefficient.
    /// </summary>
    private static readonly double[] CosTable = BuildCosTable();

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
    public int AlgorithmVersion => 2; // v2: decode-scaled input (hash values shift)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        // Cluster over distinct hashes, not individual images. Images sharing a
        // hash are the same distance from every possible seed, so they always land
        // in the same group — collapsing them first keeps the output identical and
        // takes exact duplicates (the bulk of a real photo library) out of the
        // pairwise scan entirely.
        var buckets = signatureGroups
            .Where(static kv => kv.Value.Count > 0)
            .Select(kv => (Hash: ParseHash(kv.Key), Candidates: kv.Value))
            .ToList();

        if (buckets.Sum(static b => b.Candidates.Count) < 2) yield break;

        var assigned = new bool[buckets.Count];
        // Resolved once per clustering pass — the settings snapshot must not be
        // consulted inside the O(n²) comparison loop.
        var threshold = ResolveHammingThreshold();

        for (var i = 0; i < buckets.Count; i++)
        {
            if (assigned[i]) continue;

            var seedHash = buckets[i].Hash;
            var group = new List<DuplicateCandidate>(buckets[i].Candidates);
            assigned[i] = true;
            var totalDist = 0;
            // The seed's own same-hash siblings are zero-distance matches; count
            // them so the confidence average stays what per-image clustering gave.
            var comparisons = group.Count - 1;

            for (var j = i + 1; j < buckets.Count; j++)
            {
                if (assigned[j]) continue;

                // Compare against the seed — single-linkage
                var dist = HammingDistance(seedHash, buckets[j].Hash);
                if (dist <= threshold)
                {
                    var matched = buckets[j].Candidates;
                    group.AddRange(matched);
                    assigned[j] = true;
                    totalDist += dist * matched.Count;
                    comparisons += matched.Count;
                }
            }

            if (group.Count < 2) continue;

            var avgDist = comparisons > 0 ? (double)totalDist / comparisons : 0d;
            var confidence = Math.Clamp(1d - avgDist / 64d, 0.5d, 1.0d);

            yield return new DuplicateStrategyMatch(
                group,
                confidence,
                $"Perceptual image match (Hamming ≤ {threshold})");
        }
    }

    // ── DCT pHash ─────────────────────────────────────────────────────────────

    private static (ulong Hash, int Width, int Height, ushort Orientation)
        ComputePHash(string path)
    {
        // One handle for both the header read and the decode; ReadWrite|Delete
        // sharing matches the other hashing strategies so a file open elsewhere
        // does not turn into a spurious PHashError.
        using var stream = new FileStream(
            path,
            FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        // Header first: the decode below is deliberately scaled down, so the true
        // pixel dimensions have to come from the metadata rather than from the
        // decoded surface.
        var info = Image.Identify(stream);
        stream.Position = 0;

        ushort orientation = 1;
        if (info.Metadata.ExifProfile?.TryGetValue(ExifTag.Orientation, out var orientTag) == true)
            orientation = orientTag?.Value ?? 1;

        using var img = Image.Load<L8>(
            new DecoderOptions { TargetSize = new Size(DecodeTargetSize, DecodeTargetSize) },
            stream);

        // Correct EXIF orientation before hashing so that the same photo
        // stored in different orientations gets the same hash.
        img.Mutate(x => x.AutoOrient());

        // Orientations 5-8 transpose the image, so the reported dimensions follow
        // AutoOrient the way they did when this read them off the decoded image.
        var transposed = orientation is >= 5 and <= 8;
        var width = transposed ? info.Height : info.Width;
        var height = transposed ? info.Width : info.Height;

        return (ComputeHashFromImage(img), width, height, orientation);
    }

    /// <summary>
    /// pHash of an already-decoded, orientation-corrected grayscale surface.
    /// Shared with <see cref="VideoPHashStrategy"/> so the frame hash and the
    /// image hash cannot drift apart.
    /// </summary>
    internal static ulong ComputeHashFromImage(Image<L8> img)
    {
        img.Mutate(x => x.Resize(DctSize, DctSize));

        // Build float grayscale matrix
        var pixels = new float[DctSize * DctSize];
        for (var y = 0; y < DctSize; y++)
            for (var x = 0; x < DctSize; x++)
                pixels[y * DctSize + x] = img[x, y].PackedValue / 255f;

        // Top-left HashBits×HashBits block of the 2-D DCT
        var coeffs = ComputeTopLeftDct(pixels);

        // Mean excluding DC [0,0] at index 0
        var sum = 0d;
        for (var i = 1; i < coeffs.Length; i++) sum += coeffs[i];
        var avg = sum / (coeffs.Length - 1);

        // 64-bit hash
        var hash = 0UL;
        for (var i = 0; i < coeffs.Length; i++)
            if (coeffs[i] >= avg)
                hash |= 1UL << i;

        return hash;
    }

    /// <summary>
    /// Separable DCT-II truncated to the coefficients the hash actually reads.
    /// Both passes stop at <see cref="HashBits"/> frequencies because the column
    /// pass only ever consumes the first <see cref="HashBits"/> columns of the row
    /// pass — the discarded three quarters were pure waste. Summation order is
    /// unchanged, so coefficients are bit-identical to the untruncated form.
    /// </summary>
    private static double[] ComputeTopLeftDct(float[] input)
    {
        // Row pass: [row, kx] for kx < HashBits.
        var temp = new double[DctSize * HashBits];
        for (var row = 0; row < DctSize; row++)
        {
            var rowOffset = row * DctSize;
            for (var kx = 0; kx < HashBits; kx++)
            {
                var cosOffset = kx * DctSize;
                var s = 0d;
                for (var t = 0; t < DctSize; t++)
                    s += input[rowOffset + t] * CosTable[cosOffset + t];
                temp[(row * HashBits) + kx] = s;
            }
        }

        // Column pass: [ky, kx] for ky < HashBits.
        var coeffs = new double[HashBits * HashBits];
        for (var kx = 0; kx < HashBits; kx++)
        {
            for (var ky = 0; ky < HashBits; ky++)
            {
                var cosOffset = ky * DctSize;
                var s = 0d;
                for (var t = 0; t < DctSize; t++)
                    s += temp[(t * HashBits) + kx] * CosTable[cosOffset + t];
                coeffs[(ky * HashBits) + kx] = s;
            }
        }
        return coeffs;
    }

    private static double[] BuildCosTable()
    {
        var table = new double[HashBits * DctSize];
        for (var k = 0; k < HashBits; k++)
            for (var t = 0; t < DctSize; t++)
                table[(k * DctSize) + t] = Math.Cos(Math.PI * k * ((2 * t) + 1) / (2d * DctSize));
        return table;
    }

    private static int HammingDistance(ulong a, ulong b) =>
        BitOperations.PopCount(a ^ b);

    private static ulong ParseHash(string? hex) =>
        ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0UL;

    // Instance method so the stamped algorithm identity can never drift from the
    // properties above when the version is bumped.
    private DuplicateSignature ErrorSig(DuplicateCandidate c, string type, string msg) =>
        new()
        {
            Id = 0,
            SessionId = c.File.SessionId,
            FileEntryId = c.File.Id,
            Method = Method,
            Algorithm = Algorithm,
            AlgorithmVersion = AlgorithmVersion,
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
