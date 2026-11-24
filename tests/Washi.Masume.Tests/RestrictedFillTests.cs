// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Forms protected with a permissions password that allow filling in, filled without that password (as the
/// 国税庁 input forms are, and as Acrobat fills them): the protection stays as it is.</summary>
public sealed class RestrictedFillTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-restricted-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string P(string name) => Path.Combine(_dir, name);

    private string Form()
    {
        if (File.Exists(P("form.pdf"))) return P("form.pdf");
        using (var d = new PdfDocument()) { d.AddPage(); d.Save(P("blank.pdf")); }
        AcroFormWriter.Write(P("blank.pdf"), P("form.pdf"),
        [
            new FormField(0, FormFieldKind.Text, new PdfRect(40, 40, 300, 70), "氏名"),
            new FormField(0, FormFieldKind.Checkbox, new PdfRect(40, 100, 54, 114), "同意"),
        ], FormScript.Japanese);
        return P("form.pdf");
    }

    /// <summary>The form protected with "owner-1" (and <paramref name="open"/> to open it), allowing printing and
    /// <paramref name="fill"/> only, in the given scheme.</summary>
    private string Locked(string name, Action<PdfSharp.Pdf.Security.PdfStandardSecurityHandler> scheme, bool fill = true, string open = "")
    {
        using var d = PdfReader.Open(Form(), PdfDocumentOpenMode.Modify);
        var s = d.SecuritySettings;
        s.UserPassword = open;
        s.OwnerPassword = "owner-1";
        s.PermitPrint = s.PermitFullQualityPrint = true;
        s.PermitExtractContent = s.PermitModifyDocument = s.PermitAnnotations = s.PermitAssembleDocument = false;
        s.PermitFormsFill = fill;
        scheme(d.SecurityHandler);
        d.Save(P(name));
        return P(name);
    }

    private static int Dark(string pdf, PdfRect box)
    {
        PdfiumDocument.Gate.Wait();
        try
        {
            using var d = PdfiumDocument.Open(pdf);
            var img = d.RenderPage(0, 1);
            int n = 0;
            for (int y = (int)box.Y0; y < (int)box.Y1; y++)
                for (int x = (int)box.X0; x < (int)box.X1; x++)
                    if (img.Pixels[(y * img.Width + x) * 3] < 128) n++;
            return n;
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    public static TheoryData<string> Schemes => ["AES-256", "AES-128", "RC4-128 (V4)", "RC4-128 (V2)"];

    [Theory]
    [MemberData(nameof(Schemes))]
    public void A_form_that_allows_filling_in_is_filled_without_its_permissions_password_and_stays_protected(string scheme)
    {
        var input = Locked("locked.pdf", h =>
        {
            switch (scheme)
            {
                case "AES-256": h.SetEncryptionToV5(); break;
                case "AES-128": h.SetEncryptionToV4UsingAES(true); break;
                case "RC4-128 (V4)": h.SetEncryptionToV4UsingRC4(true); break;
                default: h.SetEncryptionToV2With128Bits(); break;
            }
        });
        var before = PdfSecurity.Inspect(input);
        Assert.False(before.FullAccess);
        Assert.Equal("Err_NeedsPermissionsPassword", Assert.Throws<PageAssemblyException>(() => PdfSecurity.Unlock(input, P("x.pdf"), "")).Key);

        var r = AcroFormFields.Fill(input, P("filled.pdf"), new Dictionary<string, string> { ["氏名"] = "山田 太郎", ["同意"] = "true" });
        Assert.Equal(2, r.Filled);
        var values = AcroFormFields.List(P("filled.pdf")).ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal(("山田 太郎", "Yes"), (values["氏名"], values["同意"]));

        // the protection is the file's own: same method, same restrictions, still no full access without the password
        var after = PdfSecurity.Inspect(P("filled.pdf"));
        Assert.Equal((true, false, before.Method, before.Permissions), (after.Encrypted, after.FullAccess, after.Method, after.Permissions));
        Assert.True(PdfSecurity.Inspect(P("filled.pdf"), "owner-1").FullAccess);
        // only added to: everything before is byte for byte the original
        var original = File.ReadAllBytes(input);
        Assert.True(File.ReadAllBytes(P("filled.pdf")).AsSpan(0, original.Length).SequenceEqual(original));
        // PDFium decrypts on its own and draws the value
        Assert.True(Dark(P("filled.pdf"), new PdfRect(40, 40, 300, 70)) > Dark(input, new PdfRect(40, 40, 300, 70)) + 50);
    }

    [Fact]
    public void A_form_that_does_not_allow_filling_in_is_refused_and_an_open_password_is_used_when_known()
    {
        var closed = Locked("closed.pdf", h => h.SetEncryptionToV5(), fill: false);
        Assert.Equal("Err_FormLocked", Assert.Throws<PageAssemblyException>(() =>
            AcroFormFields.Fill(closed, P("x.pdf"), new Dictionary<string, string> { ["氏名"] = "x" })).Key);
        Assert.False(File.Exists(P("x.pdf")));

        var both = Locked("both.pdf", h => h.SetEncryptionToV4UsingAES(true), open: "open-1");
        Assert.ThrowsAny<Exception>(() => AcroFormFields.Fill(both, P("y.pdf"), new Dictionary<string, string> { ["氏名"] = "x" }));
        PdfPasswords.Remember(both, "open-1");
        try
        {
            AcroFormFields.Fill(both, P("y.pdf"), new Dictionary<string, string> { ["氏名"] = "鈴木" });
            PdfPasswords.Remember(P("y.pdf"), "open-1");
            Assert.Equal("鈴木", AcroFormFields.List(P("y.pdf")).Single(f => f.Name == "氏名").Value);
            Assert.True(PdfSecurity.Inspect(P("y.pdf")).NeedsPassword);   // still asks for its password to open
        }
        finally { PdfPasswords.Forget(both); PdfPasswords.Forget(P("y.pdf")); }
    }

    [Fact]
    public void Form_data_and_a_table_fill_restricted_forms_too()
    {
        var input = Locked("locked.pdf", h => h.SetEncryptionToV4UsingAES(true));
        File.WriteAllText(P("t.csv"), "氏名,同意\r\n山田 太郎,yes\r\n佐藤 花子,no\r\n");
        var r = PdfFormData.FillMany(input, P("t.csv"), P("out"));
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal("佐藤 花子", AcroFormFields.List(r.Rows[1].Output).Single(f => f.Name == "氏名").Value);
        Assert.True(PdfSecurity.Inspect(r.Rows[1].Output) is { Encrypted: true, FullAccess: false });
    }
}
