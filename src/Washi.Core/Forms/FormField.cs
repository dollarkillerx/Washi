// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Core.Forms;

public enum FormFieldKind { Text, Amount, Comb, Checkbox }

/// <summary>A field to write into a PDF, on the upright page (top-down points).</summary>
public sealed record FormField(
    int Page,
    FormFieldKind Kind,
    PdfRect Rect,
    string Name,
    int MaxLen = 0,
    int Align = 0,
    string Tooltip = "");
