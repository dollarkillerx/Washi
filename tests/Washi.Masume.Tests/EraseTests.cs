// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Erasing printed content for real: gone from the file, nothing else moved, the form itself kept.</summary>
public sealed class EraseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-erase-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// A 400 × 300 pt page written by hand, so the content stream is exactly what the test says: a green form (cell,
    /// label), black data inside the cell (text, a 2×2 image, a filled mark), a black rule, text outside, and two text
    /// runs chained on one line ("AA" then "BB" with no new position between them).
    /// </summary>
    private string Form(string name = "form.pdf")
    {
        const string content = """
            5 5 m S
            0 0.5 0.3 RG 1 w 20 200 360 60 re S
            BT /F1 12 Tf 0 0.5 0.3 rg 30 240 Td (LABEL) Tj ET
            BT /F1 12 Tf 0 0 0 rg 30 215 Td (SECRET) Tj ET
            q 20 0 0 20 300 210 cm /Im1 Do Q
            q 0 0 0 rg 240 210 m 255 225 l 270 210 l f Q
            0 0 0 RG 0.5 w 30 120 m 370 120 l S
            BT /F1 12 Tf 0 0 0 rg 30 80 Td (KEEPME) Tj ET
            BT /F1 12 Tf 0 0 0 rg 200 80 Td (AA) Tj (BB) Tj ET
            q BT /F1 12 Tf 7 Tr 300 30 Td (CLIP) Tj ET Q
            """;
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 4 0 R " +
                  "/Resources << /Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >> >>"),
            Stream("", Ascii(content)),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 ", [0, 0, 0, 0]),
        };
        var path = Path.Combine(_dir, name);
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
        File.WriteAllBytes(path, ms.ToArray());
        return path;

        static byte[] Stream(string dict, byte[] data) =>
            [.. Ascii($"<< {dict}/Length {data.Length} >>\nstream\n"), .. data, .. Ascii("\nendstream")];
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // the cell, in upright top-down points (page height 300): user y 200..260 -> 40..100
    private static readonly EraseRegion Cell = new(0, new PdfRect(25, 42, 375, 98));

    private static string Text(string pdf)
    {
        using var d = PdfiumDocument.Open(pdf);
        return d.GetPageText(0);
    }

    /// <summary>Every stream in the file, decoded: what someone digging through the PDF would find.</summary>
    private static string AllStreams(string pdf)
    {
        using var d = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return string.Concat(d.Internals.GetAllObjects().OfType<PdfDictionary>().Where(o => o.Stream is not null)
            .Select(o => Encoding.Latin1.GetString(o.Stream.UnfilteredValue)));
    }

    private static int Images(string pdf)
    {
        using var d = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return d.Internals.GetAllObjects().OfType<PdfDictionary>().Count(o => o.Elements.GetName("/Subtype") == "/Image");
    }

    [Fact]
    public void Black_data_in_a_box_is_gone_from_the_file_and_the_form_stays()
    {
        var pdf = Form();
        Assert.Contains("SECRET", Text(pdf));
        Assert.Equal(1, Images(pdf));
        var output = Path.Combine(_dir, "out.pdf");

        var report = PdfEraser.Erase(pdf, output, [Cell]);

        Assert.Equal(1, report.TextRemoved);
        Assert.Equal(1, report.ImagesRemoved);
        Assert.Equal(1, report.DrawingsRemoved);      // the black mark; the green cell border is not inside the box
        Assert.Equal(0, report.RemainingChars);
        Assert.Equal(0, report.ChangedPixelsOutside);
        var text = Text(output);
        Assert.DoesNotContain("SECRET", text);
        Assert.Contains("LABEL", text);               // green: the form's own printing
        Assert.Contains("KEEPME", text);              // outside the box
        Assert.DoesNotContain("SECRET", AllStreams(output));   // not hidden: gone, old content stream included
        Assert.Equal(0, Images(output));
        Assert.Equal(1, report.ImagesDropped);
        Assert.Contains(report.Kept, k => k.Why == EraseKeptWhy.Colored);
    }

    [Fact]
    public void Coloured_printing_goes_too_when_asked()
    {
        var output = Path.Combine(_dir, "out.pdf");
        var report = PdfEraser.Erase(Form(), output, [Cell], new EraseOptions(KeepColored: false));
        Assert.Equal(2, report.TextRemoved);
        Assert.DoesNotContain("LABEL", Text(output));
    }

    [Fact]
    public void Rules_stay_unless_asked_and_planning_writes_nothing()
    {
        var pdf = Form();
        var rule = new EraseRegion(0, new PdfRect(25, 175, 375, 185));   // around the black line at user y 120
        var (none, _) = PdfEraser.Plan(pdf, [rule]);
        Assert.Empty(none);
        var (lines, _) = PdfEraser.Plan(pdf, [rule], new EraseOptions(Rules: true));
        Assert.Equal(EraseKind.Drawing, Assert.Single(lines).Kind);
        Assert.Single(Directory.GetFiles(_dir));                         // Plan never writes
        Assert.Throws<EraseException>(() => PdfEraser.Erase(pdf, Path.Combine(_dir, "x.pdf"), [rule]));   // nothing to erase
        Assert.False(File.Exists(Path.Combine(_dir, "x.pdf")));
    }

    [Fact]
    public void A_run_chained_to_a_kept_one_is_kept_so_nothing_moves()
    {
        var pdf = Form();
        // "AA" only (user x 200.. at y 80 -> top-down 220): "BB" is drawn right after it without a new position
        var aa = new EraseRegion(0, new PdfRect(198, 205, 217, 224));
        var (planned, _) = PdfEraser.Plan(pdf, [aa]);
        Assert.Equal("AA", Assert.Single(planned).Text);
        var ex = Assert.Throws<EraseException>(() => PdfEraser.Erase(pdf, Path.Combine(_dir, "x.pdf"), [aa]));
        Assert.Contains("joined", ex.Message);

        // both runs in the box: both go, and KEEPME on the same height stays where it was
        var both = new EraseRegion(0, new PdfRect(198, 205, 235, 224));
        var output = Path.Combine(_dir, "both.pdf");
        var report = PdfEraser.Erase(pdf, output, [both]);
        Assert.Equal(2, report.TextRemoved);
        Assert.Equal(0, report.ChangedPixelsOutside);
        Assert.DoesNotContain("AA", Text(output));
        Assert.Contains("KEEPME", Text(output));
    }

    [Fact]
    public void Touching_boxes_each_get_their_own_content()
    {
        // two cells side by side, as on a form: SECRET lies in the right one and only grazes the left one's edge
        var pdf = Form();
        var left = new EraseRegion(0, new PdfRect(5, 70, 29.5, 90));
        var right = new EraseRegion(0, new PdfRect(29.5, 70, 120, 90));
        var (targets, _) = PdfEraser.Plan(pdf, [left, right]);
        Assert.Equal("SECRET", Assert.Single(targets).Text);
    }

    [Fact]
    public void Text_used_as_a_clip_is_kept_and_every_page_of_the_sample_matches()
    {
        var pdf = Form();
        Assert.All(PdfEraser.Check(pdf), c => Assert.True(c.Matches, $"page {c.Page + 1}: {c.Objects} objects, {c.Operators} operators"));
        var clip = new EraseRegion(0, new PdfRect(295, 255, 340, 275));    // around CLIP (user y 30)
        var (targets, kept) = PdfEraser.Plan(pdf, [clip], new EraseOptions(KeepColored: false));
        Assert.Empty(targets);
        Assert.Equal(EraseKeptWhy.Clips, Assert.Single(kept).Why);
    }

    [Fact]
    public void Errors_have_text_in_the_ui_languages()
    {
        var before = Washi.Core.Localization.L.Culture;
        try
        {
            Washi.Core.Localization.L.Culture = System.Globalization.CultureInfo.GetCultureInfo("zh-Hans");
            var ex = Assert.Throws<EraseException>(() => PdfEraser.Erase(Form(), Path.Combine(_dir, "n.pdf"), [new EraseRegion(0, new PdfRect(380, 280, 395, 295))]));
            Assert.Contains("nothing to erase", ex.Message);                         // English for logs and agents
            Assert.Contains("没有可删除", Washi.Core.Localization.Errors.Text(ex));   // the app shows this
            var ai = new Washi.Ai.AiSettings(Washi.Ai.AiProvider.DeepSeek, "");
            var noModel = Assert.Throws<Washi.Ai.AiException>(() => ai.CreateChatProvider(new HttpClient()));
            Assert.Contains("还没有选择模型", Washi.Core.Localization.Errors.Text(noModel));
        }
        finally { Washi.Core.Localization.L.Culture = before; }
    }

    [Fact]
    public void Never_overwrites_and_refuses_pages_that_do_not_exist()
    {
        var pdf = Form();
        var output = Path.Combine(_dir, "out.pdf");
        File.WriteAllText(output, "mine");
        Assert.Throws<EraseException>(() => PdfEraser.Erase(pdf, output, [Cell]));
        Assert.Equal("mine", File.ReadAllText(output));
        Assert.Throws<EraseException>(() => PdfEraser.Erase(pdf, Path.Combine(_dir, "o2.pdf"), [Cell with { Page = 3 }]));
    }

    [Fact]
    public void The_erase_content_tool_reports_and_checks()
    {
        var pdf = Form();
        var registry = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = pdf,
            boxes = new[] { new { page = 1, rect = new[] { 25.0, 42, 375, 98 } } },
        });
        var r = registry.Find("erase_content")!.InvokeAsync(args, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.False(r.IsError, r.Text);
        Assert.Contains("\"characters_left\": 0", r.Text);
        Assert.Contains("\"pixels_changed_outside\": 0", r.Text);
        Assert.True(File.Exists(Path.Combine(_dir, "form_erased.pdf")));
        var again = registry.Find("erase_content")!.InvokeAsync(args, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(again.IsError);                    // the default output exists now: never overwritten
    }
}
