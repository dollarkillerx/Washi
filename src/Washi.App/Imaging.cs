// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Washi.Core;

namespace Washi.App;

public static class Imaging
{
    /// <summary>
    /// RGB raster → Avalonia bitmap at 96 DPI, so its Size equals its PixelSize. The page is
    /// always drawn into an explicit rectangle; a "true" DPI would make Size smaller than the
    /// pixel grid and source rectangles would crop the page.
    /// </summary>
    public static WriteableBitmap ToBitmap(RgbImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        var row = new byte[img.Width * 4];
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0, s = y * img.Width * 3; x < img.Width; x++, s += 3)
            {
                row[x * 4] = img.Pixels[s + 2];
                row[x * 4 + 1] = img.Pixels[s + 1];
                row[x * 4 + 2] = img.Pixels[s];
                row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bmp;
    }
}
