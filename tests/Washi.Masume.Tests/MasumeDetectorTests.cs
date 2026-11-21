// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Core;
using Washi.Masume;

namespace Washi.Masume.Tests;

/// <summary>Synthetic pages: drawn with plain pixel code, no PDF needed.</summary>
public class MasumeDetectorTests
{
    private const double S = MasumeDetector.Scale;

    private sealed class Canvas(double wPt, double hPt)
    {
        public readonly int W = (int)(wPt * S), H = (int)(hPt * S);
        public readonly byte[] Px = Enumerable.Repeat((byte)255, (int)(wPt * S) * (int)(hPt * S) * 3).ToArray();

        public Canvas Rect(double x0, double y0, double x1, double y1)
        {
            HLine(x0, x1, y0); HLine(x0, x1, y1); VLine(x0, y0, y1); VLine(x1, y0, y1);
            return this;
        }

        public Canvas VLine(double x, double y0, double y1)
        {
            int px = (int)(x * S);
            for (int y = (int)(y0 * S); y <= (int)(y1 * S); y++) Dot(px, y);
            return this;
        }

        public Canvas HLine(double x0, double x1, double y)
        {
            int py = (int)(y * S);
            for (int x = (int)(x0 * S); x <= (int)(x1 * S); x++) Dot(x, py);
            return this;
        }

        private void Dot(int x, int y, int rgb = 0)
        {
            int i = (y * W + x) * 3;
            Px[i] = (byte)(rgb >> 16); Px[i + 1] = (byte)(rgb >> 8); Px[i + 2] = (byte)rgb;
        }

        /// <summary>A filled block in one colour: stands in for a glyph drawn as a path.</summary>
        public Canvas Blob(double x0, double y0, double x1, double y1, int rgb)
        {
            for (int y = (int)(y0 * S); y < (int)(y1 * S); y++)
                for (int x = (int)(x0 * S); x < (int)(x1 * S); x++) Dot(x, y, rgb);
            return this;
        }

        public PageSnapshot Snapshot(params PdfChar[] chars)
        {
            var img = new RgbImage(W, H, Px);
            return new PageSnapshot(0, W / S, H / S, S, chars, img, img, []);
        }
    }

    private static PdfChar Ch(string c, double x0, double y0, double size = 8, int rgb = 0x000000) =>
        new(c, new PdfRect(x0, y0, x0 + size, y0 + size), rgb);

    /// <summary>Three glyph-sized marks drawn as graphics in the cell 60..120 × 10..40, unevenly spaced (no comb).</summary>
    private static Canvas DrawnMarks(Canvas c, int rgb) =>
        c.Blob(66, 20, 71, 23, rgb).Blob(80, 26, 85, 29, rgb).Blob(100, 18, 105, 21, rgb);

    [Fact]
    public void Empty_cell_next_to_label_becomes_named_text_field()
    {
        var page = new Canvas(600, 400)
            .Rect(10, 10, 190, 40).VLine(60, 10, 40)
            .Snapshot(Ch("氏", 20, 20), Ch("名", 30, 20));

        var fields = MasumeDetector.Detect(page);

        var f = Assert.Single(fields);
        Assert.Equal(FieldKind.Text, f.Kind);
        Assert.Equal("氏名", f.Name);
        Assert.InRange(f.Rect.X0, 59, 61.5);
        Assert.InRange(f.Rect.X1, 189, 191);
    }

    [Fact]
    public void Yen_mark_in_corner_makes_right_aligned_amount_field()
    {
        var page = new Canvas(600, 400)
            .Rect(10, 10, 190, 40).VLine(60, 10, 40)
            .Snapshot(Ch("所", 20, 20), Ch("得", 30, 20), Ch("円", 180, 31));

        var f = Assert.Single(MasumeDetector.Detect(page));

        Assert.Equal(FieldKind.Amount, f.Kind);
        Assert.Equal(2, f.Align);
        Assert.True(f.Rect.X1 <= 180, "field must stop left of the 円 mark");
    }

    [Fact]
    public void Short_ticks_inside_a_cell_make_a_comb_field()
    {
        var c = new Canvas(600, 400).Rect(10, 10, 130, 40);
        for (int i = 1; i < 12; i++) c.VLine(10 + i * 10, 32, 40);   // 11 dividers -> 12 boxes

        var f = Assert.Single(MasumeDetector.Detect(c.Snapshot()));

        Assert.Equal(FieldKind.Comb, f.Kind);
        Assert.Equal(12, f.MaxLen);
    }

