// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Tools;

namespace Washi.Masume.Tests;

public sealed class PagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-pages-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Fillable(string name)
    {
        var flat = Path.Combine(_dir, name + "_flat.pdf");
        ToolsTests.MakeFlatForm(flat);
        var path = Path.Combine(_dir, name + ".pdf");
        AcroFormWriter.Write(flat, path, FormPipeline.Detect(flat).Fields.Select(f => f.ToFormField()).ToList(), FormScript.Japanese);
        return path;
    }

    [Fact]
    public void Merged_forms_keep_every_field_with_unique_names()
    {
        var a = Fillable("a");
        int perForm = AcroFormFields.List(a).Count;
        var output = Path.Combine(_dir, "merged.pdf");

        Assert.Equal(3, PageAssembly.Merge([a, a, Path.Combine(_dir, "a_flat.pdf")], output));

        var fields = AcroFormFields.List(output);
        Assert.Equal(2 * perForm, fields.Count);
        Assert.Equal(fields.Count, fields.Select(f => f.Name).Distinct().Count());
        Assert.Contains(fields, f => f.Name.EndsWith("_2", StringComparison.Ordinal));

        // the two copies are separate fields: filling one leaves the other empty
        var first = fields.First(f => f.Type == "text" && !f.Comb).Name;
        var filled = Path.Combine(_dir, "filled.pdf");
        AcroFormFields.Fill(output, filled, new Dictionary<string, string> { [first] = "林 燁" });
        var after = AcroFormFields.List(filled);
        Assert.Single(after, f => f.Value == "林 燁");
        Assert.Equal(1, after.Count(f => f.Name == first));
    }

    [Fact]
    public void Split_parts_keep_their_own_fields_and_fonts()
    {
        var a = Fillable("a");
        var merged = Path.Combine(_dir, "merged.pdf");
        PageAssembly.Merge([a, a], merged);

        var parts = PageAssembly.Split(merged, PageAssembly.ParseRanges("each", 2), Path.Combine(_dir, "out"));

        Assert.Equal(["merged_p1.pdf", "merged_p2.pdf"], parts.Select(p => Path.GetFileName(p.Path)));
        foreach (var part in parts)
        {
            Assert.Equal(AcroFormFields.List(a).Count, AcroFormFields.List(part.Path).Count);
            using var doc = PdfReader.Open(part.Path, PdfDocumentOpenMode.Import);
            var dr = doc.AcroForm!.Elements.GetDictionary("/DR")!.Elements.GetDictionary("/Font")!;
            Assert.True(dr.Elements.ContainsKey("/" + AcroFormWriter.FontKey(FormScript.Japanese)));
        }
    }

    [Theory]
    [InlineData("1-3,5", 6, "1-3 5-5")]
    [InlineData(" each ", 2, "1-1 2-2")]
    [InlineData("", 1, "1-1")]
    public void Ranges_parse(string spec, int pages, string expected) =>
        Assert.Equal(expected, string.Join(' ', PageAssembly.ParseRanges(spec, pages).Select(r => $"{r.From}-{r.To}")));

    [Theory]
    [InlineData("0-2")]
    [InlineData("3-1")]
    [InlineData("1-9")]
    [InlineData("x")]
    public void Bad_ranges_are_refused(string spec) =>
        Assert.Throws<PageAssemblyException>(() => PageAssembly.ParseRanges(spec, 4));

    [Fact]
    public void Files_that_forbid_assembly_are_refused_not_unlocked()
    {
        var flat = Path.Combine(_dir, "flat.pdf");
        ToolsTests.MakeFlatForm(flat);
        var locked = Path.Combine(_dir, "locked.pdf");
        using (var doc = PdfReader.Open(flat, PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.OwnerPassword = "owner-only";
            doc.SecuritySettings.PermitAssembleDocument = false;
            doc.Save(locked);
        }
        var e = Assert.Throws<PageAssemblyException>(() => PageAssembly.Merge([flat, locked], Path.Combine(_dir, "m.pdf")));
        Assert.Contains("does not remove protection", e.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "m.pdf")));
    }

    // ------------------------------------------------------------------ organize
    /// <summary>Blank pages told apart by width (100, 200, ... points).</summary>
    private string Sized(string name, int pages)
    {
        var path = Path.Combine(_dir, name + ".pdf");
        using var doc = new PdfDocument();
        for (int i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(100 * i);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(400);
        }
        doc.Save(path);
        return path;
    }

    private static (int Width, int Rotate)[] Pages(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return [.. doc.Pages.Cast<PdfPage>().Select(p => ((int)Math.Round(p.MediaBox.Width), p.Rotate))];
    }

    [Fact]
    public void Organize_reorders_drops_and_turns_pages()
    {
        var src = Sized("doc", 4);
        var output = Path.Combine(_dir, "organized.pdf");

        Assert.Equal(3, PageAssembly.Organize([new(src, 3), new(src, 1, 90), new(src, 4, -90)], output));

        Assert.Equal([(300, 0), (100, 90), (400, 270)], Pages(output));
    }

    [Fact]
    public void Organize_inserts_pages_from_another_file()
    {
        var src = Sized("doc", 2);
        var form = Fillable("form");
        var output = Path.Combine(_dir, "organized.pdf");

        PageAssembly.Organize([new(src, 1), new(form, 1), new(src, 2)], output);

        var pages = Pages(output);
        Assert.Equal(3, pages.Length);
        Assert.Equal(100, pages[0].Width);
        Assert.Equal(200, pages[2].Width);
        Assert.Equal(AcroFormFields.List(form).Count, AcroFormFields.List(output).Count);
    }

    [Fact]
    public void Organized_form_keeps_its_fields_and_turns_their_widgets_with_the_page()
    {
        var a = Fillable("a");
        var two = Path.Combine(_dir, "two.pdf");
        PageAssembly.Merge([a, a], two);
        int total = AcroFormFields.List(two).Count;
        var output = Path.Combine(_dir, "organized.pdf");

        PageAssembly.Organize([new(two, 2), new(two, 1, 90)], output);

        var fields = AcroFormFields.List(output);
        Assert.Equal(total, fields.Count);
        Assert.Equal(fields.Count, fields.Select(f => f.Name).Distinct().Count());
        using (var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            int R(PdfPage page) => page.Annotations.Cast<PdfSharp.Pdf.Annotations.PdfAnnotation>()
                .Select(w => w.Elements.GetDictionary("/MK")?.Elements.GetInteger("/R") ?? 0).Distinct().Single();
            Assert.Equal(0, R(doc.Pages[0]));
            Assert.Equal(90, R(doc.Pages[1]));   // typed text stays upright on the turned page
        }

        // still fillable
        var name = fields.First(f => f.Type == "text" && !f.Comb).Name;
        var filled = Path.Combine(_dir, "filled.pdf");
        AcroFormFields.Fill(output, filled, new Dictionary<string, string> { [name] = "山田 花子" });
        Assert.Single(AcroFormFields.List(filled), f => f.Value == "山田 花子");
    }

    [Fact]
    public void Organize_refuses_bad_lists_before_writing()
    {
        var src = Sized("doc", 2);
        var output = Path.Combine(_dir, "organized.pdf");
        Assert.Equal("Err_PageTwice", Assert.Throws<PageAssemblyException>(() => PageAssembly.Organize([new(src, 1), new(src, 1, 90)], output)).Key);
        Assert.Equal("Err_BadRotation", Assert.Throws<PageAssemblyException>(() => PageAssembly.Organize([new(src, 1, 45)], output)).Key);
        Assert.Equal("Err_RangeOutside", Assert.Throws<PageAssemblyException>(() => PageAssembly.Organize([new(src, 3)], output)).Key);
        Assert.Equal("Err_NoPages", Assert.Throws<PageAssemblyException>(() => PageAssembly.Organize([], output)).Key);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("3,1-2", "2:-90", 4, "3/0 1/0 2/-90")]
    [InlineData("", "1:180", 2, "1/180 2/0")]
    public void Page_picks_parse(string order, string rotate, int pages, string expected) =>
        Assert.Equal(expected, string.Join(' ', PageAssembly.ParsePicks("x.pdf", order, rotate, pages).Select(p => $"{p.Page}/{p.Rotate}")));

    [Theory]
    [InlineData("2")]
    [InlineData("2:45")]
    [InlineData("9:90")]
    public void Bad_rotations_are_refused(string rotate) =>
        Assert.Throws<PageAssemblyException>(() => PageAssembly.ParsePicks("x.pdf", "", rotate, 4));
}
