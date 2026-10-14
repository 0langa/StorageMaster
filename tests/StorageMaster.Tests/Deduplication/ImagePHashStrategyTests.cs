using System.Numerics;
using System.Text.Json;
using FluentAssertions;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;
using Windows.Graphics.Imaging;

namespace StorageMaster.Tests.Deduplication;

/// <summary>
/// The image perceptual hash on the Windows Imaging Component.
/// <para>
/// The fixtures are drawn and encoded here, through the same WIC encoders, so the
/// tests need no binary files and no network. Every file lives in a temp folder
/// created for the test and removed afterwards.
/// </para>
/// </summary>
public sealed class ImagePHashStrategyTests : IDisposable
{
    private const int Width = 160;
    private const int Height = 96;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sm_phash_{Guid.NewGuid():N}");
    private readonly ImagePHashStrategy _strategy = new(new WicImageHashDecoder());

    public ImagePHashStrategyTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SamePictureAsPngJpegAndBmp_HashesAlike_AndItsNegativeDoesNot()
    {
        var png = await WriteAsync("picture.png", BitmapEncoder.PngEncoderId, Width, Height, Upright);
        var jpeg = await WriteAsync("picture.jpg", BitmapEncoder.JpegEncoderId, Width, Height, Upright);
        var bmp = await WriteAsync("picture.bmp", BitmapEncoder.BmpEncoderId, Width, Height, Upright);
        var negative = await WriteAsync("negative.png", BitmapEncoder.PngEncoderId, Width, Height,
            (x, y) => (byte)(255 - Upright(x, y)));

        var pngHash = await HashAsync(png);
        var jpegHash = await HashAsync(jpeg);
        var bmpHash = await HashAsync(bmp);
        var negativeHash = await HashAsync(negative);

        Distance(pngHash, bmpHash).Should().Be(0, "PNG and BMP carry the same pixels losslessly");
        Distance(pngHash, jpegHash).Should().BeLessThanOrEqualTo(4, "JPEG compression barely moves low frequencies");
        Distance(pngHash, negativeHash).Should().BeGreaterThan(ImagePHashStrategy.DefaultHammingThreshold);
    }

