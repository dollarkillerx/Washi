// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;
using Washi.Tools;

namespace Washi.Masume.Tests;

/// <summary>Passwords and permissions; removing hidden information.</summary>
public sealed class SecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-security-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private string Form()
    {
        var flat = P("flat.pdf");
        if (!File.Exists(flat)) ToolsTests.MakeFlatForm(flat);
        var path = P("form.pdf");
        if (!File.Exists(path))
            AcroFormWriter.Write(flat, path, FormPipeline.Detect(flat).Fields.Select(f => f.ToFormField()).ToList(), FormScript.Japanese);
        return path;
    }

    // ------------------------------------------------------------------ passwords
    [Fact]
    public void An_open_password_is_asked_for_and_the_copy_keeps_its_fields()
    {
        var form = Form();
        PdfSecurity.Protect(form, P("p.pdf"), "open-1", null, PdfPermissions.All);

        var locked = PdfSecurity.Inspect(P("p.pdf"));
        Assert.True(locked.NeedsPassword);
        Assert.True(PdfSecurity.Inspect(P("p.pdf"), "wrong").NeedsPassword);
        var open = PdfSecurity.Inspect(P("p.pdf"), "open-1");
        Assert.Equal((true, false, true, "AES-256"), (open.Encrypted, open.NeedsPassword, open.FullAccess, open.Method));

        PdfiumDocument.Gate.Wait();
        try
        {
            Assert.Throws<IOException>(() => PdfiumDocument.Open(P("p.pdf")).Dispose());
            PdfPasswords.Remember(P("p.pdf"), "open-1");              // the viewer opens it once the user typed it
            using (var d = PdfiumDocument.Open(P("p.pdf"))) Assert.True(d.IsEncrypted);
            PdfPasswords.Forget(P("p.pdf"));
        }
        finally { PdfiumDocument.Gate.Release(); }

        PdfSecurity.Unlock(P("p.pdf"), P("u.pdf"), "open-1");
        Assert.False(PdfSecurity.Inspect(P("u.pdf")).Encrypted);
        Assert.Equal(AcroFormFields.List(form).Select(f => f.Name), AcroFormFields.List(P("u.pdf")).Select(f => f.Name));
    }

    [Fact]
    public void Restrictions_hold_and_only_the_permissions_password_lifts_them()
    {
        var perms = new PdfPermissions(Print: false, Copy: false, Edit: false);
        PdfSecurity.Protect(Form(), P("r.pdf"), "reader", "boss", perms);

        var asReader = PdfSecurity.Inspect(P("r.pdf"), "reader");
        Assert.Equal(perms, asReader.Permissions);
        Assert.False(asReader.FullAccess);
        Assert.True(PdfSecurity.Inspect(P("r.pdf"), "boss").FullAccess);

        // the open password is not enough to take the protection off or change it
        Assert.Equal("Err_NeedsPermissionsPassword",
            Assert.Throws<PageAssemblyException>(() => PdfSecurity.Unlock(P("r.pdf"), P("x.pdf"), "reader")).Key);
        Assert.Equal("Err_NeedsPermissionsPassword",
            Assert.Throws<PageAssemblyException>(() => PdfSecurity.Protect(P("r.pdf"), P("x.pdf"), "new", "new2", PdfPermissions.All, "reader")).Key);
        Assert.False(File.Exists(P("x.pdf")));

        PdfSecurity.Unlock(P("r.pdf"), P("u.pdf"), "boss");
        Assert.False(PdfSecurity.Inspect(P("u.pdf")).Encrypted);
    }

    [Fact]
    public void A_permissions_password_alone_opens_freely_but_keeps_its_limits()
    {
        PdfSecurity.Protect(Form(), P("o.pdf"), null, "boss", new PdfPermissions(Assemble: false));
        var p = PdfSecurity.Inspect(P("o.pdf"));
        Assert.Equal((true, false, false), (p.Encrypted, p.NeedsPassword, p.FullAccess));
        Assert.False(p.Permissions.Assemble);
        // Washi's other tools refuse it rather than ignoring its limits
        Assert.Throws<PageAssemblyException>(() => PageAssembly.Merge([P("o.pdf")], P("m.pdf")));
        Assert.Equal("Err_NeedsPermissionsPassword", Assert.Throws<PageAssemblyException>(() => PdfSecurity.Unlock(P("o.pdf"), P("u.pdf"), null)).Key);
        PdfSecurity.Unlock(P("o.pdf"), P("u.pdf"), "boss");
        Assert.False(PdfSecurity.Inspect(P("u.pdf")).Encrypted);
    }

    [Theory]
    [InlineData(null, null, false, "Err_ProtectNoPassword")]
    [InlineData("a", null, true, "Err_ProtectPermissionsPassword")]   // restrictions anyone with the open password could lift
    [InlineData("a", "a", true, "Err_ProtectPermissionsPassword")]
    public void Protection_that_would_not_protect_is_refused(string? open, string? perms, bool restrict, string key)
    {
        var e = Assert.Throws<PageAssemblyException>(() =>
            PdfSecurity.Protect(Form(), P("x.pdf"), open, perms, restrict ? new PdfPermissions(Print: false) : PdfPermissions.All));
        Assert.Equal(key, e.Key);
        Assert.False(File.Exists(P("x.pdf")));
    }

    [Fact]
    public void Unlocking_a_file_without_protection_is_refused() =>
        Assert.Equal("Err_NotProtected", Assert.Throws<PageAssemblyException>(() => PdfSecurity.Unlock(Form(), P("u.pdf"), null)).Key);

    // ------------------------------------------------------------------ hidden information
    /// <summary>A form with everything a cleaner should find: properties and XMP, an attachment, scripts, a comment,
    /// private data, and an incremental update.</summary>
    private string Loaded()
    {
        var path = P("loaded.pdf");
        using (var d = PdfReader.Open(Form(), PdfDocumentOpenMode.Modify))
        {
            d.Info.Title = "社外秘 見積";
            d.Info.Author = "林燁";
            d.Info.Subject = "draft 3";
            var cat = d.Internals.Catalog;

            // an attached file
            var data = new PdfDictionary(d);
            data.Elements["/Type"] = new PdfName("/EmbeddedFile");
            data.CreateStream("salaries"u8.ToArray());
            d.Internals.AddObject(data);
            var ef = new PdfDictionary(d);
            ef.Elements["/F"] = data.Reference;
            var spec = new PdfDictionary(d);
            spec.Elements["/Type"] = new PdfName("/Filespec");
            spec.Elements["/F"] = new PdfString("salaries.xlsx");
            spec.Elements["/UF"] = new PdfString("salaries.xlsx");
            spec.Elements["/EF"] = ef;
            d.Internals.AddObject(spec);
            var files = new PdfDictionary(d);
            files.Elements["/Names"] = new PdfArray(d, new PdfString("salaries.xlsx"), spec.Reference);
            var names = new PdfDictionary(d);
            names.Elements["/EmbeddedFiles"] = files;

            // a document script and an open action that runs JavaScript
            var js = new PdfDictionary(d);
            js.Elements["/S"] = new PdfName("/JavaScript");
            js.Elements["/JS"] = new PdfString("app.alert('hi')");
            d.Internals.AddObject(js);
            var jsTree = new PdfDictionary(d);
            jsTree.Elements["/Names"] = new PdfArray(d, new PdfString("init"), js.Reference);
            names.Elements["/JavaScript"] = jsTree;
            cat.Elements["/Names"] = names;
            cat.Elements["/OpenAction"] = js.Reference;

            // a sticky note, and private data on the page
            var page = d.Pages[0];
            var note = new PdfDictionary(d);
            note.Elements["/Type"] = new PdfName("/Annot");
            note.Elements["/Subtype"] = new PdfName("/Text");
            note.Elements["/Rect"] = new PdfArray(d, new PdfReal(10), new PdfReal(10), new PdfReal(30), new PdfReal(30));
            note.Elements["/Contents"] = new PdfString("ask 林 about the price");
            d.Internals.AddObject(note);
            if (page.Elements["/Annots"] is PdfReference { Value: PdfArray annots }) annots.Elements.Add(note.Reference);
            else if (page.Elements["/Annots"] is PdfArray direct) direct.Elements.Add(note.Reference);
            else page.Elements["/Annots"] = new PdfArray(d, note.Reference);
            var piece = new PdfDictionary(d);
            piece.Elements["/Illustrator"] = new PdfDictionary(d);
            page.Elements["/PieceInfo"] = piece;
            d.Save(path);
        }
        // an incremental update: the earlier version stays in the file
        File.AppendAllText(path, "\n" + IncrementalUpdate(File.ReadAllBytes(path)), Encoding.Latin1);
        return path;
    }

    /// <summary>A minimal incremental update (an unused object, a new xref section and %%EOF), as editors append.</summary>
    private static string IncrementalUpdate(byte[] file)
    {
        var text = Encoding.Latin1.GetString(file);
        int startxref = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0].Trim());
        int size = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"/Size ?(\d+)").Groups[1].Value);
        var root = System.Text.RegularExpressions.Regex.Match(text, @"/Root ?(\d+ \d+ R)").Groups[1].Value;
        var info = System.Text.RegularExpressions.Regex.Match(text, @"/Info ?(\d+ \d+ R)").Groups[1].Value;
        int at = file.Length + 1;
        var obj = $"{size} 0 obj\n(old value)\nendobj\n";
        int xref = at + obj.Length;
        return obj + $"xref\n{size} 1\n{at:D10} 00000 n \ntrailer\n<</Size {size + 1}/Root {root}/Info {info}/Prev {startxref}>>\nstartxref\n{xref}\n%%EOF\n";
    }

    [Fact]
    public void Everything_hidden_is_found()
    {
        var found = PdfCleaner.Inspect(Loaded()).ToDictionary(i => i.Kind);
        Assert.Contains("Title", found[HiddenInfo.Metadata].Details);
        Assert.Contains("Author", found[HiddenInfo.Metadata].Details);
        Assert.Contains("salaries.xlsx", found[HiddenInfo.Attachments].Details);
        Assert.Equal(2, found[HiddenInfo.Scripts].Count);       // the document script and the open action
        Assert.Equal(1, found[HiddenInfo.Comments].Count);
        Assert.Equal(1, found[HiddenInfo.PrivateData].Count);
        Assert.Equal(1, found[HiddenInfo.EarlierVersions].Count);
    }

    [Fact]
    public void A_cleaned_copy_has_none_of_it_and_keeps_its_fields()
    {
        var input = Loaded();
        var all = Enum.GetValues<HiddenInfo>().ToHashSet();
        var removed = PdfCleaner.Clean(input, P("clean.pdf"), all);

        Assert.Equal(all, removed.Select(r => r.Kind).ToHashSet());
        Assert.Empty(PdfCleaner.Inspect(P("clean.pdf")));
        Assert.Equal(AcroFormFields.List(input).Select(f => f.Name), AcroFormFields.List(P("clean.pdf")).Select(f => f.Name));

        var bytes = Encoding.Latin1.GetString(File.ReadAllBytes(P("clean.pdf")));
        foreach (var secret in new[] { "salaries", "app.alert", "/PieceInfo", "old value", "draft 3" }) Assert.DoesNotContain(secret, bytes);
        Assert.DoesNotMatch(@"0001-01-01T00:00:00\+0[1-9]", bytes);   // no time zone of this computer
    }

    [Fact]
    public void Only_the_chosen_kinds_go()
    {
        var input = Loaded();
        PdfCleaner.Clean(input, P("meta.pdf"), new HashSet<HiddenInfo> { HiddenInfo.Metadata });
        var left = PdfCleaner.Inspect(P("meta.pdf")).Select(i => i.Kind).ToList();
        Assert.Equal([HiddenInfo.Attachments, HiddenInfo.Scripts, HiddenInfo.Comments, HiddenInfo.PrivateData], left);
    }

    [Fact]
    public void Protected_files_are_not_cleaned()
    {
        PdfSecurity.Protect(Form(), P("p.pdf"), "pw", null, PdfPermissions.All);
        Assert.Equal("Err_CleanProtected",
            Assert.Throws<PageAssemblyException>(() => PdfCleaner.Clean(P("p.pdf"), P("c.pdf"), new HashSet<HiddenInfo> { HiddenInfo.Metadata })).Key);
    }
}
