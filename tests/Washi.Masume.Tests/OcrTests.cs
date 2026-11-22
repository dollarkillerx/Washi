// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Ai;
using Washi.Core;
using Washi.Core.Fonts;
using Washi.Core.Ocr;
using Washi.Core.Pdfium;
using Washi.Tools;

namespace Washi.Masume.Tests;

/// <summary>Returns fixed lines, given in upright page points, scaled to the image it is handed.</summary>
public sealed class FakeOcrEngine(params (string Text, double X0, double Y0, double X1, double Y1)[] lines) : IOcrEngine
{
    public int Calls { get; private set; }
    public string Name => "fake";
    public string Describe => "fake OCR (test)";
    public bool SendsDataOffDevice => false;
    public int Dpi => 144;

    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(OcrImage image, IReadOnlyList<string> languages, CancellationToken ct = default)
    {
        Calls++;
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, image.Png[..4]);
        double s = Dpi / 72.0;
        return Task.FromResult<IReadOnlyList<OcrLine>>(
            [.. lines.Select(l => new OcrLine(l.Text, new PdfRect(l.X0 * s, l.Y0 * s, l.X1 * s, l.Y1 * s), 1))]);
    }
}

public sealed class OcrTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-ocr-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A "scan": a page that is only an image of text (no text layer).</summary>
    internal static void MakeScan(string path, string text = "Washi OCR 2026", int rotate = 0)
    {
        var src = path + ".src.pdf";
        var doc = new PdfDocument();
        AddTextPage(doc, text, 28, 400, 200);
        doc.Save(src);
        var png = path + ".png";
        using (var pdf = PdfiumDocument.Open(src))
        using (var fs = File.Create(png))
            PngWriter.Write(pdf.RenderPage(0, 3), fs);
        var scan = new PdfDocument();
        var p = scan.AddPage();
        p.Width = XUnit.FromPoint(400); p.Height = XUnit.FromPoint(200);
        using (var g = XGraphics.FromPdfPage(p)) g.DrawImage(XImage.FromFile(png), 0, 0, 400, 200);
        if (rotate != 0) p.Rotate = rotate;
        scan.Save(path);
        File.Delete(src); File.Delete(png);
    }

    /// <summary>A page with real text in the built-in Helvetica (PDFsharp has no font resolver on macOS).</summary>
    internal static void AddTextPage(PdfDocument doc, string text, double size, double width, double height)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(width); page.Height = XUnit.FromPoint(height);
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F1"] = font;
        page.Resources.Elements["/Font"] = fonts;
        var content = page.Contents.AppendContent();
        content.CreateStream(System.Text.Encoding.ASCII.GetBytes(
            $"BT /F1 {size} Tf 40 {height - 100} Td ({text}) Tj ET"));
    }

    private static List<PdfChar> Chars(string pdf)
    {
        using var d = PdfiumDocument.Open(pdf);
        return [.. d.ReadChars(0)];
    }

    [Fact]
    public async Task Recognised_lines_become_an_invisible_searchable_layer_in_place()
    {
        var scan = Path.Combine(_dir, "scan.pdf");
        MakeScan(scan);
        var engine = new FakeOcrEngine(("Washi 林燁 2026", 40, 76, 240, 104));
        var output = Path.Combine(_dir, "out.pdf");

        Assert.Equal([1], OcrPipeline.Plan(scan));
        var r = await OcrPipeline.RunAsync(scan, output, engine);

        Assert.Equal(1, r.Recognized);
        Assert.Equal(output, r.Output);
        using (var d = PdfiumDocument.Open(output)) Assert.Equal("Washi 林燁 2026", d.GetPageText(0).Trim());
        var chars = Chars(output).Where(c => c.Text.Trim().Length > 0).ToList();
        Assert.InRange(chars.First().Rect.X0, 38, 42);
        Assert.InRange(chars.Last().Rect.X1, 236, 242);
        Assert.All(chars, c => Assert.InRange(c.Rect.Center.Y, 80, 100));

        // the page looks the same: the layer draws nothing
        using (var a = PdfiumDocument.Open(scan))
        using (var b = PdfiumDocument.Open(output))
            Assert.Equal(a.RenderPage(0, 1).Pixels, b.RenderPage(0, 1).Pixels);

        // a second run finds nothing to do and writes nothing
        var again = await OcrPipeline.RunAsync(output, Path.Combine(_dir, "again.pdf"), engine);
        Assert.Equal(OcrPageStatus.AlreadySearchable, again.Pages[0].Status);
        Assert.Null(again.Output);
        Assert.Equal(1, engine.Calls);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Rotated_pages_get_the_layer_where_the_user_sees_the_text(int rotate)
    {
        var scan = Path.Combine(_dir, $"scan{rotate}.pdf");
        MakeScan(scan, rotate: rotate);
        double w = rotate == 180 ? 400 : 200, h = rotate == 180 ? 200 : 400;   // upright size
        var box = (Text: "MARK", X0: w * 0.25, Y0: h * 0.5, X1: w * 0.25 + 60, Y1: h * 0.5 + 20);
        var output = Path.Combine(_dir, $"out{rotate}.pdf");

        await OcrPipeline.RunAsync(scan, output, new FakeOcrEngine(box));

        var chars = Chars(output);
        Assert.Equal("MARK", string.Concat(chars.Select(c => c.Text)).Trim());
        var all = new PdfRect(chars.Min(c => c.Rect.X0), chars.Min(c => c.Rect.Y0), chars.Max(c => c.Rect.X1), chars.Max(c => c.Rect.Y1));
        Assert.InRange(all.X0, box.X0 - 2, box.X0 + 2);
        Assert.InRange(all.X1, box.X1 - 2, box.X1 + 2);
        Assert.InRange(all.Center.Y, box.Y0 + 5, box.Y1 - 5);
    }

    [Fact]
    public async Task Pages_with_text_are_skipped_unless_forced()
    {
        var pdf = Path.Combine(_dir, "text.pdf");
        var doc = new PdfDocument();
        AddTextPage(doc, "This page already has a text layer.", 12, 595, 842);
        doc.Save(pdf);
        var engine = new FakeOcrEngine(("x", 0, 0, 10, 10));

        var r = await OcrPipeline.RunAsync(pdf, Path.Combine(_dir, "o.pdf"), engine);
        Assert.Equal(OcrPageStatus.HasText, r.Pages[0].Status);
        Assert.Null(r.Output);
        Assert.Empty(OcrPipeline.Plan(pdf));
        Assert.Equal([1], OcrPipeline.Plan(pdf, new OcrOptions { Force = true }));
        Assert.Empty(OcrPipeline.Plan(pdf, new OcrOptions { Force = true, Pages = new HashSet<int> { 2 } }));
    }

    [Fact]
    public void The_glyphless_font_is_a_valid_TrueType_font()
    {
        var ttf = TextLayer.GlyphlessTrueType();
        uint sum = 0;
        Span<byte> word = stackalloc byte[4];
        for (int i = 0; i < ttf.Length; i += 4)
        {
            word.Clear();
            ttf.AsSpan(i, Math.Min(4, ttf.Length - i)).CopyTo(word);
            sum += BinaryPrimitives.ReadUInt32BigEndian(word);
        }
        Assert.Equal(0xB1B0AFBAu, sum);   // what head.checkSumAdjustment guarantees
        var font = OpenTypeFont.Parse(ttf);
        Assert.Equal("GlyphLessFont", font.PostScriptName);
    }

    [Theory]
    [InlineData("ja,EN", "ja en")]
    [InlineData("zh, zh-tw", "zh-Hans zh-Hant")]
    [InlineData("auto", "")]
    public void Languages_parse(string spec, string expected) =>
        Assert.Equal(expected, string.Join(' ', OcrLanguages.Parse(spec)));

    [Fact]
    public void Unknown_languages_are_refused() =>
        Assert.Throws<OcrException>(() => OcrLanguages.Parse("klingon"));

    [Fact]
    public void Tesseract_words_are_grouped_into_lines()
    {
        const string tsv = """
            level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
            5	1	1	1	1	1	10	20	30	12	96	Hello
            5	1	1	1	1	2	45	20	40	12	90	world
            5	1	1	1	2	1	10	40	12	12	80	林
            5	1	1	1	2	2	22	40	12	12	70	燁
            """;
        var lines = TesseractOcr.ParseTsv(tsv.Replace("\r", ""), cjk: true);
        Assert.Equal(["Hello world", "林燁"], lines.Select(l => l.Text));
        Assert.Equal(new PdfRect(10, 20, 85, 32), lines[0].Box);
    }

    [Fact]
    public async Task Apple_Vision_reads_a_scan_on_this_Mac()
    {
        Assert.SkipUnless(VisionOcr.IsAvailable, "Apple Vision needs macOS with the Xcode command line tools");
        var scan = Path.Combine(_dir, "scan.pdf");
        MakeScan(scan, "Searchable 2026");
        var output = Path.Combine(_dir, "vision.pdf");
        var r = await OcrPipeline.RunAsync(scan, output, new VisionOcr(), new OcrOptions { Languages = ["en"] });
        Assert.Contains("Searchable 2026", r.Pages[0].Text);
        using var d = PdfiumDocument.Open(output);
        Assert.Contains("Searchable 2026", d.GetPageText(0));
    }
}

