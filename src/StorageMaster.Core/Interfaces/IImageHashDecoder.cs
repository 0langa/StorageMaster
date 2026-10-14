namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Decodes images for the perceptual image hash and the duplicate preview.
/// <para>
/// Supplied by the platform — the Windows Imaging Component on Windows, the same
/// decoders File Explorer uses, kept current by Windows Update — so Core carries no
/// image codec of its own. Formats Windows has no codec for (HEIC without the HEIF
/// extension, for example) fail to decode and surface as a per-file hash error.
/// </para>
/// </summary>
public interface IImageHashDecoder
{
    /// <summary>
    /// Decodes the first frame of <paramref name="stream"/>, applies its EXIF
    /// orientation and scales it to <paramref name="size"/>×<paramref name="size"/>
    /// 8-bit luminance, row-major. Throws when the data cannot be decoded.
    /// </summary>
    Task<ImageHashSurface> DecodeAsync(Stream stream, int size, CancellationToken ct = default);

    /// <summary>Reads the image's dimensions and orientation from its header without decoding pixels.</summary>
    Task<ImageHeader> IdentifyAsync(Stream stream, CancellationToken ct = default);
}

/// <param name="Width">Pixel width as displayed, i.e. after EXIF orientation.</param>
/// <param name="Height">Pixel height as displayed, i.e. after EXIF orientation.</param>
/// <param name="Orientation">EXIF orientation tag, 1 (upright) when absent.</param>
public sealed record ImageHeader(int Width, int Height, ushort Orientation);

/// <param name="Luminance">Size×Size 8-bit luminance, row-major.</param>
public sealed record ImageHashSurface(byte[] Luminance, int Size, ImageHeader Header);
