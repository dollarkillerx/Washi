// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Core;

/// <summary>24-bit RGB raster, row-major, no padding.</summary>
public sealed class RgbImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    /// <summary>R, G, B per pixel.</summary>
    public byte[] Pixels { get; } = pixels.Length == width * height * 3
        ? pixels : throw new ArgumentException("pixel buffer size mismatch", nameof(pixels));

    /// <summary>Luminance 0.299 R + 0.587 G + 0.114 B, truncated (same as the Python prototype).</summary>
    public byte[] ToGray()
    {
        var g = new byte[Width * Height];
        for (int i = 0, p = 0; i < g.Length; i++, p += 3)
            g[i] = (byte)(0.299 * Pixels[p] + 0.587 * Pixels[p + 1] + 0.114 * Pixels[p + 2]);
        return g;
    }
}

/// <summary>One character from the text layer, on the upright page (top-down points).</summary>
public readonly record struct PdfChar(string Text, PdfRect Rect, int Rgb);

/// <summary>An existing form widget (used to score detection against hand-made forms).</summary>
public readonly record struct PdfWidget(PdfRect Rect, int FieldType);

/// <summary>Everything the form detector needs from one page.</summary>
public sealed record PageSnapshot(
    int PageIndex,
    double Width,
    double Height,
    double Scale,
    IReadOnlyList<PdfChar> Chars,
    RgbImage Full,
    RgbImage GraphicsOnly,
    IReadOnlyList<PdfWidget> Widgets);