    [Theory]
    [InlineData(0x028559, 0x80C2AC, "")]                           // a lighter tint of the green label colour
    [InlineData(0x000000, 0x404040, "")]                           // black headings come out grey once anti-aliased
    [InlineData(0x028559, 0x000000, MasumeDetector.DrawnMarker)]   // black glyphs on a green form: written in
    [InlineData(0x000000, 0xD03030, MasumeDetector.DrawnMarker)]   // red glyphs on a black form: written in
    public void Drawn_marks_in_the_form_colour_are_not_prefilled(int labelRgb, int inkRgb, string prefilled)
    {
        var c = DrawnMarks(new Canvas(600, 400).Rect(10, 10, 120, 40).VLine(60, 10, 40), inkRgb);

        var f = Assert.Single(MasumeDetector.Detect(c.Snapshot(Ch("氏", 20, 20, rgb: labelRgb), Ch("名", 30, 20, rgb: labelRgb))));

        Assert.Equal(FieldKind.Text, f.Kind);
        Assert.Equal(prefilled, f.Prefilled);
    }

    [Theory]
    [InlineData(0x404040, "")]
    [InlineData(0xD03030, MasumeDetector.DrawnMarker)]
    public void Without_a_text_layer_the_rulings_give_the_form_colour(int inkRgb, string prefilled)
    {
        // every word of the form drawn as outlines (国税庁 PDFs): no text colour to compare with, so the page's
        // commonest ink (black rulings and frame) is the form's own
        var c = DrawnMarks(new Canvas(600, 400).Rect(5, 5, 595, 395).Rect(60, 10, 120, 40), inkRgb);

        var f = Assert.Single(MasumeDetector.Detect(c.Snapshot()));

        Assert.Equal(prefilled, f.Prefilled);
    }

    [Fact]
    public void Digit_groups_split_by_tall_lines_are_still_one_comb()
    {
        // 法人番号 on the 扶養控除等申告書: a note along the top, then 13 boxes in groups of 1 + 4 + 4 + 4. The group
        // separators are as tall as the row under the note; the other dividers are short ticks.
        var c = new Canvas(600, 400).Rect(10, 10, 140, 40);
        for (int i = 1; i <= 12; i++) c.VLine(10 + i * 10, i is 1 or 5 or 9 ? 16 : 32, 40);

        var f = Assert.Single(MasumeDetector.Detect(c.Snapshot(Ch("※", 12, 11, 4), Ch("記", 17, 11, 4))));

        Assert.Equal(FieldKind.Comb, f.Kind);
        Assert.Equal(13, f.MaxLen);
    }

    [Fact]
    public void A_date_cell_gets_one_field_before_each_mark()
    {
        // ＿＿年＿＿月＿＿日: a value typed into one wide field would land on the printed marks
        var c = new Canvas(600, 400).Rect(10, 10, 130, 30);
        var fields = MasumeDetector.Detect(c.Snapshot(Ch("年", 50, 16), Ch("月", 85, 16), Ch("日", 118, 16)));

        Assert.Equal(3, fields.Count);
        Assert.All(fields, f => Assert.Equal(FieldKind.Text, f.Kind));
        Assert.Equal(["年", "月", "日"], fields.OrderBy(f => f.Rect.X0).Select(f => f.Label));
        Assert.All(fields, f => Assert.Equal(2, f.Align));                       // the value sits next to its mark
        Assert.True(fields.OrderBy(f => f.Rect.X0).First().Rect.X1 <= 50);      // and ends before it

        // a spaced-out heading is not split at its characters, and a word at the right end is one field as before
        Assert.True(MasumeDetector.Detect(new Canvas(600, 400).Rect(10, 10, 130, 30).Snapshot(Ch("氏", 20, 16), Ch("名", 110, 16))).Count <= 1);
        Assert.Single(MasumeDetector.Detect(new Canvas(600, 400).Rect(10, 10, 130, 30)
            .Snapshot(Ch("税", 90, 16), Ch("務", 98, 16), Ch("署", 106, 16), Ch("長", 114, 16))));
    }

    /// <summary>A right-aligned number ending at x = 48 on the line at y (a wage-bracket table cell, 6 pt digits).</summary>
    private static IEnumerable<PdfChar> Flush(string s, double y) => s.Select((ch, i) => Ch(ch.ToString(), 48 - 6 * (s.Length - i), y, 6));

