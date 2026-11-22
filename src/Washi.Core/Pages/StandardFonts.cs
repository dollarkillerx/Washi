// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Core.Pages;

/// <summary>
/// Glyph widths of the 14 standard fonts, for PDFs that use them without a /Widths array (the reader is expected to
/// know them). Names written by other programs (ArialMT, TimesNewRomanPS-BoldMT, CourierNew) map to the face with the
/// same widths.
/// </summary>
internal static partial class StandardFonts
{
    internal sealed record Face(string Name, double Ascent, double Descent, IReadOnlyDictionary<string, int> Widths,
                                IReadOnlyDictionary<int, string>? BuiltIn, int? Fixed);

    private static readonly Lazy<Dictionary<string, Face>> All = new(() =>
    {
        var d = new Dictionary<string, Face>(StringComparer.Ordinal);
        foreach (var (name, asc, desc, widths, builtIn) in Faces)
        {
            var w = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in widths.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (p.LastIndexOf(':') is var i and > 0) w[p[..i]] = int.Parse(p[(i + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            Dictionary<int, string>? b = null;
            if (builtIn.Length > 0)
            {
                b = [];
                foreach (var p in builtIn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (p.IndexOf(':') is var i and > 0) b[int.Parse(p[..i], System.Globalization.CultureInfo.InvariantCulture)] = p[(i + 1)..];
            }
            d[name] = new Face(name, asc == 0 ? 0.8 : asc / 1000.0, desc == 0 ? -0.2 : desc / 1000.0, w, b, null);
        }
        var courier = new Face("Courier", 0.629, -0.157, d["Helvetica"].Widths, null, 600);
        d["Courier"] = courier;
        return d;
    });

    /// <summary>The standard face <paramref name="baseFont"/> stands for, or null when it is not one of them.</summary>
    public static Face? Find(string? baseFont)
    {
        if (string.IsNullOrEmpty(baseFont)) return null;
        var n = baseFont.TrimStart('/');
        if (n.Length > 7 && n[6] == '+') n = n[7..];   // subset prefix ABCDEF+
        var plain = n.Replace(" ", "").Replace(",", "-");
        bool bold = plain.Contains("Bold", StringComparison.OrdinalIgnoreCase) || plain.Contains("Black", StringComparison.OrdinalIgnoreCase);
        bool italic = plain.Contains("Italic", StringComparison.OrdinalIgnoreCase) || plain.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        var lower = plain.ToLowerInvariant();
        string? face =
            lower.StartsWith("symbol", StringComparison.Ordinal) ? "Symbol" :
            lower.StartsWith("zapfdingbats", StringComparison.Ordinal) ? "ZapfDingbats" :
            lower.StartsWith("courier", StringComparison.Ordinal) ? "Courier" :
            lower.StartsWith("helvetica", StringComparison.Ordinal) || lower.StartsWith("arial", StringComparison.Ordinal) && !lower.Contains("narrow") && !lower.Contains("unicode")
                ? (bold ? "Helvetica-Bold" : "Helvetica") :
            lower.StartsWith("times", StringComparison.Ordinal)
                ? (bold && italic ? "Times-BoldItalic" : bold ? "Times-Bold" : italic ? "Times-Italic" : "Times-Roman") :
            null;
        return face is null ? null : All.Value[face];
    }

    public static IReadOnlyList<string> Standard => StandardNames;
    public static IReadOnlyList<string> WinAnsi => WinAnsiNames;
    public static IReadOnlyList<string> MacRoman => MacRomanNames;
}
