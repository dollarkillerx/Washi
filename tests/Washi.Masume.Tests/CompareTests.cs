// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Washi.Core;
using Washi.Core.Fonts;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Comparing two versions: pages paired by their text, words compared, pictures compared outside the text.</summary>
public sealed class CompareTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-compare-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A document written by hand, one content stream per page (Helvetica as /F1), pages 400 × 300 unless given.</summary>
    private string Doc(string name, params string[] pages) => Sized(name, 400, 300, pages);

    private string Sized(string name, int width, int height, params string[] pages)
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{6 + i * 2} 0 R"))}] /Count {pages.Length} >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Ascii("<< /Type /Font /Subtype /Type0 /BaseFont /KozMinPr6N-Regular /Encoding /UniJIS-UTF16-H /DescendantFonts [5 0 R] >>"),
            Ascii("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /KozMinPr6N-Regular /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> /DW 1000 >>"),
        };
        foreach (var content in pages)
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents {objects.Count + 2} 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> >>"));
            var data = Encoding.Latin1.GetBytes(content);
            objects.Add([.. Ascii($"<< /Length {data.Length} >>\nstream\n"), .. data, .. Ascii("\nendstream")]);
        }
        using var ms = new MemoryStream();
        ms.Write(Ascii("%PDF-1.7\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            ms.Write(Ascii($"{i + 1} 0 obj\n"));
            ms.Write(objects[i]);
            ms.Write(Ascii("\nendobj\n"));
        }
        long xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Ascii(sb.ToString()));
        File.WriteAllBytes(P(name), ms.ToArray());
        return P(name);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string Lines(params string[] lines) =>
        string.Join("\n", lines.Select((l, i) => $"BT /F1 12 Tf 30 {260 - i * 24} Td ({l}) Tj ET"));

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static CompareReport Compare(string a, string b) => Locked(() => PdfComparer.Compare(a, b));

    [Fact]
    public void The_same_document_has_no_changes()
    {
        var a = Doc("a.pdf", Lines("Invoice 2025", "Total due: 1,200 yen"));
        var report = Compare(a, a);
        Assert.True(report.Same);
        Assert.Equal([(0, 0)], report.Pairs.Select(p => (p.Old!.Value, p.New!.Value)));
    }

    [Fact]
    public void Words_changed_added_and_removed_are_found_where_they_are()
    {
        var a = Doc("a.pdf", Lines("Invoice 2025", "Total due: 1,200 yen", "Payment within 30 days please", "Thank you"));
        var b = Doc("b.pdf", Lines("Invoice 2025", "Total due: 1,350 yen", "Payment within 30 days", "Thank you very much"));
        var report = Compare(a, b);
        var changed = Assert.Single(report.Changes, c => c.Kind == ChangeKind.Changed);
        Assert.Equal("1,200", changed.OldText);
        Assert.Equal("1,350", changed.NewText);
        var box = Assert.Single(changed.NewBoxes);
        Assert.InRange(box.Center.Y, 300 - 236 - 10, 300 - 236 + 6);              // on the second line
        var removed = Assert.Single(report.Changes, c => c.Kind == ChangeKind.Deleted);
        Assert.Equal("please", removed.OldText);
        var added = Assert.Single(report.Changes, c => c.Kind == ChangeKind.Inserted);
        Assert.Equal("very much", added.NewText);
        Assert.DoesNotContain(report.Changes, c => c.Kind == ChangeKind.Visual);
    }

    [Fact]
    public void An_inserted_page_does_not_shift_the_rest()
    {
        var a = Doc("a.pdf", Lines("Chapter one", "Alpha text here"), Lines("Chapter two", "Beta text here"), Lines("Chapter three", "Gamma text here"));
        var b = Doc("b.pdf", Lines("Chapter one", "Alpha text here"), Lines("A new page", "inserted between"), Lines("Chapter two", "Beta text here"),
                    Lines("Chapter three", "Gamma text here"));
        var report = Compare(a, b);
        Assert.Equal([(0, 0), (-1, 1), (1, 2), (2, 3)], report.Pairs.Select(p => (p.Old ?? -1, p.New ?? -1)));
        var added = Assert.Single(report.Changes);
        Assert.Equal(ChangeKind.PageAdded, added.Kind);
        Assert.Equal(1, added.NewPage);
        Assert.Contains("A new page", added.NewText);

        var back = Compare(b, a);
        var removed = Assert.Single(back.Changes);
        Assert.Equal(ChangeKind.PageRemoved, removed.Kind);
        Assert.Equal(1, removed.OldPage);
    }

    [Fact]
    public void Pictures_that_changed_outside_the_text_are_boxed()
    {
        var text = Lines("Site plan", "Approved");
        var a = Doc("a.pdf", text + "\n0.2 0.4 0.8 rg 220 60 120 80 re f");
        var b = Doc("b.pdf", text + "\n0.8 0.2 0.2 rg 220 60 120 80 re f\n0 0 0 RG 2 w 40 40 m 120 100 l S");
        var report = Compare(a, b);
        Assert.DoesNotContain(report.Changes, c => c.Kind is ChangeKind.Changed or ChangeKind.Inserted or ChangeKind.Deleted);
        var visual = report.Changes.Where(c => c.Kind == ChangeKind.Visual).ToList();
        Assert.Equal(2, visual.Count);
        Assert.Contains(visual, v => v.NewBoxes[0].IoU(new PdfRect(220, 160, 340, 240)) > 0.7);   // the recoloured block
        Assert.Contains(visual, v => v.NewBoxes[0].Intersect(new PdfRect(40, 200, 120, 260)).Area > 0);   // the new line
    }

    [Fact]
    public void A_page_of_another_size_is_one_change()
    {
        var a = Doc("a.pdf", Lines("Same words"));
        var b = Sized("b.pdf", 300, 400, Lines("Same words"));
        var change = Assert.Single(Compare(a, b).Changes);
        Assert.Equal(ChangeKind.Visual, change.Kind);
        Assert.Contains("400", change.OldText);
    }

    [Fact]
    public void Japanese_is_compared_character_by_character()
    {
        string Ja(string name, string text) =>
            Doc(name, $"BT /F2 14 Tf 30 240 Td <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))}> Tj ET");
        var report = Compare(Ja("a.pdf", "申告者 山田太郎 様"), Ja("b.pdf", "申告者 山田次郎 様"));
        var change = Assert.Single(report.Changes);
        Assert.Equal(ChangeKind.Changed, change.Kind);
        Assert.Equal("太", change.OldText);
        Assert.Equal("次", change.NewText);
    }

    [Fact]
    public void The_marked_copy_carries_a_comment_for_each_change()
    {
        var a = Doc("a.pdf", Lines("Total due: 1,200 yen", "Payment within 30 days please"), Lines("Old last page"));
        var b = Doc("b.pdf", Lines("Total due: 1,350 yen", "Payment within 30 days", "New line here"));
        var report = Compare(a, b);
        int marks = Locked(() => PdfComparer.MarkUp(b, P("marked.pdf"), report));
        Assert.Equal(report.Changes.Count, marks);
        var comments = Locked(() => PdfComments.List(P("marked.pdf")));
        Assert.Equal([ChangeKind.Changed, ChangeKind.Deleted, ChangeKind.Inserted, ChangeKind.PageRemoved], report.Changes.Select(c => c.Kind));
        Assert.Contains(comments, c => c.Type == "Highlight" && c.Text.Contains("1,200"));
        Assert.Contains(comments, c => c.Type == "Text" && c.Text.Contains("please"));
        Assert.Contains(comments, c => c.Type == "Text" && c.Text.Contains("Old last page"));
        var same = Compare(a, a);
        Assert.Equal("Err_CompareSame", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfComparer.MarkUp(a, P("x.pdf"), same))).Key);
    }
}
