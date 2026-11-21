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

        private void Dot(int x, int y)
        {
            int i = (y * W + x) * 3;
            Px[i] = Px[i + 1] = Px[i + 2] = 0;
        }

        public PageSnapshot Snapshot(params PdfChar[] chars)
        {
            var img = new RgbImage(W, H, Px);
            return new PageSnapshot(0, W / S, H / S, S, chars, img, img, []);
        }
    }

    private static PdfChar Ch(string c, double x0, double y0, double size = 8) =>
        new(c, new PdfRect(x0, y0, x0 + size, y0 + size), 0x000000);

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
}
