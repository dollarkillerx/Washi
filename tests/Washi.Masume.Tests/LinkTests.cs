// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Links: listed, added over an area or found in the text, pointed elsewhere, removed; the pages look the same.</summary>
public sealed class LinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-links-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>Pages of 400 × 300 pt with text lines (Helvetica), optional /Rotate and annotations (raw dictionaries).</summary>
    private string Doc(string name, params (string[] Lines, int Rotate, string[] Annots)[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "",   // pages, below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var kids = new List<int>();
        foreach (var (lines, rotate, annots) in pages)
        {
            int pageNo = objects.Count + 1;
            kids.Add(pageNo);
            var content = string.Join("\n", lines.Select((l, i) => $"BT /F1 12 Tf 30 {260 - i * 24} Td ({l}) Tj ET"));
            var annotRefs = new List<int>();
            objects.Add("");                                                   // the page, filled in below
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            foreach (var a in annots) { objects.Add(a.Replace("PAGE", $"{pageNo} 0 R")); annotRefs.Add(objects.Count); }
            objects[pageNo - 1] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Rotate {rotate} /Contents {pageNo + 1} 0 R " +
                                  $"/Resources << /Font << /F1 3 0 R >> >> /Annots [{string.Join(" ", annotRefs.Select(r => $"{r} 0 R"))}] >>";
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(P(name), sb.ToString(), Encoding.Latin1);
        return P(name);
    }

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private string Brochure() => Doc("b.pdf",
        (["Visit https://example.com/shop today", "Mail sales@example.co.jp or www.washi.dev", "See the price list"],
         0, ["<< /Type /Annot /Subtype /Link /Rect [30 30 130 50] /Border [0 0 0] /A << /S /URI /URI (https://old.example) >> >>"]),
        (["Price list"], 0, []));

    [Fact]
    public void Links_are_listed_with_their_targets()
    {
        var links = PdfLinks.List(Brochure());
        var l = Assert.Single(links);
        Assert.Equal(("1-1", 0, "uri", "https://old.example"), (l.Id, l.Page, l.Kind, l.Target));
        Assert.Equal(new PdfRect(30, 250, 130, 270), l.Rect);                         // upright: y from the top
    }

    [Fact]
    public void Addresses_in_the_text_become_links_and_the_pages_look_the_same()
    {
        var input = Brochure();
        var found = Locked(() => PdfLinks.FindAddresses(input));
        Assert.Equal(["https://example.com/shop", "sales@example.co.jp", "www.washi.dev"], found.Select(f => f.Target));
        var r = Locked(() => PdfLinks.Apply(input, P("out.pdf"), found));
        Assert.Equal(new LinkResult(3, 0, 0), r);
        var links = PdfLinks.List(P("out.pdf"));
        Assert.Contains(links, l => l.Target == "https://example.com/shop");
        Assert.Contains(links, l => l.Target == "mailto:sales@example.co.jp");
        Assert.Contains(links, l => l.Target == "https://www.washi.dev");
        var shop = links.Single(l => l.Target == "https://example.com/shop");
        Assert.InRange(shop.Rect.Y0, 25, 40);                                         // over the first line
        // found again: nothing new (each address has its link now)
        Assert.Empty(Locked(() => PdfLinks.FindAddresses(P("out.pdf"))));
    }

    [Fact]
    public void Links_go_to_pages_change_target_and_are_removed()
    {
        var input = Brochure();
        var r = Locked(() => PdfLinks.Apply(input, P("out.pdf"), [new NewLink(0, new PdfRect(30, 70, 150, 86), "2")],
                                            change: new Dictionary<string, string> { ["1-1"] = "https://new.example" }));
        Assert.Equal(new LinkResult(1, 0, 1), r);
        var links = PdfLinks.List(P("out.pdf"));
        Assert.Contains(links, l => l.Kind == "page" && l.Target == "2" && l.Rect.IoU(new PdfRect(30, 70, 150, 86)) > 0.99);
        Assert.Contains(links, l => l.Id == "1-1" && l.Target == "https://new.example");

        var r2 = Locked(() => PdfLinks.Apply(P("out.pdf"), P("out2.pdf"), [], remove: ["1-1"]));
        Assert.Equal(new LinkResult(0, 1, 0), r2);
        Assert.DoesNotContain(PdfLinks.List(P("out2.pdf")), l => l.Kind == "uri");
    }

    [Fact]
    public void A_turned_page_gets_its_link_where_it_is_shown()
    {
        var input = Doc("t.pdf", (["Turned"], 90, []));
        Locked(() => PdfLinks.Apply(input, P("out.pdf"), [new NewLink(0, new PdfRect(20, 40, 120, 60), "https://example.com")]));
        var l = Assert.Single(PdfLinks.List(P("out.pdf")));
        Assert.True(l.Rect.IoU(new PdfRect(20, 40, 120, 60)) > 0.99);
    }

    [Theory]
    [InlineData("12", "page", "12")]
    [InlineData("example.com/a?b=1", "uri", "https://example.com/a?b=1")]
    [InlineData("taro@example.jp", "uri", "mailto:taro@example.jp")]
    [InlineData("ftp://files.example", "uri", "ftp://files.example")]
    public void Targets_are_read_as_typed(string typed, string kind, string value) =>
        Assert.Equal((kind, value), PdfLinks.Parse(typed));

    [Fact]
    public void Bad_targets_ids_and_pages_are_refused_and_nothing_is_written()
    {
        var input = Brochure();
        Assert.Equal("Err_LinkTarget", Assert.Throws<PageAssemblyException>(() => PdfLinks.Parse("not a link")).Key);
        Assert.Equal("Err_LinkNothing", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfLinks.Apply(input, P("x1.pdf"), []))).Key);
        Assert.Equal("Err_LinkId", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfLinks.Apply(input, P("x2.pdf"), [], remove: ["1-9"]))).Key);
        Assert.Equal("Err_RangeOutside", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfLinks.Apply(input, P("x3.pdf"), [new NewLink(0, new PdfRect(0, 0, 10, 10), "9")]))).Key);
        Assert.False(File.Exists(P("x1.pdf")) || File.Exists(P("x2.pdf")) || File.Exists(P("x3.pdf")));
    }
}
