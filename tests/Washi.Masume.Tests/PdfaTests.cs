// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Fonts;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;
using Washi.Core.Signatures;

namespace Washi.Masume.Tests;

/// <summary>PDF/A-2b: what stands in the way is listed, and a copy that conforms keeps every character where it was.</summary>
public sealed class PdfaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-pdfa-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static ArchiveResult Convert(string input, string output, bool flatten = false) => Locked(() => PdfArchive.Convert(input, output, flatten));

    /// <summary>
    /// A page drawn by hand with fonts it only names (Helvetica, Times-Bold, a Japanese font in UniJIS-UCS2-H), word and
    /// character spacing, a JavaScript that runs on opening, an attached file and a hidden note.
    /// </summary>
    private string Unembedded(string content, Func<PdfDocument, PdfDictionary>? extraFont = null, string name = "in.pdf")
    {
        var path = P(name);
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(600);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(400);
        PdfDictionary Simple(string baseFont)
        {
            var f = new PdfDictionary(doc);
            doc.Internals.AddObject(f);
            f.Elements.SetName("/Type", "/Font");
            f.Elements.SetName("/Subtype", "/Type1");
            f.Elements.SetName("/BaseFont", "/" + baseFont);
            f.Elements.SetName("/Encoding", "/WinAnsiEncoding");
            return f;
        }
        var fonts = new PdfDictionary(doc);
        fonts.Elements.SetReference("/F1", Simple("Helvetica"));
        fonts.Elements.SetReference("/F2", Simple("Times-Bold"));
        if (extraFont is not null) fonts.Elements.SetReference("/F3", extraFont(doc));
        var res = new PdfDictionary(doc);
        res.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = res;
        var stream = new PdfDictionary(doc);
        doc.Internals.AddObject(stream);
        stream.CreateStream(Encoding.Latin1.GetBytes(content));
        page.Elements.SetReference("/Contents", stream);

        var js = new PdfDictionary(doc);
        js.Elements.SetName("/S", "/JavaScript");
        js.Elements.SetString("/JS", "app.alert('hi')");
        doc.Internals.Catalog.Elements["/OpenAction"] = js;
        var note = new PdfDictionary(doc);
        doc.Internals.AddObject(note);
        note.Elements.SetName("/Type", "/Annot");
        note.Elements.SetName("/Subtype", "/Text");
        note.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(10), new PdfInteger(10), new PdfInteger(30), new PdfInteger(30));
        note.Elements.SetInteger("/F", 2);
        note.Elements.SetString("/Contents", "hidden");
        page.Elements["/Annots"] = new PdfArray(doc, note.Reference!);
        doc.Info.Title = "Archive test – 試験";
        doc.Info.Author = "Washi";
        doc.Save(path);
        return path;
    }

    private const string Latin =
        "BT /F1 14 Tf 40 340 Td (Hello, archive world: AVA WAVE) Tj 0 -24 Td 2 Tw 0.5 Tc (word spacing and letters) Tj " +
        "0 -24 Td 0 Tw 0 Tc [(Kerned) -250 (T) 80 (o) 120 (day)] TJ /F2 12 Tf 0 -24 Td (Bold Times \\(with brackets\\)) Tj " +
        "(still Times) ' 1 0.2 (and a quote) \" ET";

    private static string Xmp(string pdf)
    {
        using var d = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var m = (PdfDictionary)((PdfReference)d.Internals.Catalog.Elements["/Metadata"]!).Value;
        return Encoding.UTF8.GetString(m.Stream.UnfilteredValue);
    }

    [Fact]
    public void Standard_fonts_are_embedded_from_this_computer_and_every_character_stays_in_place()
    {
        var input = Unembedded(Latin);
        var found = PdfArchive.Check(input).ToDictionary(i => i.Kind);
        Assert.Equal(["Helvetica", "Times-Bold"], found["Fonts"].Details.Order());
        Assert.Contains("JavaScript", found["Scripts"].Details);
        Assert.True(found.ContainsKey("OutputIntent") && found.ContainsKey("Metadata") && found.ContainsKey("Annotations"));

        var r = Convert(input, P("out.pdf"));   // checks again, compares text positions and pixels, or throws
        Assert.Equal("PDF/A-2b", r.Conformance);
        Assert.Equal(2, r.Changes.Single(c => c.Kind == "Fonts").Count);
        Assert.Empty(PdfArchive.Check(P("out.pdf")));
        using (var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import))
        {
            Assert.Empty(FontReplacer.Missing(d));
            Assert.False(d.Internals.Catalog.Elements.ContainsKey("/OpenAction"));
            Assert.False(d.Pages[0].Elements.ContainsKey("/Annots"));   // the hidden note showed nothing and is gone
            Assert.StartsWith("%PDF-1.7", Encoding.Latin1.GetString(File.ReadAllBytes(P("out.pdf")), 0, 8));
        }
        var xmp = Xmp(P("out.pdf"));
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
        Assert.Contains("Archive test – 試験", xmp);
        Locked(() =>
        {
            using var d = PdfiumDocument.Open(P("out.pdf"));
            var text = d.GetPageText(0);
            Assert.Contains("Hello, archive world", text);
            Assert.Contains("Bold Times (with brackets)", text);
            return 0;
        });
    }

    [Fact]
    public void A_Japanese_font_that_is_only_named_is_replaced_and_its_characters_keep_their_places()
    {
        PdfDictionary Mincho(PdfDocument doc)
        {
            var desc = new PdfDictionary(doc);
            doc.Internals.AddObject(desc);
            desc.Elements.SetName("/Type", "/FontDescriptor");
            desc.Elements.SetName("/FontName", "/KozMinPr6N-Regular");
            desc.Elements.SetInteger("/Flags", 6);
            desc.Elements["/FontBBox"] = new PdfArray(doc, new PdfInteger(-195), new PdfInteger(-272), new PdfInteger(1110), new PdfInteger(1075));
            desc.Elements.SetInteger("/Ascent", 880); desc.Elements.SetInteger("/Descent", -120);
            desc.Elements.SetInteger("/CapHeight", 742); desc.Elements.SetInteger("/StemV", 80); desc.Elements.SetInteger("/ItalicAngle", 0);
            var info = new PdfDictionary(doc);
            info.Elements.SetString("/Registry", "Adobe"); info.Elements.SetString("/Ordering", "Japan1"); info.Elements.SetInteger("/Supplement", 6);
            var cid = new PdfDictionary(doc);
            doc.Internals.AddObject(cid);
            cid.Elements.SetName("/Type", "/Font"); cid.Elements.SetName("/Subtype", "/CIDFontType0");
            cid.Elements.SetName("/BaseFont", "/KozMinPr6N-Regular");
            cid.Elements["/CIDSystemInfo"] = info;
            cid.Elements.SetReference("/FontDescriptor", desc);
            cid.Elements.SetInteger("/DW", 1000);
            // proportional Latin (CIDs 1-95) at 500, as such files give them
            cid.Elements["/W"] = new PdfArray(doc, new PdfInteger(1), new PdfInteger(95), new PdfInteger(500));
            var f = new PdfDictionary(doc);
            doc.Internals.AddObject(f);
            f.Elements.SetName("/Type", "/Font"); f.Elements.SetName("/Subtype", "/Type0");
            f.Elements.SetName("/BaseFont", "/KozMinPr6N-Regular");
            f.Elements.SetName("/Encoding", "/UniJIS-UCS2-H");
            f.Elements["/DescendantFonts"] = new PdfArray(doc, cid.Reference!);
            return f;
        }
        var hex = System.Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("東京都千代田区 ABC 申告書"));
        var input = Unembedded($"BT /F3 16 Tf 40 300 Td <{hex}> Tj ET " + Latin, Mincho);
        Assert.Contains("KozMinPr6N-Regular", PdfArchive.Check(input).Single(i => i.Kind == "Fonts").Details);

        var r = Convert(input, P("out.pdf"));
        Assert.Contains(r.Changes.Single(c => c.Kind == "Fonts").Details, d => d.StartsWith("KozMinPr6N-Regular → ", StringComparison.Ordinal));
        Assert.Empty(PdfArchive.Check(P("out.pdf")));
        Locked(() =>
        {
            using var d = PdfiumDocument.Open(P("out.pdf"));
            Assert.Contains("東京都千代田区", d.GetPageText(0));
            return 0;
        });
    }

    [Fact]
    public void A_filled_Washi_form_stays_fillable_or_is_flattened_first()
    {
        var blank = P("blank.pdf");
        using (var d = new PdfDocument()) { d.AddPage(); d.Save(blank); }
        AcroFormWriter.Write(blank, P("form.pdf"), [new FormField(0, FormFieldKind.Text, new PdfRect(40, 300, 260, 324), "氏名")], FormScript.Japanese);
        AcroFormFields.Fill(P("form.pdf"), P("filled.pdf"), new Dictionary<string, string> { ["氏名"] = "山田 太郎" });

        Convert(P("filled.pdf"), P("fillable.pdf"));
        Assert.Empty(PdfArchive.Check(P("fillable.pdf")));
        Assert.Equal("山田 太郎", Assert.Single(AcroFormFields.List(P("fillable.pdf"))).Value);

        var r = Convert(P("filled.pdf"), P("flat.pdf"), flatten: true);
        Assert.Equal("Flattened", r.Changes[0].Kind);
        Assert.Empty(AcroFormFields.List(P("flat.pdf")));
        Assert.Empty(PdfArchive.Check(P("flat.pdf")));
    }

    [Fact]
    public void A_file_painted_in_CMYK_gets_a_CMYK_output_intent_and_its_RGB_goes_through_sRGB()
    {
        if (!OperatingSystem.IsMacOS()) return;   // the CMYK profile comes from the system
        var input = Unembedded("0 0.5 1 0 k 40 40 200 100 re f 1 0 0 rg 300 40 200 100 re f " + Latin);
        Assert.Contains(PdfArchive.Check(input), i => i.Kind == "Cmyk");
        Convert(input, P("out.pdf"));
        Assert.Empty(PdfArchive.Check(P("out.pdf")));
        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        var intent = (PdfDictionary)((PdfArray)d.Internals.Catalog.Elements.GetObject("/OutputIntents")!).Elements.GetDictionary(0)!;
        Assert.Equal(4, ((PdfDictionary)((PdfReference)intent.Elements["/DestOutputProfile"]!).Value).Elements.GetInteger("/N"));
    }

    [Fact]
    public void Signed_files_and_existing_outputs_are_refused()
    {
        var input = Unembedded(Latin);
        var id = DigitalIds.Create("山田 太郎", "taro@example.com", P("id.p12"), "secret-1");
        PdfDigitalSigner.Sign(input, P("signed.pdf"), new SignRequest(id, Page: 0, Rect: new PdfRect(300, 300, 500, 350)));
        Assert.Equal("Err_PdfaSigned", Assert.Throws<PageAssemblyException>(() => Convert(P("signed.pdf"), P("a.pdf"))).Key);
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() => Convert(input, input)).Key);
        Assert.False(File.Exists(P("a.pdf")));
    }

    [Theory]
    [InlineData("D:20261123203141+09'00'", "2026-11-23T20:31:41+09:00")]
    [InlineData("D:20200101000000Z", "2020-01-01T00:00:00Z")]
    [InlineData("D:2020", "2020")]
    public void Document_dates_are_written_as_XMP_dates(string pdf, string xmp) => Assert.Equal(xmp, PdfArchive.XmpDate(pdf));
}