    [Fact]
    public async Task ExifOrientation_IsAppliedBeforeHashing()
    {
        // Stored rotated 90° counter-clockwise and tagged "rotate 90° clockwise to
        // display" (orientation 6), which is how a camera held upright on its side
        // writes a photo. Displayed, it is the upright picture.
        var upright = await WriteAsync("upright.jpg", BitmapEncoder.JpegEncoderId, Width, Height, Upright);
        var tagged = await WriteAsync("tagged.jpg", BitmapEncoder.JpegEncoderId, Height, Width, StoredSideways, orientation: 6);
        var untagged = await WriteAsync("untagged.jpg", BitmapEncoder.JpegEncoderId, Height, Width, StoredSideways);

        var uprightSignature = await SignAsync(upright);
        var taggedSignature = await SignAsync(tagged);
        var untaggedHash = await HashAsync(untagged);

        Distance(Parse(uprightSignature), Parse(taggedSignature)).Should().BeLessThanOrEqualTo(4,
            "the same photo stored in another orientation must hash the same");
        Distance(Parse(uprightSignature), untaggedHash).Should().BeGreaterThan(ImagePHashStrategy.DefaultHammingThreshold,
            "without the tag the stored pixels really are a different picture, so the test would prove nothing otherwise");

        using var metadata = JsonDocument.Parse(taggedSignature.MetadataJson!);
        metadata.RootElement.GetProperty("width").GetInt32().Should().Be(Width, "dimensions are reported as displayed");
        metadata.RootElement.GetProperty("height").GetInt32().Should().Be(Height);
        metadata.RootElement.GetProperty("orientation").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task UndecodableFile_BecomesAPerFileHashError()
    {
        var path = Path.Combine(_directory, "broken.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05]);

        var signature = await _strategy.ComputeSignatureAsync(Candidate(path));

        signature.Status.Should().Be("Error");
        signature.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DamagedFiles_NeverThrow_AndAreClosedAfterwards()
    {
        var jpeg = await File.ReadAllBytesAsync(
            await WriteAsync("source.jpg", BitmapEncoder.JpegEncoderId, Width, Height, Upright));
        var damaged = new (string Name, byte[] Bytes, bool MustFail)[]
        {
            ("empty.jpg", [], true),
            ("one-byte.jpg", [0xFF], true),
            ("header-only.jpg", jpeg[..Math.Min(200, jpeg.Length)], true),
            ("text.png", "not an image at all"u8.ToArray(), true),
            ("noise.webp", Enumerable.Range(0, 4096).Select(static i => (byte)(i * 7919 % 251)).ToArray(), true),
            // A cut-off JPEG may still decode, with the missing rows filled in; either
            // outcome is fine as long as it is a per-file result and not an exception.
            ("truncated.jpg", jpeg[..(jpeg.Length / 2)], false),
        };

        foreach (var (name, bytes, mustFail) in damaged)
        {
            var path = Path.Combine(_directory, name);
            await File.WriteAllBytesAsync(path, bytes);

            var signature = await _strategy.ComputeSignatureAsync(Candidate(path));

            signature.Status.Should().BeOneOf(["Ready", "Error"], name);
            if (mustFail)
                signature.Status.Should().Be("Error", name);

            // The decoder must not keep the file open: a later delete, move or
            // re-hash of the same file would otherwise fail.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }
    }

    [Fact]
    public async Task Identify_ReadsDisplayedDimensionsWithoutDecodingPixels()
    {
        var tagged = await WriteAsync("identify.jpg", BitmapEncoder.JpegEncoderId, Height, Width, StoredSideways, orientation: 6);

        await using var stream = File.OpenRead(tagged);
        var header = await new WicImageHashDecoder().IdentifyAsync(stream);

        header.Should().Be(new Core.Interfaces.ImageHeader(Width, Height, 6));
    }

    [Theory]
    [InlineData(255, 255, 255, 255)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 255, 0, 182)]   // Rec. 709: green carries most of the luma
    [InlineData(255, 0, 0, 54)]
    [InlineData(0, 0, 255, 18)]
    public void Luminance_UsesRec709Weights(byte red, byte green, byte blue, byte expected)
        => WicImageHashDecoder.ToLuminance([blue, green, red, 255]).Should().Equal(expected);

    [Fact]
    public void CoreCarriesNoImageCodecOfItsOwn()
    {
        typeof(ImagePHashStrategy).Assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .Should().NotContain(name => name != null && name.StartsWith("SixLabors", StringComparison.OrdinalIgnoreCase),
                "image decoding goes through the platform decoder, which Windows Update keeps patched");
    }

    [Fact]
    public async Task VideoFrame_RawLuminanceHashesLikeTheSameImage()
    {
        var png = await WriteAsync("frame-source.png", BitmapEncoder.PngEncoderId, 32, 32, Upright);
        await using var stream = File.OpenRead(png);
        var surface = await new WicImageHashDecoder().DecodeAsync(stream, ImagePHashStrategy.DctSize);
        var raw = Path.Combine(_directory, "frame_00.gray");
        await File.WriteAllBytesAsync(raw, surface.Luminance);

        VideoPHashStrategy.ComputeFrameHash(raw)
            .Should().Be(ImagePHashStrategy.ComputeHashFromLuminance(surface.Luminance));
    }

    [Fact]
    public async Task VideoFrame_ThatFfmpegDidNotWriteInFull_IsRejected()
    {
        var raw = Path.Combine(_directory, "frame_short.gray");
        await File.WriteAllBytesAsync(raw, new byte[ImagePHashStrategy.DctSize * ImagePHashStrategy.DctSize - 1]);

        var act = () => VideoPHashStrategy.ComputeFrameHash(raw);

        act.Should().Throw<InvalidDataException>();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A bright block in the top-left quarter over a left-to-right ramp: no rotational symmetry.</summary>
    private static byte Upright(int x, int y) =>
        x < Width / 2 && y < Height / 2
            ? (byte)230
            : (byte)(30 + (x * 120 / Width) + (y * 40 / Height));

    /// <summary><see cref="Upright"/> rotated 90° counter-clockwise: stored Height wide and Width tall.</summary>
    private static byte StoredSideways(int x, int y) => Upright(Width - 1 - y, x);

    private async Task<string> WriteAsync(
        string name,
        Guid encoderId,
        int width,
        int height,
        Func<int, int, byte> luminance,
        ushort? orientation = null)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = luminance(x, y);
                var offset = ((y * width) + x) * 4;
                bgra[offset] = value;
                bgra[offset + 1] = value;
                bgra[offset + 2] = value;
                bgra[offset + 3] = 255;
            }
        }

        var path = Path.Combine(_directory, name);
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        using (var stream = file.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)width,
                (uint)height,
                96,
                96,
                bgra);
            if (orientation is { } value)
            {
                await encoder.BitmapProperties.SetPropertiesAsync(
                [
                    new KeyValuePair<string, BitmapTypedValue>(
                        "System.Photo.Orientation",
                        new BitmapTypedValue(value, global::Windows.Foundation.PropertyType.UInt16)),
                ]);
            }

            await encoder.FlushAsync();
        }

        return path;
    }

    private async Task<DuplicateSignature> SignAsync(string path)
    {
        var signature = await _strategy.ComputeSignatureAsync(Candidate(path));
        signature.Status.Should().Be("Ready", signature.ErrorMessage);
        return signature;
    }

    private async Task<ulong> HashAsync(string path) => Parse(await SignAsync(path));

    private static ulong Parse(DuplicateSignature signature) =>
        ulong.Parse(signature.SignatureText!, System.Globalization.NumberStyles.HexNumber);

    private static int Distance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);

    private static DuplicateCandidate Candidate(string path)
    {
        var info = new FileInfo(path);
        return new DuplicateCandidate(
            new FileEntry
            {
                Id = 1,
                SessionId = 1,
                FullPath = path,
                FileName = info.Name,
                Extension = info.Extension,
                SizeBytes = info.Length,
                CreatedUtc = info.CreationTimeUtc,
                ModifiedUtc = info.LastWriteTimeUtc,
                AccessedUtc = info.LastAccessTimeUtc,
                Attributes = info.Attributes,
                Category = FileTypeCategory.Image,
            },
            null);
    }
}