public class AiOcrTests
{
    [Fact]
    public async Task The_model_transcribes_with_boxes_on_a_1000_grid()
    {
        var model = new FakeModel(r =>
        {
            Assert.Single(r.Images);
            Assert.NotNull(r.Schema);
            Assert.Contains("ja", r.Prompt);
            return JsonSerializer.Serialize(new
            {
                lines = new object[]
                {
                    new { text = "林 燁", box = new[] { 100, 200, 300, 250 } },
                    new { text = "", box = new[] { 0, 0, 10, 10 } },          // empty: dropped
                    new { text = "bad box", box = new[] { 1, 2, 3 } },        // not 4 numbers: dropped
                },
            });
        });
        var engine = new AiOcrEngine(model);
        var lines = await engine.RecognizeAsync(new OcrImage([0x89, (byte)'P', (byte)'N', (byte)'G'], 2000, 1000), ["ja"]);
        var line = Assert.Single(lines);
        Assert.Equal("林 燁", line.Text);
        Assert.Equal(new PdfRect(200, 200, 600, 250), line.Box);
        Assert.Equal(3, AiOcrEngine.DescribeOcr(model, 3).PageImages);
    }

    [Fact]
    public void Text_only_models_cannot_do_OCR() =>
        Assert.Throws<AiException>(() => new AiOcrEngine(new FakeModel(_ => "", images: false)));
}

