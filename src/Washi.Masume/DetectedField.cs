// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Washi.Core;

namespace Washi.Masume;

[JsonConverter(typeof(JsonStringEnumConverter<FieldKind>))]
public enum FieldKind { Text, Amount, Comb, Checkbox }

/// <summary>
/// One field suggested by the detector. The JSON shape is shared with the Python reference
/// implementation in <c>prototype/</c> (its <c>*_fields.json</c> output), so both can be
/// compared field by field.
/// </summary>
public sealed record DetectedField
{
    public int Page { get; init; }
    public FieldKind Kind { get; init; }
    public PdfRect Rect { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Comb fields: number of character cells. JSON name matches the PDF /MaxLen key.</summary>
    [JsonPropertyName("maxlen")]
    public int MaxLen { get; init; }
    /// <summary>0 left, 1 centre, 2 right (PDF /Q).</summary>
    public int Align { get; init; }
    /// <summary>Data already printed inside the cell, if any.</summary>
    public string Prefilled { get; init; } = "";
    public string Label { get; init; } = "";
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Text size in points; 0 = fitted to the field.</summary>
    public double FontSize { get; init; }
    /// <summary>"#RRGGBB"; empty = black.</summary>
    public string Color { get; init; } = "";
    /// <summary>"" (the form language's usual face), "serif" or "sans".</summary>
    public string Font { get; init; } = "";

    public Washi.Core.Forms.FormField ToFormField() => new(
        Page,
        Kind switch
        {
            FieldKind.Amount => Washi.Core.Forms.FormFieldKind.Amount,
            FieldKind.Comb => Washi.Core.Forms.FormFieldKind.Comb,
            FieldKind.Checkbox => Washi.Core.Forms.FormFieldKind.Checkbox,
            _ => Washi.Core.Forms.FormFieldKind.Text,
        },
        Rect, Name, MaxLen, Align, Label, FontSize, Color.Length > 0 ? Color : null,
        Font switch { "serif" => Washi.Core.Forms.FormFontFace.Serif, "sans" => Washi.Core.Forms.FormFontFace.Sans, _ => Washi.Core.Forms.FormFontFace.Auto });
}

public static class FieldsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new RectArrayConverter(), new JsonStringEnumConverter<FieldKind>(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = true,
    };

    public static IReadOnlyList<DetectedField> Parse(string json) =>
        JsonSerializer.Deserialize<List<DetectedField>>(json, Options) ?? [];

    public static string Serialize(IEnumerable<DetectedField> fields) =>
        JsonSerializer.Serialize(fields, Options);

    /// <summary>Rects are stored as <c>[x0, y0, x1, y1]</c>.</summary>
    private sealed class RectArrayConverter : JsonConverter<PdfRect>
    {
        public override PdfRect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = JsonSerializer.Deserialize<double[]>(ref reader, options);
            if (v is not { Length: 4 }) throw new JsonException("rect must be [x0, y0, x1, y1]");
            return new PdfRect(v[0], v[1], v[2], v[3]);
        }

        public override void Write(Utf8JsonWriter writer, PdfRect r, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new[] { r.X0, r.Y0, r.X1, r.Y1 }, options);
    }
}
