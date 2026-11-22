// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Core.Forms;

public enum FormFieldKind { Text, Amount, Comb, Checkbox, Radio, Choice, Signature }

/// <summary>The typeface of a field's text. Auto is the form language's usual face (Mincho / Song / Myeongjo for
/// CJK, Helvetica for Latin). When a font file is embedded, every field uses that font.</summary>
public enum FormFontFace { Auto, Serif, Sans }

/// <summary>A field to write into a PDF, on the upright page (top-down points).</summary>
/// <param name="FontSize">Points; 0 = sized to the field (and a value that is too long is shrunk to fit).</param>
/// <param name="Color">"#RRGGBB"; null or empty = black.</param>
/// <param name="Export">Radio buttons: the option this button stands for (its label, e.g. "有"). Buttons with the same
/// <paramref name="Name"/> form one group, of which one option can be chosen.</param>
/// <param name="Options">Drop-down lists (<see cref="FormFieldKind.Choice"/>): the items to choose from.</param>
public sealed record FormField(
    int Page,
    FormFieldKind Kind,
    PdfRect Rect,
    string Name,
    int MaxLen = 0,
    int Align = 0,
    string Tooltip = "",
    double FontSize = 0,
    string? Color = null,
    FormFontFace Font = FormFontFace.Auto,
    string Export = "",
    IReadOnlyList<string>? Options = null)
{
    /// <summary>The size a field gets when none is chosen: from its height, between 5 and 10 pt.</summary>
    public static double NominalSize(double fieldHeight) => Math.Round(Math.Clamp(fieldHeight * 0.6, 5, 10), 1);

    /// <summary>PDF colour operators for <see cref="Color"/> ("0 g" for black or anything unreadable).</summary>
    public static string ColorOps(string? color)
    {
        if (color is { Length: 7 } c && c[0] == '#' && int.TryParse(c.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb) && rgb != 0)
        {
            static string F(int v) => (v / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return $"{F((rgb >> 16) & 255)} {F((rgb >> 8) & 255)} {F(rgb & 255)} rg";
        }
        return "0 g";
    }
}
