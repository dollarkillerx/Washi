// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Washi.Core.Fonts;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Subsets of the CJK fonts of this computer draw exactly what the whole font draws.</summary>
public sealed class FontSubsetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-subset-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A page that draws <paramref name="text"/> in the font; <paramref name="subset"/> swaps the program for a subset.</summary>
    private string Draw(string name, OpenTypeFont font, string text, bool subset)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(400);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(120);
        var type0 = PdfFontEmbedder.Embed(doc, font);
        var codes = new StringBuilder();
        var glyphs = new HashSet<int>();
        foreach (var r in text.EnumerateRunes())
        {
            int gid = font.UnicodeToGlyph[r.Value];
            glyphs.Add(gid);
            codes.Append((font.IsCff ? font.GlyphToCid[gid] : gid).ToString("X4"));
        }
        if (subset)
        {
            var cid = (PdfDictionary)((PdfReference)type0.Elements.GetArray("/DescendantFonts")!.Elements[0]).Value;
            var fd = (PdfDictionary)((PdfReference)cid.Elements["/FontDescriptor"]!).Value;
            var program = (PdfDictionary)((PdfReference)fd.Elements[font.IsCff ? "/FontFile3" : "/FontFile2"]!).Value;
            var data = font.IsCff ? FontSubsetter.Cff(FontSubsetter.CffTable(font.Data), glyphs) : FontSubsetter.TrueType(font.Data, glyphs);
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            program.Stream.Value = ms.ToArray();
            program.Elements.SetInteger("/Length1", data.Length);
            if (font.IsCff) program.Elements.SetName("/Subtype", "/CIDFontType0C");
        }
        var res = new PdfDictionary(doc);
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F0"] = type0.Reference!;
        res.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = res;
        var content = new PdfDictionary(doc);
        content.CreateStream(Encoding.ASCII.GetBytes($"BT /F0 40 Tf 20 50 Td <{codes}> Tj ET"));
        doc.Internals.AddObject(content);
        page.Elements["/Contents"] = content.Reference!;
        var path = Path.Combine(_dir, name);
        doc.Save(path);
        return path;
    }

    private static byte[] Pixels(string pdf)
    {
        PdfiumDocument.Gate.Wait();
        try
        {
            using var d = PdfiumDocument.Open(pdf);
            return d.RenderPage(0, 2).Pixels;
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    [Theory]
    [InlineData("/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc", 0, "社外秘テスト株式会社、御中。")]   // CID-keyed CFF
    [InlineData("/System/Library/Fonts/STHeiti Light.ttc", 1, "机密文件测试，谢谢。")]                // TrueType
    public void A_subset_draws_what_the_whole_font_draws(string path, int face, string text)
    {
        Assert.SkipUnless(File.Exists(path), "the font is not on this computer");
        var font = OpenTypeFont.Load(path, face);
        var whole = Draw("whole.pdf", font, text, subset: false);
        var part = Draw("part.pdf", font, text, subset: true);
        var a = Pixels(whole);
        var b = Pixels(part);
        Assert.Equal(a.Length, b.Length);
        Assert.Contains(a, v => v < 128);                                   // something is drawn
        Assert.True(a.AsSpan().SequenceEqual(b), "the subset draws differently from the whole font");
        Assert.True(new FileInfo(part).Length < new FileInfo(whole).Length / 10);   // and is much smaller
    }
}
