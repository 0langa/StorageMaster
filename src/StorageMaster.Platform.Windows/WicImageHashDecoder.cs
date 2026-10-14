using StorageMaster.Core.Interfaces;
using Windows.Graphics.Imaging;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// <see cref="IImageHashDecoder"/> on the Windows Imaging Component, through
/// <c>Windows.Graphics.Imaging</c>.
/// <para>
/// These are the decoders File Explorer uses for thumbnails. Windows Update keeps
/// them patched, and any codec the user has installed (HEIF, WebP, camera raw) works
/// without the app shipping one. That replaced ImageSharp, whose 3.x line stopped
/// receiving security fixes.
/// </para>
/// <para>
/// The scale-down happens inside the decode: WIC asks the codec for a reduced
/// image where it can (JPEG decodes straight from its scaled IDCT), so a 50-megapixel
/// photo is never materialised at full size just to become 32×32.
/// </para>
/// </summary>
public sealed class WicImageHashDecoder : IImageHashDecoder
{
    private const string OrientationProperty = "System.Photo.Orientation";

    public async Task<ImageHashSurface> DecodeAsync(Stream stream, int size, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        using var randomAccess = stream.AsRandomAccessStream();
        var decoder = await OpenAsync(randomAccess, ct).ConfigureAwait(false);
        var header = await ReadHeaderAsync(decoder, ct).ConfigureAwait(false);

        // Stretch-to-square with Fant (area-averaging) resampling, EXIF orientation
        // applied, straight alpha so transparent pixels keep the colour they store,
        // and no colour management: the hash compares structure, not appearance.
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)size,
            ScaledHeight = (uint)size,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider pixelData;
        try
        {
            pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw DecodeFailure("decode the pixels of", ex);
        }

        var bgra = pixelData.DetachPixelData();
        if (bgra.Length != size * size * 4)
        {
            throw new InvalidDataException(
                $"The image decoder returned {bgra.Length} bytes for a {size}x{size} BGRA surface.");
        }

        return new ImageHashSurface(ToLuminance(bgra), size, header);
    }

    public async Task<ImageHeader> IdentifyAsync(Stream stream, CancellationToken ct = default)
    {
        using var randomAccess = stream.AsRandomAccessStream();
        var decoder = await OpenAsync(randomAccess, ct).ConfigureAwait(false);
        return await ReadHeaderAsync(decoder, ct).ConfigureAwait(false);
    }

    private static async Task<BitmapDecoder> OpenAsync(
        global::Windows.Storage.Streams.IRandomAccessStream stream,
        CancellationToken ct)
    {
        try
        {
            return await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw DecodeFailure("read", ex);
        }
    }

    /// <summary>
    /// WinRT decode failures often carry an empty message, which would surface as a
    /// blank per-file error. Name the failure and keep the HRESULT for diagnosis.
    /// </summary>
    private static InvalidDataException DecodeFailure(string action, Exception inner)
    {
        var detail = string.IsNullOrWhiteSpace(inner.Message) ? string.Empty : " " + inner.Message.Trim();
        return new InvalidDataException(
            $"Windows could not {action} this image (HRESULT 0x{inner.HResult:X8}).{detail}",
            inner);
    }

    /// <summary>
    /// Rec. 709 luma, the weighting the hash was computed with before. Integer
    /// arithmetic (coefficients scaled by 2^16) keeps the result identical on every
    /// machine.
    /// </summary>
    internal static byte[] ToLuminance(ReadOnlySpan<byte> bgra)
    {
        var luminance = new byte[bgra.Length / 4];
        for (int pixel = 0, offset = 0; pixel < luminance.Length; pixel++, offset += 4)
        {
            var b = bgra[offset];
            var g = bgra[offset + 1];
            var r = bgra[offset + 2];
            luminance[pixel] = (byte)(((13933 * r) + (46871 * g) + (4732 * b) + 32768) >> 16);
        }

        return luminance;
    }

    private static async Task<ImageHeader> ReadHeaderAsync(BitmapDecoder decoder, CancellationToken ct)
    {
        var orientation = await ReadOrientationAsync(decoder, ct).ConfigureAwait(false);
        return new ImageHeader(
            checked((int)decoder.OrientedPixelWidth),
            checked((int)decoder.OrientedPixelHeight),
            orientation);
    }

    private static async Task<ushort> ReadOrientationAsync(BitmapDecoder decoder, CancellationToken ct)
    {
        try
        {
            var properties = await decoder.BitmapProperties
                .GetPropertiesAsync([OrientationProperty])
                .AsTask(ct)
                .ConfigureAwait(false);
            if (properties.TryGetValue(OrientationProperty, out var value) &&
                value.Value is ushort orientation &&
                orientation is >= 1 and <= 8)
            {
                return orientation;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Formats without a metadata block (BMP, most PNGs) report the property as
            // unsupported rather than absent. Either way the image is upright.
        }

        return 1;
    }
}
