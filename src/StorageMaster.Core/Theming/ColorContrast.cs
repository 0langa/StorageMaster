namespace StorageMaster.Core.Theming;

/// <summary>
/// WCAG 2.1 relative-luminance and contrast-ratio maths.
/// <para>
/// The theme system is meant to grow: new accents will be added over time. Every
/// accent therefore has to prove it is readable rather than merely look right to
/// whoever added it, so these helpers back an automated contrast test instead of
/// living in a design document.
/// </para>
/// </summary>
public static class ColorContrast
{
    /// <summary>Minimum ratio for normal body text (WCAG 1.4.3 AA).</summary>
    public const double MinimumTextRatio = 4.5;

    /// <summary>Minimum ratio for large text and for UI/graphical objects (WCAG 1.4.11).</summary>
    public const double MinimumGraphicRatio = 3.0;

    /// <summary>Relative luminance of an sRGB colour, per WCAG 2.1.</summary>
    public static double RelativeLuminance(Rgb color)
    {
        static double Channel(byte raw)
        {
            var value = raw / 255.0;
            return value <= 0.040_45
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R))
             + (0.7152 * Channel(color.G))
             + (0.0722 * Channel(color.B));
    }

    /// <summary>
    /// Contrast ratio between two opaque colours. Always returns a value in
    /// [1, 21] and is order independent.
    /// </summary>
    public static double Ratio(Rgb first, Rgb second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }
}

/// <summary>An opaque 8-bit-per-channel sRGB colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses "#RRGGBB". Kept strict so a malformed token fails loudly.</summary>
    public static Rgb Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        var span = hex.AsSpan().Trim();
        if (span.Length == 7 && span[0] == '#')
            span = span[1..];

        if (span.Length != 6)
            throw new FormatException($"Expected a colour of the form #RRGGBB but got '{hex}'.");

        return new Rgb(
            byte.Parse(span[..2], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(span[2..4], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(span[4..], System.Globalization.NumberStyles.HexNumber));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