public sealed class PageToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("washi-pagetools-").FullName;
    private readonly ToolRegistry _tools;

    public PageToolsTests()
    {
        _tools = WashiTools.CreateRegistry(new PathPolicy([_root]));
        ToolsTests.MakeFlatForm(Path.Combine(_root, "a.pdf"));
        OcrTests.MakeScan(Path.Combine(_root, "scan.pdf"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<ToolResult> Call(string tool, object args) =>
        await _tools.Find(tool)!.InvokeAsync(JsonSerializer.SerializeToElement(args));

    [Fact]
    public async Task Merge_and_split_write_new_files_only()
    {
        var merged = await Call("merge_pdfs", new { paths = new[] { "a.pdf", "scan.pdf" } });
        Assert.False(merged.IsError, merged.Text);
        Assert.Equal(2, JsonDocument.Parse(merged.Text).RootElement.GetProperty("pages").GetInt32());
        Assert.True(File.Exists(Path.Combine(_root, "a_merged.pdf")));
        Assert.Contains("already exists", (await Call("merge_pdfs", new { paths = new[] { "a.pdf", "scan.pdf" } })).Text);
        Assert.Contains("outside", (await Call("merge_pdfs", new { paths = new[] { "a.pdf", "/etc/hosts" } })).Text);

        var split = await Call("split_pdf", new { path = "a_merged.pdf", pages = "each" });
        Assert.False(split.IsError, split.Text);
        Assert.Equal(2, JsonDocument.Parse(split.Text).RootElement.GetProperty("files").GetArrayLength());
        Assert.Contains("already exists", (await Call("split_pdf", new { path = "a_merged.pdf" })).Text);
        Assert.Contains("outside", (await Call("split_pdf", new { path = "a.pdf", output_dir = "/tmp/elsewhere" })).Text);
    }

    [Fact]
    public async Task Organize_tool_writes_the_listed_pages_to_a_new_file()
    {
        await Call("merge_pdfs", new { paths = new[] { "a.pdf", "scan.pdf" } });
        var r = await Call("organize_pages", new { path = "a_merged.pdf", pages = new object[] { new { page = 2 }, new { page = 1, rotate = 90 }, new { page = 1, path = "scan.pdf" } } });
        Assert.False(r.IsError, r.Text);
        Assert.Equal(3, JsonDocument.Parse(r.Text).RootElement.GetProperty("pages").GetInt32());
        Assert.True(File.Exists(Path.Combine(_root, "a_merged_organized.pdf")));
        Assert.Contains("already exists", (await Call("organize_pages", new { path = "a_merged.pdf", pages = new[] { new { page = 1 } } })).Text);
        Assert.Contains("twice", (await Call("organize_pages", new { path = "a.pdf", output = "b.pdf", pages = new[] { new { page = 1 }, new { page = 1 } } })).Text);
        Assert.Contains("outside", (await Call("organize_pages", new { path = "a.pdf", output = "c.pdf", pages = new[] { new { page = 1, path = "/etc/hosts" } } })).Text);
    }

    [Fact]
    public async Task Ocr_tool_reports_text_or_a_clear_error()
    {
        var r = await Call("ocr_pdf", new { path = "scan.pdf", lang = "en" });
        if (!VisionOcr.IsAvailable && TesseractOcr.Find() is null)
        {
            Assert.True(r.IsError);
            Assert.Contains("OCR", r.Text);
            return;
        }
        Assert.False(r.IsError, r.Text);
        var json = JsonDocument.Parse(r.Text).RootElement;
        Assert.Equal(1, json.GetProperty("pages_recognized").GetInt32());
        Assert.Contains("2026", json.GetProperty("pages")[0].GetProperty("text").GetString());
        Assert.Contains("unknown OCR language", (await Call("ocr_pdf", new { path = "scan.pdf", lang = "xx", output = "x.pdf" })).Text);
    }
}
