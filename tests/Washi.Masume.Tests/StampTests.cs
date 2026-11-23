// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Headers, footers, page and Bates numbers, watermarks: added over the page, nothing else touched.</summary>
public sealed class StampTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-stamp-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A document written by hand: one page per entry (its body text, and an optional /Rotate).</summary>
    private string Doc(string name, params (string Body, int Rotate)[] pages)
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] /Count {pages.Length} >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
        foreach (var (body, rotate) in pages)
        {
            int content = objects.Count + 2;
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Rotate {rotate} /Contents {content} 0 R /Resources << /Font << /F1 3 0 R >> >> >>"));
            var data = Ascii($"BT /F1 14 Tf 60 150 Td ({body}) Tj ET 0.9 0.9 0.9 rg 60 60 280 40 re f");
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

    private string Three() => Doc("three.pdf", ("First body", 0), ("Second body", 0), ("Third body", 0));

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static string Text(string pdf, int page) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        return d.GetPageText(page);
    });

    private static StampResult Stamp(string input, string output, StampOptions o) => Locked(() => PdfStamper.Apply(input, output, o));

    [Fact]
    public void Page_numbers_go_on_every_page_and_the_pages_own_content_is_not_touched()
    {
        var input = Three();
        var r = Stamp(input, P("out.pdf"), new StampOptions { FooterCenter = "{page} / {pages}", HeaderRight = "{file}" });
        Assert.Equal(3, r.Pages);
        Assert.Null(r.FirstBates);
        for (int i = 0; i < 3; i++)
        {
            var text = Text(P("out.pdf"), i);
            Assert.Contains($"{i + 1} / 3", text);
            Assert.Contains("three.pdf", text);
            Assert.Contains(new[] { "First body", "Second body", "Third body" }[i], text);
        }
        // the original content stream is still there, byte for byte, between ours
        using var before = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var after = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        var contents = Assert.IsType<PdfArray>(after.Pages[0].Elements["/Contents"]);
        Assert.Equal(3, contents.Elements.Count);
        var original = ((PdfReference)before.Pages[0].Elements["/Contents"]!).Value as PdfDictionary;
        var kept = ((PdfReference)contents.Elements[1]).Value as PdfDictionary;
        Assert.Equal(original!.Stream.UnfilteredValue, kept!.Stream.UnfilteredValue);
        // tagged as pagination artifacts
        var added = Encoding.Latin1.GetString((((PdfReference)contents.Elements[2]).Value as PdfDictionary)!.Stream.UnfilteredValue);
        Assert.Contains("/Artifact <</Type /Pagination>> BDC", added);
    }

    [Fact]
    public void Bates_numbers_count_the_stamped_pages_only()
    {
        var input = Three();
        var r = Stamp(input, P("bates.pdf"), new StampOptions
        {
            FooterRight = "{bates}", Pages = "2-3", Bates = new BatesNumbers("ACME-", 41, 5),
        });
        Assert.Equal(2, r.Pages);
        Assert.Equal("ACME-00041", r.FirstBates);
        Assert.Equal("ACME-00042", r.LastBates);
        Assert.DoesNotContain("ACME", Text(P("bates.pdf"), 0));
        Assert.Contains("ACME-00041", Text(P("bates.pdf"), 1));
        Assert.Contains("ACME-00042", Text(P("bates.pdf"), 2));
    }

    [Fact]
    public void A_watermark_goes_across_the_page_see_through_in_front_or_behind()
    {
        var input = Three();
        Stamp(input, P("front.pdf"), new StampOptions { Watermark = new Watermark("CONFIDENTIAL") });
        Stamp(input, P("behind.pdf"), new StampOptions { Watermark = new Watermark("社外秘", Opacity: 0.2, Behind: true, Color: 0xCC0000) });
        Assert.Contains("CONFIDENTIAL", Text(P("front.pdf"), 1));
        Assert.Contains("社外秘", Text(P("behind.pdf"), 2));
        // turned 45° across the middle of the page
        var turned = Locked(() => PdfTextEditor.Segments(P("front.pdf"), 0)).Where(x => !x.Horizontal).ToList();
        Assert.Equal("CONFIDENTIAL", string.Concat(turned.Select(x => x.Text)));
        var box = turned.Select(x => x.Box).Aggregate((p, q) => new PdfRect(Math.Min(p.X0, q.X0), Math.Min(p.Y0, q.Y0), Math.Max(p.X1, q.X1), Math.Max(p.Y1, q.Y1)));
        Assert.InRange(box.Center.X, 185, 215);
        Assert.InRange(box.Center.Y, 135, 165);
        Assert.InRange(box.Width, 200, 360);                      // as large as fits
        using var d = PdfReader.Open(P("behind.pdf"), PdfDocumentOpenMode.Import);
        var res = d.Pages[0].Elements.GetDictionary("/Resources")!;
        var gs = res.Elements.GetDictionary("/ExtGState")!;
        var alpha = ((PdfReference)gs.Elements.Values.First()).Value as PdfDictionary;
        Assert.Equal(0.2, alpha!.Elements.GetReal("/ca"), 3);
        // behind: drawn before the page's content
        var first = ((PdfReference)((PdfArray)d.Pages[0].Elements["/Contents"]!).Elements[0]).Value as PdfDictionary;
        Assert.Contains("/Subtype /Watermark", Encoding.Latin1.GetString(first!.Stream.UnfilteredValue));
    }

    [Fact]
    public void Turned_pages_get_their_header_at_the_top_as_shown()
    {
        var input = Doc("turned.pdf", ("Upright page", 0), ("Turned page", 90), ("Upside down", 180));
        Stamp(input, P("out.pdf"), new StampOptions { HeaderLeft = "Washi Inc.", FooterCenter = "{page}" });
        foreach (var page in new[] { 0, 1, 2 })
        {
            var segs = Locked(() => PdfTextEditor.Segments(P("out.pdf"), page));
            var header = Assert.Single(segs, s => s.Text == "Washi Inc.");
            Assert.True(header.Horizontal);                       // reads left to right as shown
            Assert.InRange(header.Box.Y0, 20, 40);                // at the top
            Assert.InRange(header.Box.X0, 25, 32);                // at the left
            var footer = Assert.Single(segs, s => s.Text == (page + 1).ToString());
            var (_, height) = page == 1 ? (300.0, 400.0) : (400.0, 300.0);
            Assert.InRange(footer.Box.Y1, height - 40, height - 20);   // at the bottom
        }
    }

    [Fact]
    public void Japanese_text_uses_a_CJK_font_and_numbers_can_continue_another_document()
    {
        var input = Three();
        Stamp(input, P("ja.pdf"), new StampOptions { HeaderCenter = "株式会社和紙　御中", FooterRight = "{page}", NumberOffset = 10, Serif = true });
        Assert.Contains("株式会社和紙", Text(P("ja.pdf"), 0));
        Assert.Contains("13", Text(P("ja.pdf"), 2));
    }

    [Fact]
    public void Nothing_to_add_bad_sizes_and_existing_files_are_refused()
    {
        var input = Three();
        Assert.Equal("Err_StampNothing", Assert.Throws<PageAssemblyException>(() => Stamp(input, P("a.pdf"), new StampOptions())).Key);
        Assert.Equal("Err_StampSize", Assert.Throws<PageAssemblyException>(() => Stamp(input, P("b.pdf"), new StampOptions { HeaderLeft = "x", FontSize = 200 })).Key);
        Assert.Equal("Err_StampWatermark", Assert.Throws<PageAssemblyException>(() =>
            Stamp(input, P("c.pdf"), new StampOptions { Watermark = new Watermark("x", Opacity: 0) })).Key);
        File.WriteAllText(P("taken.pdf"), "x");
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() => Stamp(input, P("taken.pdf"), new StampOptions { HeaderLeft = "x" })).Key);
    }

    [Fact]
    public void Tokens_resolve()
    {
        var date = new DateTime(2026, 11, 23);
        Assert.Equal("3 of 9 — a.pdf — 2026-11-23 — B-007", PdfStamper.Resolve("{page} of {pages} — {file} — {date} — {bates}", 3, 9, "a.pdf", date, "B-007"));
        Assert.Equal("{unknown} 3", PdfStamper.Resolve("{unknown} {PAGE}", 3, 9, "a.pdf", date, null));
    }

    [Fact]
    public async Task Agents_stamp_with_headers_bates_and_a_watermark()
    {
        var input = Three();
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var r = await tools.Find("stamp_pages")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input,
            header = new { right = "{date}" },
            footer = new { center = "{page}/{pages}", right = "{bates}" },
            bates = new { prefix = "DOC", start = 100, digits = 4 },
            watermark = new { text = "COPY", color = "#1F49E0", opacity = 0.25 },
        }), TestContext.Current.CancellationToken);
        Assert.False(r.IsError, r.Text);
        var json = System.Text.Json.JsonDocument.Parse(r.Text).RootElement;
        Assert.Equal(3, json.GetProperty("pages").GetInt32());
        Assert.Equal("DOC0102", json.GetProperty("last_bates").GetString());
        var text = Text(P("three_stamped.pdf"), 2);
        Assert.Contains("3/3", text);
        Assert.Contains("DOC0102", text);
        Assert.Contains("COPY", text);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), text);

        var bad = await tools.Find("stamp_pages")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input, watermark = new { text = "X", color = "blue" }, output = P("bad.pdf"),
        }), TestContext.Current.CancellationToken);
        Assert.True(bad.IsError);
        Assert.False(File.Exists(P("bad.pdf")));
    }

    [Fact]
    public void Marks_Washi_added_are_found_removed_and_replaced()
    {
        var input = Three();
        Stamp(input, P("once.pdf"), new StampOptions { HeaderLeft = "Draft 1", FooterCenter = "{page} / {pages}", Watermark = new Watermark("DRAFT") });
        Assert.Equal((3, 3), PdfStamper.Existing(P("once.pdf")));
        Assert.Equal((0, 0), PdfStamper.Existing(input));

        // the watermark only
        int n = Locked(() => PdfStamper.Remove(P("once.pdf"), P("nowm.pdf"), headers: false, watermarks: true));
        Assert.Equal(3, n);
        Assert.Equal((3, 0), PdfStamper.Existing(P("nowm.pdf")));
        Assert.DoesNotContain("DRAFT", Text(P("nowm.pdf"), 0));
        Assert.Contains("Draft 1", Text(P("nowm.pdf"), 0));

        // everything: the pages read as they did before any mark
        Locked(() => PdfStamper.Remove(P("once.pdf"), P("clean.pdf"), headers: true, watermarks: true));
        Assert.Equal((0, 0), PdfStamper.Existing(P("clean.pdf")));
        for (int i = 0; i < 3; i++) Assert.Equal(Text(input, i), Text(P("clean.pdf"), i));

        // stamped again with replace: the old header goes, the watermark stays (no new one was given)
        Stamp(P("once.pdf"), P("twice.pdf"), new StampOptions { HeaderLeft = "Final", ReplaceExisting = true });
        var t = Text(P("twice.pdf"), 1);
        Assert.Contains("Final", t);
        Assert.DoesNotContain("Draft 1", t);
        Assert.DoesNotContain("2 / 3", t);                                   // the old footer was in the same block
        Assert.Contains("DRAFT", t);
        Stamp(P("once.pdf"), P("named.pdf"), new StampOptions { FooterRight = "{file}", ReplaceExisting = true });
        Assert.Contains("once.pdf", Text(P("named.pdf"), 0));             // the file's own name, not the temporary copy's

        Assert.Equal("Err_StampNone", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfStamper.Remove(input, P("x.pdf"), true, true))).Key);
    }

    [Fact]
    public void Headers_footers_and_watermarks_from_other_programs_are_found_and_removed()
    {
        // Acrobat-style header (inline properties, a nested span), Word-style footer (properties by name), a watermark
        var content =
            "/Artifact <</Type /Pagination /Subtype /Header>> BDC BT /F1 9 Tf 30 280 Td /Span <</ActualText (Draft)>> BDC (Draft copy) Tj EMC ET EMC\n" +
            "BT /F1 14 Tf 60 150 Td (Body text stays) Tj ET\n" +
            "/Artifact /P0 BDC BT /F1 9 Tf 190 20 Td (Page 1 of 1) Tj ET EMC\n" +
            "/Artifact <</Subtype /Watermark /Type /Pagination>> BDC q 0.5 g BT /F1 40 Tf 1 0 0 1 80 120 Tm (SAMPLE) Tj ET Q EMC";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /Properties << /P0 << /Type /Pagination /Subtype /Footer >> >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ];
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(P("other.pdf"), sb.ToString(), Encoding.ASCII);

        Assert.Equal((1, 1), PdfStamper.Existing(P("other.pdf")));
        Assert.Equal(2, Locked(() => PdfStamper.Remove(P("other.pdf"), P("noheaders.pdf"), headers: true, watermarks: false)));
        var t = Text(P("noheaders.pdf"), 0);
        Assert.DoesNotContain("Draft", t);
        Assert.DoesNotContain("Page 1 of 1", t);
        Assert.Contains("SAMPLE", t);
        Assert.Contains("Body text stays", t);
        Assert.Equal((0, 1), PdfStamper.Existing(P("noheaders.pdf")));

        Locked(() => PdfStamper.Remove(P("other.pdf"), P("clean.pdf"), headers: true, watermarks: true));
        Assert.Equal("Body text stays", Text(P("clean.pdf"), 0).Trim());
    }

    [Fact]
    public void Japanese_marks_are_drawn_in_an_embedded_subset_of_this_computers_font()
    {
        Assert.SkipUnless(File.Exists("/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc") || OperatingSystem.IsWindows(), "no Japanese system font to embed here");
        var input = Three();
        Stamp(input, P("ja.pdf"), new StampOptions { HeaderLeft = "株式会社サンプル", Watermark = new Watermark("社外秘") });
        using var d = PdfReader.Open(P("ja.pdf"), PdfDocumentOpenMode.Import);
        var fonts = d.Pages[0].Resources.Elements.GetDictionary("/Font")!.Elements.Values
            .Select(v => ((PdfReference)v).Value as PdfDictionary).Where(f => f!.Elements.GetName("/Subtype") == "/Type0").ToList();
        var font = Assert.Single(fonts.DistinctBy(f => f!.Elements.GetName("/BaseFont")));
        Assert.Matches("^/[A-Z]{6}\\+", font!.Elements.GetName("/BaseFont"));      // a subset, embedded
        Assert.NotNull(font.Elements["/ToUnicode"]);
        Assert.InRange(new FileInfo(P("ja.pdf")).Length - new FileInfo(input).Length, 1, 150_000);
        Assert.Contains("社外秘", Text(P("ja.pdf"), 0));
    }
}
