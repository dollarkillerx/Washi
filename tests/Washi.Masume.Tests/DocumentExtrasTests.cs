// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Signatures;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Page labels, attachments and layers.</summary>
public sealed class DocumentExtrasTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-extras-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string P(string name) => Path.Combine(_dir, name);

    private string Pages(int count, string name = "pages.pdf")
    {
        using var d = new PdfDocument();
        for (int i = 0; i < count; i++) d.AddPage();
        d.Save(P(name));
        return P(name);
    }

    [Fact]
    public void Page_labels_are_written_read_and_spelled_out()
    {
        var input = Pages(12);
        Assert.Empty(PdfPageLabels.Read(input));
        Assert.Equal("3", PdfPageLabels.Labels(input)[2]);

        var spec = PdfPageLabels.Parse("1:i,5:1,10:A:付録-,12:-:表紙");
        Assert.Equal(new PageLabelRange(9, "A", "付録-"), spec[2]);
        PdfPageLabels.Write(input, P("labelled.pdf"), spec);
        Assert.Equal(spec, PdfPageLabels.Read(P("labelled.pdf")));
        Assert.Equal(["i", "ii", "iii", "iv", "1", "2", "3", "4", "5", "付録-A", "付録-B", "表紙"], PdfPageLabels.Labels(P("labelled.pdf")));
        Assert.Equal("1:i,5:1,10:A:付録-,12:-:表紙", PdfPageLabels.Format(spec));

        // a run that starts later: plain numbers before it; letters go on as AA, BB …; and labels can be removed
        PdfPageLabels.Write(input, P("later.pdf"), [new PageLabelRange(3, "a", "", 26)]);
        Assert.Equal(["1", "2", "3", "z", "aa", "bb"], PdfPageLabels.Labels(P("later.pdf")).Take(6));
        PdfPageLabels.Write(P("labelled.pdf"), P("plain.pdf"), []);
        Assert.Empty(PdfPageLabels.Read(P("plain.pdf")));
        Assert.Equal("XIV", PdfPageLabels.Number(14, "I"));

        Assert.Equal("Err_LabelPage", Assert.Throws<PageAssemblyException>(() => PdfPageLabels.Write(input, P("x.pdf"), [new PageLabelRange(12, "1")])).Key);
        Assert.Equal("Err_LabelSpec", Assert.Throws<PageAssemblyException>(() => PdfPageLabels.Parse("0:i")).Key);
        Assert.Equal("Err_LabelStyle", Assert.Throws<PageAssemblyException>(() => PdfPageLabels.Write(input, P("y.pdf"), [new PageLabelRange(0, "x")])).Key);
    }

    [Fact]
    public void Files_are_attached_listed_saved_out_and_removed_completely()
    {
        var input = Pages(1);
        File.WriteAllText(P("請求書.csv"), "品目,金額\nりんご,120\n", Encoding.UTF8);
        var data = new byte[5000];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(P("data.bin"), data);

        var added = PdfAttachments.Edit(input, P("attached.pdf"), [P("請求書.csv"), P("data.bin"), P("data.bin")], [], "明細");
        Assert.Equal(["請求書.csv", "data.bin", "data (2).bin"], added);
        var list = PdfAttachments.List(P("attached.pdf"));
        Assert.Equal(3, list.Count);
        var csv = list.Single(a => a.Name == "請求書.csv");
        Assert.Equal(("明細", -1, new FileInfo(P("請求書.csv")).Length), (csv.Description, csv.Page, csv.Size));
        Assert.NotNull(csv.Modified);

        // saved out: never over a file that is there
        var outDir = Directory.CreateDirectory(P("out")).FullName;
        File.WriteAllText(Path.Combine(outDir, "data.bin"), "taken");
        var saved = PdfAttachments.Extract(P("attached.pdf"), outDir, ["data.bin", "請求書.csv"]);
        Assert.Equal(["data (2).bin", "請求書.csv"], saved.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(outDir, "data (2).bin")));
        Assert.Equal("taken", File.ReadAllText(Path.Combine(outDir, "data.bin")));

        // removed: gone from the list and from the file
        PdfAttachments.Edit(P("attached.pdf"), P("removed.pdf"), [], ["data.bin", "data (2).bin"]);
        Assert.Equal(["請求書.csv"], PdfAttachments.List(P("removed.pdf")).Select(a => a.Name));
        using (var d = PdfReader.Open(P("removed.pdf"), PdfDocumentOpenMode.Import))
            Assert.Single(d.Internals.GetAllObjects(), o => o is PdfDictionary x && x.Elements.GetName("/Type") == "/EmbeddedFile");
        PdfAttachments.Edit(P("removed.pdf"), P("none.pdf"), [], ["請求書.csv"]);
        Assert.Empty(PdfAttachments.List(P("none.pdf")));

        Assert.Equal("Err_AttachmentNone", Assert.Throws<PageAssemblyException>(() => PdfAttachments.Edit(P("none.pdf"), P("z.pdf"), [], ["x"])).Key);
        Assert.Equal("Err_AttachmentsNone", Assert.Throws<PageAssemblyException>(() => PdfAttachments.Extract(P("none.pdf"), outDir)).Key);
        Assert.Equal(["evil.txt", "attachment", "a_b.txt"], new[] { "../../evil.txt", "..", "a:b.txt" }.Select(PdfAttachments.SafeName));
    }

    /// <summary>Two layers on one page: a red square on "Red", a blue one on "Blue", which starts hidden.</summary>
    internal static string Layered(string path)
    {
        using var d = new PdfDocument();
        var page = d.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(200);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(100);
        PdfDictionary Group(string name)
        {
            var g = new PdfDictionary(d);
            d.Internals.AddObject(g);
            g.Elements.SetName("/Type", "/OCG");
            g.Elements["/Name"] = new PdfString(name);
            return g;
        }
        var red = Group("Red");
        var blue = Group("Blue");
        var props = new PdfDictionary(d);
        props.Elements["/OCGs"] = new PdfArray(d, red.Reference!, blue.Reference!);
        var config = new PdfDictionary(d);
        config.Elements["/OFF"] = new PdfArray(d, blue.Reference!);
        config.Elements["/Order"] = new PdfArray(d, red.Reference!, blue.Reference!);
        props.Elements["/D"] = config;
        d.Internals.Catalog.Elements["/OCProperties"] = props;
        var resProps = new PdfDictionary(d);
        resProps.Elements["/R"] = red.Reference;
        resProps.Elements["/B"] = blue.Reference;
        var res = new PdfDictionary(d);
        res.Elements["/Properties"] = resProps;
        page.Elements["/Resources"] = res;
        var content = new PdfDictionary(d);
        d.Internals.AddObject(content);
        content.CreateStream(Encoding.ASCII.GetBytes("/OC /R BDC 1 0 0 rg 10 10 80 80 re f EMC /OC /B BDC 0 0 1 rg 110 10 80 80 re f EMC"));
        page.Elements["/Contents"] = content.Reference;
        d.Save(path);
        return path;
    }

    private static (int Red, int Blue) Colours(string path)
    {
        PdfiumDocument.Gate.Wait();
        try
        {
            using var d = PdfiumDocument.Open(path);
            var img = d.RenderPage(0, 1);
            int red = 0, blue = 0;
            for (int i = 0; i < img.Width * img.Height; i++)
            {
                byte r = img.Pixels[i * 3], g = img.Pixels[i * 3 + 1], b = img.Pixels[i * 3 + 2];
                if (r > 200 && g < 60 && b < 60) red++;
                if (b > 200 && r < 60 && g < 60) blue++;
            }
            return (red, blue);
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    [Fact]
    public void Layers_are_listed_and_shown_or_hidden_in_a_copy()
    {
        var input = Layered(P("layers.pdf"));
        var layers = PdfLayers.List(input);
        Assert.Equal([("Red", true), ("Blue", false)], layers.Select(l => (l.Name, l.Visible)));
        Assert.Equal([0], layers[1].Pages);
        var before = Colours(input);
        Assert.True(before is { Red: > 1000, Blue: 0 }, before.ToString());

        var after = PdfLayers.SetVisible(input, P("switched.pdf"), show: ["Blue"], hide: ["Red"]);
        Assert.Equal([("Red", false), ("Blue", true)], after.Select(l => (l.Name, l.Visible)));
        var drawn = Colours(P("switched.pdf"));
        Assert.True(drawn is { Red: 0, Blue: > 1000 }, drawn.ToString());   // the viewer draws what the file now says

        Assert.Equal("Err_LayerNone", Assert.Throws<PageAssemblyException>(() => PdfLayers.SetVisible(input, P("x.pdf"), ["Green"], [])).Key);
        Assert.Equal("Err_LayersNone", Assert.Throws<PageAssemblyException>(() => PdfLayers.SetVisible(Pages(1, "plain.pdf"), P("y.pdf"), ["Red"], [])).Key);
        Assert.Empty(PdfLayers.List(P("plain.pdf")));
    }

    private static string Text(string path)
    {
        PdfiumDocument.Gate.Wait();
        try
        {
            using var d = PdfiumDocument.Open(path);
            return string.Join("\f", Enumerable.Range(0, d.PageCount).Select(d.GetPageText));
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    [Fact]
    public void A_file_encrypted_for_certificates_opens_only_with_a_recipients_digital_id()
    {
        ToolsTests.MakeFlatForm(P("flat.pdf"));
        var taro = DigitalIds.Create("山田 太郎", "", P("taro.p12"), "secret-1");
        var hanako = DigitalIds.Create("Sato Hanako", "", P("hanako.p12"), "secret-1");
        var stranger = DigitalIds.Create("Stranger", "", P("stranger.p12"), "secret-1");
        // recipients are given by their certificate alone (no private key), as a colleague sends it
        var taroCert = X509CertificateLoader.LoadCertificate(taro.Certificate.RawData);
        var hanakoCert = X509CertificateLoader.LoadCertificate(hanako.Certificate.RawData);

        PdfCertificateSecurity.Encrypt(P("flat.pdf"), P("sealed.pdf"), [taroCert, hanakoCert]);
        Assert.True(PdfCertificateSecurity.IsEncryptedForCertificates(P("sealed.pdf")));
        Assert.False(PdfCertificateSecurity.IsEncryptedForCertificates(P("flat.pdf")));
        Assert.Equal(["山田 太郎", "Sato Hanako"], PdfCertificateSecurity.RecipientsOf(P("sealed.pdf")).Select(r => r.Issuer));
        var protection = PdfSecurity.Inspect(P("sealed.pdf"));
        Assert.True(protection is { Encrypted: true, NeedsPassword: true, Method: "Certificates" });
        // the text is not readable in the file
        Assert.DoesNotContain("BT", Encoding.Latin1.GetString(File.ReadAllBytes(P("sealed.pdf"))));

        PdfCertificateSecurity.Decrypt(P("sealed.pdf"), P("taro.pdf"), taro);
        PdfCertificateSecurity.Decrypt(P("sealed.pdf"), P("hanako.pdf"), hanako);
        var original = Text(P("flat.pdf"));
        Assert.Equal(original, Text(P("taro.pdf")));
        Assert.Equal(original, Text(P("hanako.pdf")));
        Assert.False(PdfSecurity.Inspect(P("taro.pdf")).Encrypted);

        Assert.Equal("Err_CertNotRecipient", Assert.Throws<PageAssemblyException>(() => PdfCertificateSecurity.Decrypt(P("sealed.pdf"), P("x.pdf"), stranger)).Key);
        Assert.Equal("Err_CertNotEncrypted", Assert.Throws<PageAssemblyException>(() => PdfCertificateSecurity.Decrypt(P("flat.pdf"), P("y.pdf"), taro)).Key);
        Assert.Equal("Err_CertAlready", Assert.Throws<PageAssemblyException>(() => PdfCertificateSecurity.Encrypt(P("sealed.pdf"), P("z.pdf"), [taroCert])).Key);

        // an ECDSA certificate cannot receive a key; a password-protected input is opened with its password
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ecCert = new CertificateRequest("CN=EC", ec, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        Assert.Equal("Err_CertNotRsa", Assert.Throws<PageAssemblyException>(() => PdfCertificateSecurity.Encrypt(P("flat.pdf"), P("ec.pdf"), [ecCert])).Key);
        PdfSecurity.Protect(P("flat.pdf"), P("locked.pdf"), "open-1", null, PdfPermissions.All);
        PdfCertificateSecurity.Encrypt(P("locked.pdf"), P("relocked.pdf"), [taroCert], "open-1");
        PdfCertificateSecurity.Decrypt(P("relocked.pdf"), P("relocked-open.pdf"), taro);
        Assert.Equal(original, Text(P("relocked-open.pdf")));
    }

    [Fact]
    public void Files_with_compressed_objects_and_many_strings_survive_certificate_encryption()
    {
        // a real-world shape: object streams, a cross-reference stream, strings with brackets and escapes
        ToolsTests.MakeFlatForm(P("flat.pdf"));
        AcroFormWriter.Write(P("flat.pdf"), P("form.pdf"), [new FormField(0, FormFieldKind.Text, new PdfRect(40, 300, 260, 324), "氏名 (カナ)")], FormScript.Japanese);
        AcroFormFields.Fill(P("form.pdf"), P("filled.pdf"), new Dictionary<string, string> { ["氏名 (カナ)"] = "ヤマダ \\ (タロウ)" });
        File.WriteAllBytes(P("packed.pdf"), ObjectStreams.Pack(File.ReadAllBytes(P("filled.pdf")))!);
        var me = DigitalIds.Create("Me", "", P("me.p12"), "secret-1");
        PdfCertificateSecurity.Encrypt(P("packed.pdf"), P("sealed.pdf"), [me.Certificate]);
        PdfCertificateSecurity.Decrypt(P("sealed.pdf"), P("open.pdf"), me);
        Assert.Equal("ヤマダ \\ (タロウ)", Assert.Single(AcroFormFields.List(P("open.pdf"))).Value);
        Assert.Equal(Text(P("filled.pdf")), Text(P("open.pdf")));
    }
}
