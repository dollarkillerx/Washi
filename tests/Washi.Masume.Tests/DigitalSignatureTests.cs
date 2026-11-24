// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;
using Washi.Core.Signatures;

namespace Washi.Masume.Tests;

/// <summary>Signing with a digital ID (PAdES, incremental) and checking signatures.</summary>
public sealed class DigitalSignatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-dsig-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private DigitalId Id(string name = "山田 太郎") => File.Exists(P(name + ".p12")) ? DigitalIds.FromFile(P(name + ".p12"), "secret-1")
                                                     : DigitalIds.Create(name, "taro@example.com", P(name + ".p12"), "secret-1");

    private string Flat()
    {
        if (!File.Exists(P("flat.pdf"))) ToolsTests.MakeFlatForm(P("flat.pdf"));
        return P("flat.pdf");
    }

    [Fact]
    public void A_digital_id_is_made_opened_and_refused_with_the_wrong_password()
    {
        var id = Id();
        Assert.Equal("山田 太郎", id.Name);
        Assert.True(id.SelfSigned && id.Certificate.HasPrivateKey);
        Assert.Equal("Err_IdOpen", Assert.Throws<PageAssemblyException>(() => DigitalIds.FromFile(P("山田 太郎.p12"), "wrong")).Key);
        Assert.Equal("Err_IdPassword", Assert.Throws<PageAssemblyException>(() => DigitalIds.Create("A", "", P("a.p12"), "123")).Key);
    }

    [Fact]
    public void An_invisible_signature_verifies_and_says_the_identity_is_self_signed()
    {
        var r = PdfDigitalSigner.Sign(Flat(), P("signed.pdf"), new SignRequest(Id(), Reason: "承認", Location: "東京"));
        Assert.Equal("Signature1", r.Field);
        var check = Assert.Single(PdfSignatureVerifier.Verify(P("signed.pdf")));
        Assert.True(check.Intact);
        Assert.True(check.CoversDocument);
        Assert.False(check.Trusted);
        Assert.Equal("unverified", check.Status);
        Assert.Equal(("山田 太郎", "承認", "東京", "ETSI.CAdES.detached"), (check.Signer, check.Reason, check.Location, check.SubFilter));
        Assert.Contains(check.Problems, p => p.Contains("self-signed"));
        // the original bytes are untouched: the signature is an update after them
        var before = File.ReadAllBytes(Flat());
        Assert.True(File.ReadAllBytes(P("signed.pdf")).AsSpan(0, before.Length).SequenceEqual(before));
    }

    [Fact]
    public void A_visible_signature_shows_on_the_page_and_a_changed_byte_breaks_it()
    {
        var ink = Ink.From([[(0.0, 0.0), (30, 20), (60, 5), (90, 25)]]);
        PdfDigitalSigner.Sign(Flat(), P("visible.pdf"), new SignRequest(Id(), Page: 0, Rect: new PdfRect(500, 300, 760, 360), Reason: "Approved", Ink: ink));
        var check = Assert.Single(PdfSignatureVerifier.Verify(P("visible.pdf")));
        Assert.True(check.Intact);
        Assert.Equal(0, check.Page);
        Assert.Equal(500, check.Rect.X0, 1);

        PdfiumDocument.Gate.Wait();
        try
        {
            using var d = PdfiumDocument.Open(P("visible.pdf"));
            var img = d.RenderPage(0, 1);
            int dark = 0;
            for (int y = 300; y < 360; y++)
                for (int x = 500; x < 760; x++)
                    if (img.Pixels[(y * img.Width + x) * 3] < 128) dark++;
            Assert.True(dark > 100, $"{dark} dark pixels in the signature box");
        }
        finally { PdfiumDocument.Gate.Release(); }

        // one byte changed inside the signed range: the signature no longer holds
        var bytes = File.ReadAllBytes(P("visible.pdf"));
        int at = Array.IndexOf(bytes, (byte)'/', 200);
        bytes[at + 1] ^= 0x20;
        File.WriteAllBytes(P("tampered.pdf"), bytes);
        var bad = PdfSignatureVerifier.Verify(P("tampered.pdf")).FirstOrDefault();
        Assert.True(bad is null || !bad.Intact);
    }

    [Fact]
    public void An_empty_signature_field_is_signed_and_a_second_signature_keeps_the_first()
    {
        AcroFormWriter.Write(Flat(), P("form.pdf"),
        [
            new FormField(0, FormFieldKind.Signature, new PdfRect(40, 200, 240, 250), "Applicant"),
            new FormField(0, FormFieldKind.Signature, new PdfRect(300, 200, 500, 250), "Manager"),
        ], FormScript.Japanese);
        PdfDigitalSigner.Sign(P("form.pdf"), P("one.pdf"), new SignRequest(Id(), Field: "Applicant"));
        PdfDigitalSigner.Sign(P("one.pdf"), P("two.pdf"), new SignRequest(Id("Sato Hanako"), Field: "Manager", Reason: "Checked"));
        var checks = PdfSignatureVerifier.Verify(P("two.pdf")).ToDictionary(c => c.Field);
        Assert.True(checks["Applicant"].Intact);                   // the first signature still holds
        Assert.False(checks["Applicant"].CoversDocument);          // and says a revision came after it
        Assert.Equal(1, checks["Applicant"].ChangedAfter);
        Assert.Equal(["signature"], checks["Applicant"].LaterChanges);   // only a signature, which it allows
        Assert.True(checks["Applicant"].ChangesAllowed);
        Assert.DoesNotContain(checks["Applicant"].Problems, p => p.Contains("changed after this signature"));
        Assert.True(checks["Manager"].Intact && checks["Manager"].CoversDocument);
        Assert.Equal("Sato Hanako", checks["Manager"].Signer);
        Assert.Equal("Err_SignFieldSigned", Assert.Throws<PageAssemblyException>(() =>
            PdfDigitalSigner.Sign(P("two.pdf"), P("three.pdf"), new SignRequest(Id(), Field: "Manager"))).Key);
        Assert.Equal(2, AcroFormFields.List(P("two.pdf")).Count);   // still a readable form
    }

    [Fact]
    public void Files_with_cross_reference_streams_are_signed_too_and_protected_ones_are_refused()
    {
        // a compressed copy has object streams and a cross-reference stream
        var bytes = ObjectStreams.Pack(File.ReadAllBytes(Flat()))!;
        File.WriteAllBytes(P("packed.pdf"), bytes);
        PdfDigitalSigner.Sign(P("packed.pdf"), P("packed-signed.pdf"), new SignRequest(Id(), Page: 0, Rect: new PdfRect(40, 40, 240, 90)));
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(P("packed-signed.pdf"))).Intact);
        using (PdfReader.Open(P("packed-signed.pdf"), PdfDocumentOpenMode.Import)) { }
        PdfiumDocument.Gate.Wait();
        try { using var d = PdfiumDocument.Open(P("packed-signed.pdf")); Assert.Equal(1, d.PageCount); }
        finally { PdfiumDocument.Gate.Release(); }

        PdfSecurity.Protect(Flat(), P("locked.pdf"), "open", null, PdfPermissions.All);
        Assert.Throws<PageAssemblyException>(() => PdfDigitalSigner.Sign(P("locked.pdf"), P("x.pdf"), new SignRequest(Id())));
        File.WriteAllText(P("taken.pdf"), "x");
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() => PdfDigitalSigner.Sign(Flat(), P("taken.pdf"), new SignRequest(Id()))).Key);
        Assert.Empty(PdfSignatureVerifier.Verify(Flat()));
    }
}
