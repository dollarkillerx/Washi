// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Core;

namespace Washi.Masume;

/// <summary>
/// Review image: the page with detected fields painted on top. Same colour code as the
/// prototype: blue text, purple amount, orange comb, green checkbox, red dashed = already
/// holds printed data, grey = shaded cell. Real fields (when scoring) are red outlines.
/// </summary>
public static class MasumeOverlay
{
    private static readonly Dictionary<FieldKind, (byte R, byte G, byte B)> Colors = new()
    {
        [FieldKind.Text] = (26, 89, 242),
        [FieldKind.Amount] = (140, 51, 217),
        [FieldKind.Comb] = (242, 128, 13),
        [FieldKind.Checkbox] = (13, 166, 64),
        [FieldKind.Radio] = (0, 125, 121),
        [FieldKind.Choice] = (0, 99, 168),
        [FieldKind.Signature] = (138, 63, 0),
    };

    public static RgbImage Draw(PageSnapshot page, IEnumerable<DetectedField> fields,
                                IEnumerable<CellInfo>? cells = null, IEnumerable<PdfRect>? truth = null)
    {
        var src = page.Full;
        var px = (byte[])src.Pixels.Clone();
        var img = new RgbImage(src.Width, src.Height, px);
        double s = page.Scale;

        foreach (var c in cells ?? [])
            if (c.Kind == "shaded") Fill(img, c.Rect, s, (128, 128, 128), 0.25);

        foreach (var f in fields)
        {
            var col = Colors[f.Kind];
            Fill(img, f.Rect, s, col, 0.18);
            Outline(img, f.Rect, s, col, dashed: false);
            if (f.Kind == FieldKind.Comb && f.MaxLen > 1)
            {
                double cw = f.Rect.Width / f.MaxLen;
                for (int i = 1; i < f.MaxLen; i++)
                {
                    double x = f.Rect.X0 + i * cw;
                    Outline(img, new PdfRect(x, f.Rect.Y1 - 3, x, f.Rect.Y1), s, col, dashed: false);
                }
            }
            if (f.Prefilled.Length > 0)
                Outline(img, new PdfRect(f.Rect.X0 - 0.8, f.Rect.Y0 - 0.8, f.Rect.X1 + 0.8, f.Rect.Y1 + 0.8), s, (230, 26, 26), dashed: true);
        }

        foreach (var t in truth ?? [])
            Outline(img, t, s, (255, 0, 0), dashed: false);
        return img;
    }

    private static void Fill(RgbImage img, PdfRect r, double s, (byte R, byte G, byte B) c, double alpha)
    {
        int x0 = Clamp((int)(r.X0 * s), img.Width), x1 = Clamp((int)(r.X1 * s), img.Width);
        int y0 = Clamp((int)(r.Y0 * s), img.Height), y1 = Clamp((int)(r.Y1 * s), img.Height);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * img.Width + x) * 3;
                img.Pixels[i] = (byte)(img.Pixels[i] * (1 - alpha) + c.R * alpha);
                img.Pixels[i + 1] = (byte)(img.Pixels[i + 1] * (1 - alpha) + c.G * alpha);
                img.Pixels[i + 2] = (byte)(img.Pixels[i + 2] * (1 - alpha) + c.B * alpha);
            }
    }

    private static void Outline(RgbImage img, PdfRect r, double s, (byte R, byte G, byte B) c, bool dashed)
    {
        int x0 = (int)(r.X0 * s), x1 = (int)(r.X1 * s), y0 = (int)(r.Y0 * s), y1 = (int)(r.Y1 * s);
        for (int x = x0; x <= x1; x++) { Put(x, y0); Put(x, y1); }
        for (int y = y0; y <= y1; y++) { Put(x0, y); Put(x1, y); }

        void Put(int x, int y)
        {
            if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return;
            if (dashed && ((x + y) / 6) % 2 == 1) return;
            int i = (y * img.Width + x) * 3;
            img.Pixels[i] = c.R; img.Pixels[i + 1] = c.G; img.Pixels[i + 2] = c.B;
        }
    }

    private static int Clamp(int v, int max) => Math.Clamp(v, 0, max);
}
