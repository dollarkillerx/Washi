// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Core;
using Washi.Masume;

namespace Washi.Masume.Tests;

public class FieldsJsonTests
{
    // Same shape as prototype/autoform output (*_fields.json).
    private const string PrototypeOutput = """
        [
         {"page": 0, "kind": "comb", "rect": [390.1, 78.5, 531.7, 97.0], "name": "あなたの個人番号",
          "maxlen": 12, "align": 1, "prefilled": "", "label": "", "notes": []},
         {"page": 2, "kind": "amount", "rect": [231.0, 146.0, 310.5, 170.0], "name": "p3_amount_011",
          "maxlen": 0, "align": 2, "prefilled": "", "label": "円", "notes": []},
         {"page": 2, "kind": "checkbox", "rect": [94.4, 232.3, 100.4, 238.1], "name": "判定1",
          "maxlen": 0, "align": 0, "prefilled": "", "label": "", "notes": ["□ glyph"]}
        ]
        """;

    [Fact]
    public void Reads_prototype_output()
    {
        var fields = FieldsJson.Parse(PrototypeOutput);

        Assert.Equal(3, fields.Count);
        Assert.Equal(FieldKind.Comb, fields[0].Kind);
        Assert.Equal(12, fields[0].MaxLen);
        Assert.Equal(new PdfRect(390.1, 78.5, 531.7, 97.0), fields[0].Rect);
        Assert.Equal(FieldKind.Amount, fields[1].Kind);
        Assert.Equal(2, fields[1].Align);
        Assert.Equal(["□ glyph"], fields[2].Notes);
    }

    [Fact]
    public void Round_trips()
    {
        var fields = FieldsJson.Parse(PrototypeOutput);
        var again = FieldsJson.Parse(FieldsJson.Serialize(fields));

        Assert.Equal(fields.Select(f => (f.Kind, f.Rect, f.Name, f.MaxLen)),
                     again.Select(f => (f.Kind, f.Rect, f.Name, f.MaxLen)));
    }

    [Theory]
    [InlineData(0, 0, 10, 10, 5, 5, 15, 15, 25.0 / 175.0)]
    [InlineData(0, 0, 10, 10, 20, 20, 30, 30, 0.0)]
    public void IoU(double a0, double a1, double a2, double a3, double b0, double b1, double b2, double b3, double expected) =>
        Assert.Equal(expected, new PdfRect(a0, a1, a2, a3).IoU(new PdfRect(b0, b1, b2, b3)), 6);
}
