// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Fonts;
using Washi.Core.Forms;

namespace Washi.Masume.Tests;

public sealed class FontEmbeddingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-font-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A TrueType font that exists on the machine running the tests, if any.</summary>
    private static string? SystemFont() => new[]
    {
        "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",
        @"C:\\Windows\\Fonts\\arial.ttf",
    }.FirstOrDefault(File.Exists);

    /// <summary>The smallest sfnt that has an OS/2 table with the given fsType.</summary>
    private static byte[] TinySfnt(ushort fsType)
    {
        var b = new byte[12 + 16 + 96];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 1);
        "OS/2"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 28);   // offset
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), 96);   // length
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(28 + 8), fsType);
        return b;
    }

    [Theory]
    [InlineData(0x0002)]   // restricted
    [InlineData(0x0004)]   // preview & print only: would make the form read-only
    [InlineData(0x0200)]   // bitmap embedding only
    public void Fonts_whose_licence_forbids_editable_embedding_are_refused(ushort fsType)
    {
        var e = Assert.Throws<FontNotUsableException>(() => OpenTypeFont.Parse(TinySfnt(fsType)));
        Assert.Equal("license", e.Reason);
        Assert.Equal(fsType, e.FsType);
    }

    [Fact]
    public void Files_that_are_not_fonts_are_refused() =>
        Assert.Equal("format", Assert.Throws<FontNotUsableException>(() => OpenTypeFont.Parse("%PDF-1.7 not a font"u8.ToArray())).Reason);

    [Fact]
    public void Truncated_fonts_are_refused_not_crashed()
    {
        var b = TinySfnt(0)[..20];
        Assert.Equal("format", Assert.Throws<FontNotUsableException>(() => OpenTypeFont.Parse(b)).Reason);
    }

    [Fact]
    public void Reads_a_real_font()
    {
        if (SystemFont() is not { } path) return;   // no known font on this machine
        var f = OpenTypeFont.Load(path);
        Assert.True(f.GlyphCount > 100);
        Assert.True(f.UnicodeToGlyph.TryGetValue('A', out var gA) && gA > 0);
        Assert.True(f.Advances[gA] > 0);
        Assert.False(string.IsNullOrEmpty(f.PostScriptName));
    }

    [Fact]
    public void Embedded_font_is_written_as_a_type0_font_the_fields_use()
    {
        if (SystemFont() is not { } path) return;
        var font = OpenTypeFont.Load(path);
        var src = Path.Combine(_dir, "in.pdf");
        var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(src);
        var dst = Path.Combine(_dir, "out.pdf");
        AcroFormWriter.Write(src, dst, [new FormField(0, FormFieldKind.Text, new PdfRect(50, 50, 300, 80), "name")],
                             FormScript.Japanese, font);

        using var outDoc = PdfReader.Open(dst, PdfDocumentOpenMode.Import);
        var form = outDoc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;
        var widget = (PdfDictionary)((PdfReference)form.Elements.GetArray("/Fields")!.Elements[0]).Value;
        Assert.StartsWith($"/{AcroFormWriter.EmbeddedKey} ", widget.Elements.GetString("/DA"));

        var type0 = (PdfDictionary)((PdfReference)form.Elements.GetDictionary("/DR")!.Elements.GetDictionary("/Font")!
                                     .Elements["/" + AcroFormWriter.EmbeddedKey]!).Value;
        Assert.Equal("/Type0", type0.Elements.GetName("/Subtype"));
        Assert.Equal("/Identity-H", type0.Elements.GetName("/Encoding"));
        Assert.Equal("/" + font.PostScriptName, type0.Elements.GetName("/BaseFont"));
        Assert.NotNull(type0.Elements["/ToUnicode"]);
        var cid = (PdfDictionary)((PdfReference)type0.Elements.GetArray("/DescendantFonts")!.Elements[0]).Value;
        var fd = (PdfDictionary)((PdfReference)cid.Elements["/FontDescriptor"]!).Value;
        Assert.True(fd.Elements.ContainsKey(font.IsCff ? "/FontFile3" : "/FontFile2"));
    }
}