    [Fact]
    public void A_column_of_printed_values_is_a_table_not_a_field()
    {
        // four lines: before, the right-aligned stack read as "＿税務署長" and the whole cell became a field
        var four = new[] { "$375", "$385", "$395", "$405" }.SelectMany((v, i) => Flush(v, 12 + 10 * i)).ToArray();
        Assert.Empty(MasumeDetector.Detect(new Canvas(600, 400).Rect(10, 10, 50, 56).Snapshot(four)));
        // three short lines: before, the empty left part became a field ("a few marks scattered")
        var three = new[] { "$0", "$1", "$3" }.SelectMany((v, i) => Flush(v, 12 + 10 * i)).ToArray();
        Assert.Empty(MasumeDetector.Detect(new Canvas(600, 400).Rect(10, 10, 50, 46).Snapshot(three)));
    }

    /// <summary>Text as 8 pt chars from (x, y), in red: the minority colour, so the detector sees it as data.</summary>
    private static IEnumerable<PdfChar> Red(string s, double x, double y) => s.Select((ch, i) => Ch(ch.ToString(), x + 8 * i, y, rgb: 0xFF0000));

    /// <summary>Black text outside any cell, so black is the page's label colour.</summary>
    private static IEnumerable<PdfChar> BlackNotes() => Enumerable.Range(0, 40).Select(i => Ch("注", 10 + 8 * (i % 20), 300 + 10 * (i / 20)));

    [Fact]
    public void A_paragraph_in_a_cell_is_printed_explanation_not_a_filled_in_value()
    {
        // a guide's explanation box: before, the whole paragraph became one prefilled field, and "make editable"
        // erased it from the page and moved it into that field
        var para = Red("これは説明の文です。", 14, 14).Concat(Red("次の欄に記入します。", 14, 26)).Concat(Red("以上です", 14, 38));
        var page = new Canvas(600, 400).Rect(10, 10, 200, 60).Snapshot([.. para, .. BlackNotes()]);
        Assert.Empty(MasumeDetector.Detect(page));
    }

    [Fact]
    public void A_filled_in_address_on_two_lines_stays_a_prefilled_value()
    {
        var addr = Red("東京都千代田区一丁目", 14, 14).Concat(Red("二番三号", 14, 26));
        var page = new Canvas(600, 400).Rect(10, 10, 200, 40).Snapshot([.. addr, .. BlackNotes()]);
        var f = Assert.Single(MasumeDetector.Detect(page));
        Assert.StartsWith("東京都", f.Prefilled);
    }

    [Fact]
    public void Marks_on_two_lines_that_do_not_end_together_still_leave_a_field()
    {
        // "3＋4" mid-cell and the row number "5" in the bottom-right corner (国税庁 総額 cell): a real blank
        var c = new Canvas(600, 400).Rect(10, 10, 86, 48);
        var f = Assert.Single(MasumeDetector.Detect(c.Snapshot(Ch("3", 36, 22), Ch("＋", 44, 22), Ch("4", 52, 22), Ch("5", 77, 38))));
        Assert.Equal(FieldKind.Text, f.Kind);
    }

    [Fact]
    public void Small_square_cell_is_a_checkbox()
    {
        var page = new Canvas(600, 400).Rect(20, 20, 28, 28).Snapshot();

        var f = Assert.Single(MasumeDetector.Detect(page));

        Assert.Equal(FieldKind.Checkbox, f.Kind);
    }

    [Fact]
    public void Garbled_text_layer_is_not_readable() =>
        Assert.False(MasumeDetector.Readable("ᖺ୰ᶓ὾୰ࢽ࣮ࣗ"));

    [Fact]
    public void Japanese_text_is_readable() =>
        Assert.True(MasumeDetector.Readable("給与所得者の扶養控除等（異動）申告書"));

    [Fact]
    public void Largest_empty_rect_avoids_marks_on_both_sides()
    {
        var cell = new PdfRect(0, 0, 100, 20);
        var e = MasumeDetector.LargestEmpty(cell, [new PdfRect(2, 2, 12, 10), new PdfRect(92, 10, 98, 18)]);

        Assert.NotNull(e);
        Assert.True(e.Value.Area >= 0.5 * cell.Area);
        Assert.True(e.Value.X0 >= 12 && e.Value.X1 <= 92);
    }
}

