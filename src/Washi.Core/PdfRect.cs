// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Core;

/// <summary>
/// Axis-aligned rectangle in PDF points, top-down (y grows downwards), measured on the
/// upright page as the user sees it. Every layer above Core uses this coordinate system;
/// conversion to bottom-up PDF user space happens only when objects are written.
/// </summary>
public readonly record struct PdfRect(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    public (double X, double Y) Center => ((X0 + X1) / 2, (Y0 + Y1) / 2);

    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;

    public PdfRect Intersect(PdfRect o) =>
        new(Math.Max(X0, o.X0), Math.Max(Y0, o.Y0), Math.Min(X1, o.X1), Math.Min(Y1, o.Y1));

    public double IoU(PdfRect o)
    {
        var i = Intersect(o).Area;
        return i <= 0 ? 0 : i / (Area + o.Area - i);
    }
}
