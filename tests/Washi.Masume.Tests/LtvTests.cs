// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;
using Washi.Core.Signatures;

namespace Washi.Masume.Tests;

/// <summary>Long-term validation (DSS, document time-stamps), certification (DocMDP) and what later revisions may change,
/// against a certificate authority served on this computer (<see cref="TestPki"/>).</summary>
public sealed class LtvTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-ltv-").FullName;
    private readonly TestPki _pki = new();
    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string P(string name) => Path.Combine(_dir, name);
    private IReadOnlyList<SignatureCheck> Verify(string path) => PdfSignatureVerifier.Verify(path, [_pki.Root]);

    private string Flat()
    {
        if (!File.Exists(P("flat.pdf"))) ToolsTests.MakeFlatForm(P("flat.pdf"));
        return P("flat.pdf");
    }

    /// <summary>A form with two signature fields and a text field.</summary>
    private string Form()
    {
        if (!File.Exists(P("form.pdf")))
            AcroFormWriter.Write(Flat(), P("form.pdf"),
            [
                new FormField(0, FormFieldKind.Signature, new PdfRect(40, 200, 240, 250), "Author"),
                new FormField(0, FormFieldKind.Signature, new PdfRect(300, 200, 500, 250), "Approver"),
                new FormField(0, FormFieldKind.Text, new PdfRect(40, 300, 240, 320), "Name"),
            ], FormScript.Japanese);
        return P("form.pdf");
    }

    /// <summary>An incremental update made by hand, as another program would save one.</summary>
    private static void Update(string input, string output, Action<PdfDocument, Action<PdfObject>, Func<string, int>> change)
    {
        var bytes = File.ReadAllBytes(input);
        var update = new IncrementalUpdate(bytes);
        using (var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import))
        {
            int highest = doc.Internals.GetAllObjects().Select(o => o.Reference?.ObjectNumber ?? 0).Max();
            change(doc, d => update.Put(d.Reference!.ObjectNumber, d.Reference.GenerationNumber, PdfSyntax.Write(d)),
                   body => { int n = update.NewNumber(highest); update.Put(n, 0, body); return n; });
        }
        File.WriteAllBytes(output, update.Build());
    }

    private static void AddAnnotation(PdfPage page, int number, Action<PdfObject> put)
    {
        if (page.Elements["/Annots"] is PdfReference r) { ((PdfArray)r.Value).Elements.Add(new PdfLiteral($"{number} 0 R")); put((PdfArray)r.Value); }
        else { ((PdfArray)page.Elements["/Annots"]!).Elements.Add(new PdfLiteral($"{number} 0 R")); put(page); }
    }

    private static PdfDictionary FieldNamed(PdfDocument doc, string name) =>
        (PdfDictionary)((PdfArray)((PdfDictionary)((PdfReference)doc.Internals.Catalog.Elements["/AcroForm"]!).Value).Elements.GetObject("/Fields")!)
            .Elements.Select(e => ((PdfReference)e).Value).OfType<PdfDictionary>().First(f => f.Elements.GetString("/T") == name);

    [Fact]
    public void Signing_with_ltv_keeps_the_chain_and_revocation_answers_and_verifies_offline()
    {
        var id = _pki.Id(_dir);
        var r = PdfDigitalSigner.Sign(Flat(), P("ltv.pdf"), new SignRequest(id, Reason: "承認", TimestampServer: _pki.TsaUrl, Ltv: true));
        Assert.True(r.Timestamped);
        Assert.True(r.Ltv, string.Join("; ", r.Notes));
        Assert.True(_pki.OcspRequests >= 2, $"{_pki.OcspRequests} OCSP requests");   // the signer's and the time-stamping authority's
        Assert.True(_pki.CrlRequests >= 1, $"{_pki.CrlRequests} CRL requests");      // the intermediate's, from the root's list

        _pki.Down = true;   // from here on, nothing can be fetched
        var check = Assert.Single(Verify(P("ltv.pdf")));
        Assert.Equal("valid", check.Status);
        Assert.True(check is { Intact: true, Trusted: true, Ltv: true, Timestamped: true, Revoked: false, Kind: "approval" });
        Assert.False(check.CoversDocument);
        Assert.Equal(["validation"], check.LaterChanges);
        Assert.True(check.ChangesAllowed);
        Assert.Empty(check.Problems);

        using var doc = PdfReader.Open(P("ltv.pdf"), PdfDocumentOpenMode.Import);
        var dss = Dss.Read(doc);
        Assert.True(dss.Certs.Count >= 4, $"{dss.Certs.Count} certificates");   // signer, time-stamping, intermediate, root
        Assert.True(dss.Ocsps.Count >= 2 && dss.Crls.Count >= 1);
        Assert.Equal("/DSS", ((PdfDictionary)((PdfReference)doc.Internals.Catalog.Elements["/DSS"]!).Value).Elements.GetName("/Type"));

        // without the root trusted, the identity is unverified but the data is still all there
        var untrusted = Assert.Single(PdfSignatureVerifier.Verify(P("ltv.pdf")));
        Assert.Equal("unverified", untrusted.Status);
        Assert.True(untrusted.Ltv);
    }

    [Fact]
    public void Validation_and_a_document_time_stamp_are_added_to_a_signed_file_and_renewed()
    {
        var id = _pki.Id(_dir);
        PdfDigitalSigner.Sign(Flat(), P("signed.pdf"), new SignRequest(id, Page: 0, Rect: new PdfRect(40, 40, 240, 90)));
        Assert.False(Assert.Single(Verify(P("signed.pdf"))).Ltv);

        var r = PdfLtv.AddValidation(P("signed.pdf"), P("lta.pdf"), _pki.TsaUrl, [_pki.Root]);
        Assert.True(r.Timestamped);
        Assert.Equal(["Signature1", "Timestamp1"], r.Ltv.Order());
        Assert.Empty(r.NotLtv);
        Assert.True(r.Certificates >= 3 && r.Ocsps >= 2, $"{r.Certificates} certificates, {r.Ocsps} OCSP");

        var before = File.ReadAllBytes(P("signed.pdf"));
        Assert.True(File.ReadAllBytes(P("lta.pdf")).AsSpan(0, before.Length).SequenceEqual(before));   // only added to

        _pki.Down = true;
        var checks = Verify(P("lta.pdf")).ToDictionary(c => c.Field);
        Assert.Equal("valid", checks["Signature1"].Status);
        Assert.True(checks["Signature1"].Ltv);
        Assert.Equal(["validation", "timestamp"], checks["Signature1"].LaterChanges);
        var stamp = checks["Timestamp1"];
        Assert.Equal(("timestamp", "valid", "Washi Test Time-Stamping"), (stamp.Kind, stamp.Status, stamp.Signer));
        Assert.True(stamp is { Intact: true, Timestamped: true, Ltv: true, Page: 0 });
        Assert.Equal(["validation"], stamp.LaterChanges);   // its own chain's data came after it

        // nothing new to add; a second time-stamp renews the first
        Assert.Equal("Err_LtvNothing", Assert.Throws<PageAssemblyException>(() => PdfLtv.AddValidation(P("lta.pdf"), P("again.pdf"), null, [_pki.Root])).Key);
        _pki.Down = false;
        var renewed = PdfLtv.AddValidation(P("lta.pdf"), P("renewed.pdf"), _pki.TsaUrl, [_pki.Root]);
        Assert.Contains("Timestamp2", renewed.Ltv);
        Assert.All(Verify(P("renewed.pdf")), c => Assert.Equal("valid", c.Status));

        PdfiumDocument.Gate.Wait();
        try { using var d = PdfiumDocument.Open(P("renewed.pdf")); Assert.Equal(1, d.PageCount); }
        finally { PdfiumDocument.Gate.Release(); }
        Assert.Equal("Err_LtvNone", Assert.Throws<PageAssemblyException>(() => PdfLtv.AddValidation(Flat(), P("none.pdf"))).Key);
    }

    [Fact]
    public void A_certification_allows_signing_and_form_filling_and_nothing_else()
    {
        var author = _pki.Id(_dir, "Author");
        PdfDigitalSigner.Sign(Form(), P("certified.pdf"), new SignRequest(author, Field: "Author", Certify: 2));
        var c = Assert.Single(Verify(P("certified.pdf")));
        Assert.Equal(("certification", 2, "valid"), (c.Kind, c.Permissions, c.Status));

        // a second signature, then a value typed into the form: both allowed
        PdfDigitalSigner.Sign(P("certified.pdf"), P("approved.pdf"), new SignRequest(_pki.Id(_dir, "Approver"), Field: "Approver"));
        Update(P("approved.pdf"), P("filled.pdf"), (doc, put, _) =>
        {
            var name = FieldNamed(doc, "Name");
            name.Elements["/V"] = new PdfString("Taro");
            put(name);
        });
        var filled = Verify(P("filled.pdf")).ToDictionary(x => x.Field);
        Assert.Equal(["signature", "form"], filled["Author"].LaterChanges);
        Assert.Equal("valid", filled["Author"].Status);
        Assert.Equal(["form"], filled["Approver"].LaterChanges);
        Assert.Equal("valid", filled["Approver"].Status);

        // a comment is not allowed at level 2, a change to the page never
        Update(P("filled.pdf"), P("commented.pdf"), (doc, put, add) =>
        {
            var page = doc.Pages[0];
            int note = add($"<</Type/Annot/Subtype/Text/Rect[100 100 120 120]/Contents(hello)/P {page.Reference!.ObjectNumber} 0 R>>");
            AddAnnotation(page, note, put);
        });
        var commented = Verify(P("commented.pdf")).First(x => x.Field == "Author");
        Assert.Contains("comments", commented.LaterChanges);
        Assert.Equal("changed", commented.Status);
        Assert.Contains(commented.Problems, p => p.Contains("comments"));

        Update(P("filled.pdf"), P("edited.pdf"), (doc, put, add) =>
        {
            var page = doc.Pages[0];
            int content = add("<</Length 18>>\nstream\n0 0 m 100 100 l S\nendstream");
            page.Elements["/Contents"] = new PdfLiteral($"{content} 0 R");
            put(page);
        });
        Assert.All(Verify(P("edited.pdf")), x => Assert.Equal("changed", x.Status));
        Assert.Contains("other", Verify(P("edited.pdf"))[0].LaterChanges);

        // only the first signature certifies
        Assert.Equal("Err_CertifyNotFirst", Assert.Throws<PageAssemblyException>(() =>
            PdfDigitalSigner.Sign(P("approved.pdf"), P("x.pdf"), new SignRequest(author, Certify: 2))).Key);
    }

    [Fact]
    public void A_certification_with_no_changes_refuses_signatures_but_takes_validation_data_and_level_3_takes_comments()
    {
        var author = _pki.Id(_dir, "Author");
        PdfDigitalSigner.Sign(Form(), P("locked.pdf"), new SignRequest(author, Field: "Author", Certify: 1));
        Assert.Equal("Err_SignCertifiedLocked", Assert.Throws<PageAssemblyException>(() =>
            PdfDigitalSigner.Sign(P("locked.pdf"), P("x.pdf"), new SignRequest(author, Field: "Approver"))).Key);
        PdfLtv.AddValidation(P("locked.pdf"), P("locked-ltv.pdf"), _pki.TsaUrl, [_pki.Root]);
        var locked = Verify(P("locked-ltv.pdf")).First(x => x.Field == "Author");
        Assert.Equal((1, "valid"), (locked.Permissions, locked.Status));
        Assert.True(locked.Ltv);

        PdfDigitalSigner.Sign(Form(), P("open.pdf"), new SignRequest(author, Field: "Author", Certify: 3));
        Update(P("open.pdf"), P("open-commented.pdf"), (doc, put, add) =>
        {
            var page = doc.Pages[0];
            int note = add($"<</Type/Annot/Subtype/Text/Rect[100 100 120 120]/Contents(ok)/P {page.Reference!.ObjectNumber} 0 R>>");
            AddAnnotation(page, note, put);
        });
        var open = Assert.Single(Verify(P("open-commented.pdf")));
        Assert.Equal((3, "valid"), (open.Permissions, open.Status));
        Assert.Equal(["comments"], open.LaterChanges);
    }

    [Fact]
    public void A_revoked_certificate_invalidates_the_signature_unless_a_time_stamp_shows_it_was_made_before()
    {
        // revoked before signing
        var early = _pki.Id(_dir, "Early");
        _pki.Revoked[early.Certificate.SerialNumber] = DateTimeOffset.UtcNow.AddHours(-1);
        PdfDigitalSigner.Sign(Flat(), P("revoked.pdf"), new SignRequest(early, TimestampServer: _pki.TsaUrl, Ltv: true));
        var revoked = Assert.Single(Verify(P("revoked.pdf")));
        Assert.True(revoked.Revoked);
        Assert.Equal("invalid", revoked.Status);
        Assert.Contains(revoked.Problems, p => p.Contains("revoked"));

        // revoked after a time-stamped signature: still valid; without the time-stamp it cannot be shown
        var later = _pki.Id(_dir, "Later");
        PdfDigitalSigner.Sign(Flat(), P("stamped.pdf"), new SignRequest(later, TimestampServer: _pki.TsaUrl));
        PdfDigitalSigner.Sign(Flat(), P("unstamped.pdf"), new SignRequest(later));
        Thread.Sleep(1100);
        _pki.Revoked[later.Certificate.SerialNumber] = DateTimeOffset.UtcNow;
        PdfLtv.AddValidation(P("stamped.pdf"), P("stamped-ltv.pdf"), null, [_pki.Root]);
        PdfLtv.AddValidation(P("unstamped.pdf"), P("unstamped-ltv.pdf"), null, [_pki.Root]);
        var kept = Assert.Single(Verify(P("stamped-ltv.pdf")));
        Assert.Equal("valid", kept.Status);
        Assert.False(kept.Revoked);
        Assert.Equal("invalid", Assert.Single(Verify(P("unstamped-ltv.pdf"))).Status);
    }

    [Fact]
    public void When_the_services_cannot_be_reached_the_file_says_which_signatures_are_not_ltv()
    {
        var id = _pki.Id(_dir);
        PdfDigitalSigner.Sign(Flat(), P("signed.pdf"), new SignRequest(id));
        _pki.Down = true;
        var r = PdfLtv.AddValidation(P("signed.pdf"), P("partial.pdf"), null, [_pki.Root]);
        Assert.Equal(["Signature1"], r.NotLtv);
        Assert.Contains(r.Notes, n => n.Contains("127.0.0.1") && n.Contains("503"));
        Assert.Contains(r.Notes, n => n.Contains("no revocation information"));
        var check = Assert.Single(Verify(P("partial.pdf")));
        Assert.False(check.Ltv);
        Assert.Equal("valid", check.Status);
    }
}