public class GarbledTextTests
{
    [Fact]
    public void A_garbled_text_layer_never_reaches_names_labels_or_prefilled_text()
    {
        const double S = MasumeDetector.Scale;
        int w = (int)(600 * S), h = (int)(400 * S);
        var px = Enumerable.Repeat((byte)255, w * h * 3).ToArray();
        void Line(double x0, double y0, double x1, double y1)
        {
            for (int y = (int)(y0 * S); y <= (int)(y1 * S); y++)
                for (int x = (int)(x0 * S); x <= (int)(x1 * S); x++) { int i = (y * w + x) * 3; px[i] = px[i + 1] = px[i + 2] = 0; }
        }
        Line(10, 10, 190, 10); Line(10, 40, 190, 40); Line(10, 10, 10, 40); Line(190, 10, 190, 40); Line(60, 10, 60, 40);
        // label glyphs in the form colour and one prefilled glyph in black, all without a Unicode mapping
        PdfChar G(string c, double x, int rgb) => new(c, new PdfRect(x, 20, x + 8, 28), rgb);
        var chars = new[] { G("ᖺ", 20, 0x028559), G("୰", 30, 0x028559), G("ᶓ", 300, 0x028559), G("ࢽ", 100, 0x231F20) };
        var img = new RgbImage(w, h, px);
        var fields = MasumeDetector.Detect(new PageSnapshot(0, 600, 400, S, chars, img, img, []));

        var f = Assert.Single(fields);
        Assert.StartsWith("p1_", f.Name);
        Assert.Equal("", f.Label);
        Assert.Equal(MasumeDetector.UnreadableMarker, f.Prefilled);
    }

    [Fact]
    public void Printed_text_without_any_unicode_mapping_still_counts_as_prefilled()
    {
        // e-Gov style: the digits' font maps every glyph to nothing, so the text layer has no characters there at all
        const double S = MasumeDetector.Scale;
        int w = (int)(600 * S), h = (int)(400 * S);
        byte[] Blank() => Enumerable.Repeat((byte)255, w * h * 3).ToArray();
        var full = Blank();
        var graphics = Blank();
        void Fill(byte[] px, double x0, double y0, double x1, double y1, byte r, byte g, byte b)
        {
            for (int y = (int)(y0 * S); y <= (int)(y1 * S); y++)
                for (int x = (int)(x0 * S); x <= (int)(x1 * S); x++) { int i = (y * w + x) * 3; px[i] = r; px[i + 1] = g; px[i + 2] = b; }
        }
        foreach (var px in new[] { full, graphics })
        {
            Fill(px, 10, 10, 190, 10, 0, 0, 0); Fill(px, 10, 40, 190, 40, 0, 0, 0); Fill(px, 10, 10, 10, 40, 0, 0, 0);
            Fill(px, 190, 10, 190, 40, 0, 0, 0); Fill(px, 60, 10, 60, 40, 0, 0, 0);
            Fill(px, 10, 60, 190, 60, 0, 0, 0); Fill(px, 10, 90, 190, 90, 0, 0, 0); Fill(px, 10, 60, 10, 90, 0, 0, 0);
            Fill(px, 190, 60, 190, 90, 0, 0, 0); Fill(px, 60, 60, 60, 90, 0, 0, 0);
        }
        Fill(full, 80, 20, 84, 30, 20, 20, 20);    // black digits only the full render has (text), no characters for them
        Fill(full, 90, 20, 94, 30, 20, 20, 20);
        Fill(full, 80, 70, 84, 80, 2, 133, 89);    // a glyph in the form's own green in the other row: a label, not data
        Fill(full, 90, 70, 94, 80, 2, 133, 89);
        PdfChar G(string c, double x, double y) => new(c, new PdfRect(x, y, x + 8, y + 8), 0x028559);
        var chars = new[] { G("氏", 20, 20), G("名", 30, 20), G("住", 20, 70), G("所", 30, 70), G("番", 300, 300) };
        var fields = MasumeDetector.Detect(new PageSnapshot(0, 600, 400, S, chars, new RgbImage(w, h, full), new RgbImage(w, h, graphics), []))
            .OrderBy(f => f.Rect.Y0).ToList();

        Assert.Equal(2, fields.Count);
        Assert.Equal(MasumeDetector.UnreadableMarker, fields[0].Prefilled);
        Assert.Equal("", fields[1].Prefilled);
    }
}
