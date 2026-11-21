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
        AcroFormFields.Fill(output, filled, new Dictionary<string, string> { [first] = "王 燁" });
        var after = AcroFormFields.List(filled);
        Assert.Single(after, f => f.Value == "王 燁");
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
}
