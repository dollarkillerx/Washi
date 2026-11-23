// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Washi.App;
using Washi.App.ViewModels;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Pdfium;
using Washi.Masume;

namespace Washi.App.Tests;

/// <summary>A model that answers from a function and counts calls.</summary>
internal sealed class ScriptedModel(Func<Washi.Ai.AiRequest, string> answer, bool offDevice = true) : Washi.Ai.IAiModel
{
    public int Calls { get; private set; }
    public string Describe => "scripted (test)";
    public bool SendsDataOffDevice => offDevice;
    public bool AcceptsImages => true;
    public Task<string> CompleteAsync(Washi.Ai.AiRequest request, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(answer(request));
    }
}

public sealed class ToolsUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-app-tools-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Form(string name, int pages = 1)
    {
        var path = Path.Combine(_dir, name);
        var doc = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(842);
            page.Height = XUnit.FromPoint(595);
            using var g = XGraphics.FromPdfPage(page);
            var pen = new XPen(XColors.Black, 1);
            g.DrawRectangle(pen, 40, 40, 400, 30);
            g.DrawRectangle(pen, 40, 140, 8, 8);
        }
        doc.Save(path);
        return path;
    }

    /// <summary>A page that is only an image (a "scan").</summary>
    private string Scan()
    {
        var w = 400; var h = 200;
        var px = new byte[w * h * 3];
        Array.Fill(px, (byte)255);
        for (int y = 90; y < 110; y++) for (int x = 40; x < 240; x++) px[(y * w + x) * 3] = px[(y * w + x) * 3 + 1] = px[(y * w + x) * 3 + 2] = 0;
        var png = Path.Combine(_dir, "scan.png");
        using (var fs = File.Create(png)) PngWriter.Write(new RgbImage(w, h, px), fs);
        var path = Path.Combine(_dir, "scan.pdf");
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(w); page.Height = XUnit.FromPoint(h);
        using (var g = XGraphics.FromPdfPage(page)) g.DrawImage(XImage.FromFile(png), 0, 0, w, h);
        doc.Save(path);
        return path;
    }

    private static async Task<(MainWindow W, MainViewModel Vm)> Open(string pdf, FakeDialogs dialogs)
    {
        var window = new MainWindow(dialogs) { Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.ViewModel.LoadAsync(pdf);
        Dispatcher.UIThread.RunJobs();
        return (window, window.ViewModel);
    }

    [AvaloniaFact]
    public async Task Fields_belong_to_the_form_tool_and_are_detected_when_it_opens()
    {
        var pdf = Form("a.pdf");
        var (_, vm) = await Open(pdf, new FakeDialogs());
        Assert.True(vm.ShowEditTools);
        Assert.NotEmpty(vm.Fields);

        vm.DrawMode = true;
        vm.ActiveTool = Tool.Split;
        Assert.False(vm.ShowEditTools);
        Assert.False(vm.ShowFieldsPanel);
        Assert.False(vm.DrawMode);

        // opening another file in another tool does not detect; coming back to the form tool does
        await vm.LoadAsync(Form("b.pdf"));
        Assert.Empty(vm.Fields);
        vm.ActiveTool = Tool.Form;
        for (int i = 0; i < 50 && vm.Fields.Count == 0; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.NotEmpty(vm.Fields);
    }

    [AvaloniaFact]
    public async Task Tab_walks_through_the_fields_and_fit_width_follows_the_window()
    {
        var (_, vm) = await Open(Form("a.pdf"), new FakeDialogs());
        vm.SelectNextField(backwards: false);
        var first = vm.SelectedField;
        Assert.Same(vm.PageFields[0], first);
        vm.SelectNextField(backwards: true);
        Assert.Same(vm.PageFields[^1], vm.SelectedField);

        vm.ViewportWidth = 1000;
        double fitted = vm.Zoom;
        Assert.InRange(842 * fitted, 1000 - 2 * 56 - 10, 1000 - 2 * 56);
        vm.ZoomInCommand.Execute(null);
        double zoomed = vm.Zoom;
        vm.ViewportWidth = 700;                    // a manual zoom is kept
        Assert.Equal(zoomed, vm.Zoom);
        vm.FitWidthCommand.Execute(null);
        Assert.True(vm.Zoom < fitted);
    }

    private static FieldViewModel Field(FieldKind kind, double x, string name, string group = "", string export = "", string options = "") => new()
    {
        Page = 0, Kind = kind, X0 = x, Y0 = 200, X1 = x + (kind == FieldKind.Radio ? 10 : 120), Y1 = 210 + (kind == FieldKind.Radio ? 0 : 10),
        Name = name, Group = group, Export = export, Options = options,
    };

    [AvaloniaFact]
    public async Task Radio_buttons_choose_one_per_group_and_dropdowns_cycle_then_both_are_saved()
    {
        var a = Form("a.pdf");
        var output = Path.Combine(_dir, "a_fillable.pdf");
        var (_, vm) = await Open(a, new FakeDialogs { SavePdf = output });
        vm.Fields.Clear();
        var yes = Field(FieldKind.Radio, 40, "r1", "有無", "有");
        var no = Field(FieldKind.Radio, 80, "r2", "有無", "無");
        var pick = Field(FieldKind.Choice, 200, "続柄", options: "夫\n妻\n子");
        foreach (var f in new[] { yes, no, pick }) vm.Fields.Add(f);

        vm.PreviewMode = true;
        vm.ActivateField(yes);
        vm.ActivateField(no);                       // the group's other button is cleared
        Assert.False(yes.IsChecked);
        Assert.True(no.IsChecked);
        Assert.Equal("● 無", no.Display);
        vm.ActivateField(pick);
        vm.ActivateField(pick);
        Assert.Equal("妻", pick.Value);              // each click moves to the next item

        await vm.MakeFillableCommand.ExecuteAsync(null);
        var saved = AcroFormFields.List(output);
        Assert.Equal("無", Assert.Single(saved, f => f.Type == "radio").Value);
        Assert.Equal("妻", Assert.Single(saved, f => f.Type == "choice").Value);
    }

    [AvaloniaFact]
    public async Task Sign_tool_remembers_the_signature_places_it_and_saves_a_signed_copy()
    {
        var a = Form("a.pdf");
        var output = Path.Combine(_dir, "a_signed.pdf");
        var (w, vm) = await Open(a, new FakeDialogs { SavePdf = output });
        var store = Path.Combine(_dir, "signature.txt");
        vm.Sign.StorePath = store;
        vm.Fields.Add(Field(FieldKind.Signature, 300, "署名"));

        vm.ActiveTool = Tool.Sign;
        await vm.Sign.PrepareAsync();
        var spot = Assert.Single(vm.Sign.Spots);
        vm.Sign.SignSpotCommand.Execute(spot);
        Assert.Empty(vm.Sign.Placed);                // nothing to place before a signature is drawn
        Assert.False(vm.Sign.CanSave);

        // draw on the pad: a stroke from left to right
        var pad = w.FindControl<Washi.App.Controls.InkPad>("SignPad")!;
        Dispatcher.UIThread.RunJobs();
        w.MouseDown(Avalonia.VisualExtensions.TranslatePoint(pad, new Avalonia.Point(20, 60), w)!.Value, Avalonia.Input.MouseButton.Left);
        foreach (var x in new[] { 60, 100, 140, 180 })
            w.MouseMove(Avalonia.VisualExtensions.TranslatePoint(pad, new Avalonia.Point(x, x % 80 == 0 ? 40 : 80), w)!.Value);
        w.MouseUp(Avalonia.VisualExtensions.TranslatePoint(pad, new Avalonia.Point(200, 60), w)!.Value, Avalonia.Input.MouseButton.Left);
        Assert.True(vm.Sign.HasInk);
        Assert.True(File.Exists(store));             // remembered on this computer

        vm.Sign.SignSpotCommand.Execute(spot);
        vm.Sign.Place(new PdfRect(40, 300, 240, 340));
        Assert.Equal(2, vm.Sign.Placed.Count);
        Assert.True(vm.Sign.CanSave);

        await vm.Sign.SaveCommand.ExecuteAsync(null);
        Assert.Equal(output, vm.PdfPath);
        Assert.Empty(vm.Sign.Placed);
        Assert.Contains("2", vm.Status);

        vm.Sign.Remember = false;                    // forgetting deletes the file
        Assert.False(File.Exists(store));
    }

    /// <summary>A 600 × 400 page with one line of real (Helvetica) text at the top: "Please check this sentence".</summary>
    private string TextPdf(string name)
    {
        static byte[] A(string t) => System.Text.Encoding.ASCII.GetBytes(t);
        const string content = "BT /F1 14 Tf 40 340 Td (Please check this sentence) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        using var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(ms.Position); ms.Write(A($"{i + 1} 0 obj\n{objects[i]}\nendobj\n")); }
        long xref = ms.Position;
        var sb = new System.Text.StringBuilder($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(A(sb.ToString()));
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [AvaloniaFact]
    public async Task Comment_tool_marks_text_adds_notes_boxes_and_ink_then_saves_them_as_annotations()
    {
        var doc = TextPdf("text.pdf");
        var output = Path.Combine(_dir, "text_commented.pdf");
        var (_, vm) = await Open(doc, new FakeDialogs { SavePdf = output });
        var c = vm.Comment;
        vm.ActiveTool = Tool.Comment;
        await c.PrepareAsync();
        Assert.Empty(c.Existing);

        // highlight: drag over the text line (upright y ≈ 46-60); an empty area gives a message, not a comment
        await c.PlaceAsync(new PdfRect(30, 300, 300, 350));
        Assert.Empty(c.Pending);
        await c.PlaceAsync(new PdfRect(30, 35, 400, 70));
        var hl = Assert.Single(c.Pending).Spec;
        Assert.Equal(Washi.Core.Pages.CommentKind.Highlight, hl.Kind);
        Assert.Single(hl.Lines!);

        c.Mode = Washi.Core.Pages.CommentKind.Note;
        Assert.True(c.NeedsText);
        await c.PlaceAsync(new PdfRect(450, 40, 451, 41));   // no text typed yet: nothing added
        Assert.Single(c.Pending);
        c.Draft = "要確認";
        await c.PlaceAsync(new PdfRect(450, 40, 451, 41));
        Assert.Equal("", c.Draft);                          // ready for the next one

        c.Mode = Washi.Core.Pages.CommentKind.TextBox;
        c.Draft = "OK";
        await c.PlaceAsync(new PdfRect(40, 100, 200, 140));
        c.Mode = Washi.Core.Pages.CommentKind.Pen;
        c.AddStroke([(40.0, 200.0), (80.0, 240.0), (120.0, 200.0)]);
        c.Mode = Washi.Core.Pages.CommentKind.Box;
        await c.PlaceAsync(new PdfRect(300, 200, 500, 300));
        Assert.Equal(5, c.Pending.Count);
        c.RemovePendingCommand.Execute(c.Pending[^1]);
        Assert.Equal(4, c.Pending.Count);

        await c.SaveCommand.ExecuteAsync(null);
        Assert.Equal(output, vm.PdfPath);
        Assert.Empty(c.Pending);
        Assert.Equal(["Highlight", "Text", "FreeText", "Ink"], c.Existing.Select(e => e.Info.Type));
        Assert.Equal("要確認", c.Existing[1].Info.Text);
    }

    /// <summary>A UI context that has stopped: posted continuations never run (a closed app, an ended headless test).</summary>
    private sealed class StoppedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) { }
    }

    [AvaloniaFact]
    public async Task Protect_adds_a_password_asks_for_it_on_opening_then_takes_it_off_and_cleans()
    {
        var a = Form("a.pdf");
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "a_protected.pdf") };
        var (w, vm) = await Open(a, dialogs);
        vm.ActiveTool = Tool.Protect;
        await vm.Protect.PrepareAsync();
        Assert.Equal(L.Get("Ui_ProtectNone"), vm.Protect.StateText);
        Assert.False(vm.Protect.CanProtect);
        vm.Protect.Restrict = true;                       // limits without their own password do not protect anything
        vm.Protect.OpenPassword = vm.Protect.PermissionsPassword = "same";
        Assert.False(vm.Protect.CanProtect);
        vm.Protect.Restrict = false;
        vm.Protect.OpenPassword = "pw-1";
        Assert.True(vm.Protect.CanProtect);
        await vm.Protect.ProtectCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);         // the protected copy is open (its password is remembered)
        Assert.Equal(L.F("Ui_ProtectStateOpen", "AES-256"), vm.Protect.StateText);
        Assert.Equal("", vm.Protect.OpenPassword);          // not left in the box
        Assert.True(vm.Protect.CanRemoveProtection);
        Assert.False(vm.Protect.CanClean);

        // opened again later: it asks for the password, and only the right one opens it
        var locked = dialogs.SavePdf!;
        vm.CloseDocumentCommand.Execute(null);
        Washi.Core.Pages.PdfPasswords.Forget(locked);
        vm.ActiveTool = Tool.Form;
        await vm.LoadAsync(locked);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Tool.Protect, vm.ActiveTool);
        Assert.True(vm.Protect.IsLocked);
        Assert.False(vm.HasDocument);
        Assert.True(w.FindControl<TextBox>("UnlockPasswordBox")!.IsEffectivelyVisible);
        vm.Protect.UnlockPassword = "nope";
        await vm.Protect.OpenLockedCommand.ExecuteAsync(null);
        Assert.True(vm.Protect.IsLocked);
        Assert.Equal(L.Get("Ui_ProtectWrongPassword"), vm.Status);
        vm.Protect.UnlockPassword = "pw-1";
        await vm.Protect.OpenLockedCommand.ExecuteAsync(null);
        Assert.False(vm.Protect.IsLocked);
        Assert.Equal(locked, vm.PdfPath);
        Assert.Equal(1, vm.PageCount);

        // the open password allows everything (no separate permissions password): it takes the protection off
        dialogs.SavePdf = Path.Combine(_dir, "a_unlocked.pdf");
        Assert.Equal("pw-1", vm.Protect.CurrentPassword);
        await vm.Protect.RemoveProtectionCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(L.Get("Ui_ProtectNone"), vm.Protect.StateText);

        // hidden information: PDFsharp's metadata (dates) is found and cleaned
        var meta = Assert.Single(vm.Protect.Hidden, h => h.Item.Kind == Washi.Core.Pages.HiddenInfo.Metadata);
        Assert.True(meta.Remove);
        dialogs.SavePdf = Path.Combine(_dir, "a_cleaned.pdf");
        await vm.Protect.CleanCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.True(vm.Protect.NothingHidden);
    }

    /// <summary>A one-page PDF (Helvetica) with one line of text per entry, written by hand.</summary>
    private string TextPdf(string name, params string[] lines)
    {
        var content = string.Join("\n", lines.Select((l, i) => $"BT /F1 14 Tf 40 {540 - i * 30} Td ({l}) Tj ET"));
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ];
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    [AvaloniaFact]
    public async Task Redact_finds_words_lets_some_stay_and_saves_a_copy_without_the_rest()
    {
        var pdf = TextPdf("t.pdf", "Name: Taro Yamada", "Mail: taro@example.com", "Keep this line");
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "t_redacted.pdf") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ActiveTool = Tool.Redact;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<TextBox>("RedactWordsBox")!.IsEffectivelyVisible);
        Assert.False(vm.Redact.CanFind);
        Assert.False(vm.Redact.CanRedact);

        vm.Redact.Words = "yamada\n";
        vm.Redact.Emails = true;
        await vm.Redact.FindCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Redact.Matches.Count);
        Assert.Equal(L.F("Ui_RedactFound", 2, 1), vm.Redact.FoundText);
        Assert.Contains("Taro Yamada", vm.Redact.Matches[0].Context);
        vm.Redact.Matches[1].Redact = false;                   // the e-mail address stays
        vm.Redact.AddArea(new Washi.Core.PdfRect(40, 300, 200, 330));   // an empty spot: only covered
        Assert.Equal(2, vm.Redact.Chosen);
        Assert.Equal(L.F("Ui_RedactRun", 2), vm.Redact.RunText);

        await vm.Redact.RedactCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.False(vm.Redact.HasMatches);                     // the copy starts afresh
        Assert.StartsWith(L.F("Ui_Redacted", "t_redacted.pdf", 6, 0, 0, 0)[..20], vm.Status);
        var text = await MainViewModel.Pdfium(() =>
        {
            using var d = PdfiumDocument.Open(dialogs.SavePdf!);
            return d.GetPageText(0);
        });
        Assert.DoesNotContain("Yamada", text);
        Assert.Contains("Name: Taro", text);
        Assert.Contains("taro@example.com", text);
        Assert.Contains("Keep this line", text);
        Assert.True(File.Exists(pdf));                          // the original is untouched

        dialogs.SavePdf = vm.PdfPath;                           // never over the open file itself
        vm.Redact.AddArea(new Washi.Core.PdfRect(40, 40, 100, 60));
        await vm.Redact.RedactCommand.ExecuteAsync(null);
        Assert.Equal(L.Get("Ui_RedactNotOriginal"), vm.Status);
    }

    [AvaloniaFact]
    public async Task Images_are_replaced_removed_and_added_in_a_copy()
    {
        string Photo(string name, SkiaSharp.SKColor color, int w, int h)
        {
            using var bmp = new SkiaSharp.SKBitmap(w, h);
            bmp.Erase(color);
            using var data = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }
        // a PDF of two photos (one page each), then a page of text in between
        var pdf = Path.Combine(_dir, "album.pdf");
        await MainViewModel.Pdfium(() => Washi.Core.Pages.PdfFromImages.Create(
            [Photo("a.png", SkiaSharp.SKColors.Teal, 300, 200), Photo("b.png", SkiaSharp.SKColors.Gold, 300, 200)], pdf));
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "album_images.pdf"), Image = Photo("new.png", SkiaSharp.SKColors.Crimson, 200, 200) };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ActiveTool = Tool.Images;
        await vm.Images.PrepareAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Images.Images.Count);
        Assert.False(vm.Images.CanSave);

        // click the first photo, replace it; remove the second
        var first = vm.Images.Images[0].Image.Rect;
        vm.Images.SelectAt(first.Center.X, first.Center.Y);
        Assert.Same(vm.Images.Images[0], vm.Images.Selected);
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Button>("ImageReplaceButton")!.IsEffectivelyVisible);
        await vm.Images.ReplaceCommand.ExecuteAsync(null);
        Assert.Equal(L.F("Ui_ImageWillBe", "new.png"), vm.Images.Images[0].State);
        vm.Images.Images[1].Remove = true;

        // and add a picture in a box on page 1
        await vm.Images.ChooseToAddCommand.ExecuteAsync(null);
        Assert.True(vm.Images.Placing);
        vm.Images.Draw(new Washi.Core.PdfRect(40, 40, 140, 140));
        Assert.False(vm.Images.Placing);
        Assert.Equal(L.F("Ui_ImagesSave", 3), vm.Images.SaveText);

        await vm.Images.SaveCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(L.F("Ui_ImagesSaved", "album_images.pdf", 1, 1, 1), vm.Status);
        Assert.Equal(2, vm.Images.Images.Count);                    // the new one and the replaced one; the second photo is gone
        Assert.All(vm.Images.Images, i => Assert.Equal(0, i.Image.Page));
    }

    [AvaloniaFact]
    public async Task Links_are_drawn_found_in_the_text_and_saved_in_a_copy()
    {
        var pdf = TextPdf("l.pdf", "Order at https://example.com/shop", "Questions: help@example.com", "Terms on page 1");
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "l_links.pdf") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ActiveTool = Tool.Links;
        await vm.Links.PrepareAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Button>("LinksFindButton")!.IsEffectivelyVisible);
        Assert.False(vm.Links.HasExisting);
        Assert.False(vm.Links.CanSave);

        // a box drawn on the third line, to page 1
        vm.Links.Draw(new Washi.Core.PdfRect(40, 100, 200, 120));
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<TextBox>("LinkTargetBox")!.IsEffectivelyVisible);
        vm.Links.Target = "not a link";
        vm.Links.KeepCommand.Execute(null);
        Assert.Equal(L.F("Err_LinkTarget", "not a link"), vm.Links.Message);
        vm.Links.Target = "1";
        vm.Links.KeepCommand.Execute(null);
        Assert.Single(vm.Links.Pending);
        Assert.False(vm.Links.HasDraft);

        await vm.Links.FindAddressesCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Links.Pending.Count);
        Assert.Equal(L.F("Ui_LinksFound", 2), vm.Links.Message);
        Assert.Equal(L.F("Ui_LinksSave", 3), vm.Links.SaveText);

        await vm.Links.SaveCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(L.F("Ui_LinksSaved", "l_links.pdf", 3, 0), vm.Status);
        Assert.Equal(3, vm.Links.Existing.Count);                  // the new file's links
        Assert.Contains(vm.Links.Existing, r => r.Target == "help@example.com");

        // remove one
        dialogs.SavePdf = Path.Combine(_dir, "l_fewer.pdf");
        vm.Links.Existing.First(r => r.Link.Kind == "page").Remove = true;
        Assert.True(vm.Links.CanSave);
        await vm.Links.SaveCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Links.Existing.Count);
    }

    [AvaloniaFact]
    public async Task Organize_crops_pages_and_saves_them_as_images()
    {
        var pdf = TextPdf("o.pdf", "Quarterly report", "Figures inside");
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "o_cropped.pdf"), Folder = Path.Combine(_dir, "pngs") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ActiveTool = Tool.Organize;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Expander>("CropExpander")!.IsEffectivelyVisible);
        Assert.Null(vm.Organize.SelectedRange);
        Assert.Equal(L.Get("Ui_PagesScopeAll"), vm.Organize.ScopeText);

        // the page is 842 × 595 pt; 10 mm off each side
        await vm.Organize.CropCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(L.F("Ui_Cropped", "o_cropped.pdf", 1), vm.Status);
        Assert.Equal(842 - 2 * 10 * 72 / 25.4, vm.PageWidth, 0);

        vm.Organize.ExportFormatIndex = 1;
        vm.Organize.ExportDpiIndex = 0;
        await vm.Organize.ExportImagesCommand.ExecuteAsync(null);
        var file = Assert.Single(Directory.GetFiles(dialogs.Folder!));
        Assert.Equal("o_cropped_p1.jpg", Path.GetFileName(file));
        Assert.Equal(L.F("Ui_Exported", 1, dialogs.Folder!), vm.Status);
        using var img = SkiaSharp.SKBitmap.Decode(file);
        Assert.InRange(img.Width, 783, 787);
    }

    [AvaloniaFact]
    public async Task Compare_lists_the_differences_and_saves_a_marked_copy()
    {
        var older = TextPdf("v1.pdf", "Contract 2025", "Rent: 80,000 yen", "Notice: one month");
        var newer = TextPdf("v2.pdf", "Contract 2025", "Rent: 85,000 yen", "Notice: one month", "Pets allowed");
        var dialogs = new FakeDialogs { Pdf = older, SavePdf = Path.Combine(_dir, "v2_compared.pdf") };
        var (w, vm) = await Open(newer, dialogs);
        vm.ActiveTool = Tool.Compare;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Button>("CompareChooseButton")!.IsEffectivelyVisible);
        Assert.False(vm.Compare.CanCompare);

        await vm.Compare.ChooseCommand.ExecuteAsync(null);
        Assert.Equal(L.F("Ui_CompareOther", "v1.pdf"), vm.Compare.OtherText);
        await vm.Compare.CompareCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Compare.Rows.Count);
        Assert.Equal(L.F("Ui_CmpRowChanged", "80,000", "85,000"), vm.Compare.Rows[0].Text);
        Assert.Equal(L.F("Ui_CmpRowAdded", "Pets allowed"), vm.Compare.Rows[1].Text);
        Assert.Equal(2, vm.Compare.On(0).Count());
        Assert.Equal(L.F("Ui_CompareSummary", 2, 1, 1, 0, 0, 0, 0), vm.Compare.Summary);

        // from the other side: the open file is the newer one no more
        vm.Compare.OtherIsNewer = true;
        Assert.False(vm.Compare.HasRows);
        await vm.Compare.CompareCommand.ExecuteAsync(null);
        Assert.Equal(L.F("Ui_CmpRowDeleted", "Pets allowed"), vm.Compare.Rows[1].Text);
        vm.Compare.OtherIsNewer = false;
        await vm.Compare.CompareCommand.ExecuteAsync(null);

        Assert.True(vm.Compare.CanSave);
        await vm.Compare.SaveCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(L.F("Ui_CompareMarked", "v2_compared.pdf", 2), vm.Status);
        var comments = await MainViewModel.Pdfium(() => Washi.Core.Pages.PdfComments.List(dialogs.SavePdf!));
        Assert.Equal(2, comments.Count);

        // the same file twice is refused
        dialogs.Pdf = vm.PdfPath;
        await vm.Compare.ChooseCommand.ExecuteAsync(null);
        Assert.Equal(L.Get("Ui_CompareSameFile"), vm.Status);
    }

    [AvaloniaFact]
    public async Task Dropped_photos_go_to_Combine_and_become_a_PDF()
    {
        string Photo(string name, SkiaSharp.SKColor color)
        {
            using var bmp = new SkiaSharp.SKBitmap(240, 320);
            bmp.Erase(color);
            using var data = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "receipts.pdf") };
        var (w, vm) = await Open(TextPdf("cover.pdf", "Expense report"), dialogs);
        Assert.Equal(Tool.Form, vm.ActiveTool);

        await vm.DropAsync([Photo("r1.jpg", SkiaSharp.SKColors.Orange), Photo("r2.jpg", SkiaSharp.SKColors.Teal)]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Tool.Combine, vm.ActiveTool);                 // photos open Combine
        Assert.Equal(["r1.jpg", "r2.jpg"], vm.Combine.Files.Select(f => f.Name));
        Assert.All(vm.Combine.Files, f => Assert.True(f.IsImage));
        Assert.True(vm.Combine.HasImages);
        Assert.True(w.FindControl<ComboBox>("CombinePageSize")!.IsEffectivelyVisible);
        Assert.True(vm.Combine.CanCombine);

        await vm.Combine.RunCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(2, vm.PageCount);
        Assert.Equal(L.F("Ui_ImagesCombined", 2, "receipts.pdf"), vm.Status);

        // PDFs and a photo together: the new file is offered first, as for any open document
        dialogs.SavePdf = Path.Combine(_dir, "mixed.pdf");
        for (int i = 0; i < 100 && vm.Combine.Files.Count == 0; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        await vm.Combine.AddAsync([Path.Combine(_dir, "cover.pdf"), Path.Combine(_dir, "r1.jpg")]);
        Assert.Equal(["receipts.pdf", "cover.pdf", "r1.jpg"], vm.Combine.Files.Select(f => f.Name));
        vm.Combine.PageSizeIndex = 2;                              // the image's own size
        await vm.Combine.RunCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(4, vm.PageCount);
    }

    [AvaloniaFact]
    public async Task Headers_tool_adds_page_and_Bates_numbers_and_a_watermark_to_a_copy()
    {
        var pdf = TextPdf("s.pdf", "Contract", "Signed by both parties");
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "s_stamped.pdf") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ActiveTool = Tool.Stamp;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<TextBox>("StampHeaderLeft")!.IsEffectivelyVisible);
        Assert.False(vm.Stamp.CanSave);

        vm.Stamp.PageNumbersCommand.Execute(null);
        Assert.Equal("{page} / {pages}", vm.Stamp.FooterCenter);
        Assert.True(vm.Stamp.CanSave);
        Assert.False(vm.Stamp.UsesBates);
        vm.Stamp.BatesCommand.Execute(null);
        Assert.True(vm.Stamp.UsesBates);
        vm.Stamp.BatesPrefix = "W-";
        vm.Stamp.BatesStart = 7;
        vm.Stamp.BatesDigits = 4;
        Assert.Equal("W-0007", vm.Stamp.BatesFor(0));
        vm.Stamp.HeaderLeft = "株式会社和紙";
        vm.Stamp.WatermarkText = "DRAFT";
        Assert.True(vm.Stamp.Marks(0));
        vm.Stamp.Pages = "2";                                     // there is no page 2: nothing is marked
        Assert.False(vm.Stamp.Marks(0));
        vm.Stamp.Pages = "";

        await vm.Stamp.SaveCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Equal(L.F("Ui_StampedBates", "s_stamped.pdf", 1, "W-0007", "W-0007"), vm.Status);
        var text = await MainViewModel.Pdfium(() =>
        {
            using var d = PdfiumDocument.Open(dialogs.SavePdf!);
            return d.GetPageText(0);
        });
        Assert.Contains("1 / 1", text);
        Assert.Contains("W-0007", text);
        Assert.Contains("株式会社和紙", text);
        Assert.Contains("DRAFT", text);
        Assert.Contains("Signed by both parties", text);
        Assert.True(File.Exists(pdf));
    }

    [AvaloniaFact]
    public async Task Edit_text_changes_a_clicked_line_and_replaced_words_in_a_copy()
    {
        var pdf = TextPdf("e.pdf", "Invoice 2025", "Total: 1,200 yen", "Thank you");
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "e_edited.pdf") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ActiveTool = Tool.EditText;
        await vm.EditText.PrepareAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Invoice 2025", "Total: 1,200 yen", "Thank you"], vm.EditText.Segments.Select(x => x.Text));
        Assert.False(vm.EditText.CanSave);
        Assert.False(w.FindControl<TextBox>("EditTextBox")!.IsEffectivelyVisible);

        // a click on the first line opens it for typing
        var first = vm.EditText.Segments[0];
        vm.EditText.Select(vm.EditText.SegmentAt(first.Box.Center.X, first.Box.Center.Y));
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<TextBox>("EditTextBox")!.IsEffectivelyVisible);
        Assert.Equal("Invoice 2025", vm.EditText.Text);
        vm.EditText.Text = "Invoice 2026";
        vm.EditText.CommitCommand.Execute(null);
        Assert.Single(vm.EditText.Pending);
        Assert.Null(vm.EditText.Selected);

        // Esc leaves a line as it was; typing the old text back drops the change
        vm.EditText.Select(vm.EditText.Segments[2]);
        vm.EditText.Text = "Bye";
        vm.EditText.CancelCommand.Execute(null);
        Assert.Single(vm.EditText.Pending);
        vm.EditText.Select(first);
        Assert.Equal("Invoice 2026", vm.EditText.Text);           // the change so far
        vm.EditText.Text = "Invoice 2025";
        vm.EditText.CommitCommand.Execute(null);
        Assert.Empty(vm.EditText.Pending);
        vm.EditText.Select(first);
        vm.EditText.Text = "Invoice 2026";
        vm.EditText.CommitCommand.Execute(null);

        // find and replace
        vm.EditText.Find = "1,200";
        vm.EditText.Replace = "1,350";
        await vm.EditText.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.EditText.Pending.Count);
        Assert.Equal(L.F("Ui_EditTextReplaced", 1), vm.EditText.Message);
        vm.EditText.Find = "Osaka";
        await vm.EditText.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal(L.F("Ui_EditTextNotFound", "Osaka"), vm.EditText.Message);
        Assert.Equal(L.F("Ui_EditTextSave", 2), vm.EditText.SaveText);

        await vm.EditText.SaveCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.False(vm.EditText.HasPending);                    // the copy starts afresh
        Assert.Equal(L.F("Ui_TextEdited", "e_edited.pdf", 2), vm.Status);
        var text = await MainViewModel.Pdfium(() =>
        {
            using var d = PdfiumDocument.Open(dialogs.SavePdf!);
            return d.GetPageText(0);
        });
        Assert.Contains("Invoice 2026", text);
        Assert.Contains("Total: 1,350 yen", text);
        Assert.Contains("Thank you", text);
        Assert.DoesNotContain("2025", text);
        Assert.True(File.Exists(pdf));                           // the original is untouched

        // a longer text that would run into the rest of its line: refused, nothing written
        dialogs.SavePdf = Path.Combine(_dir, "e_long.pdf");
        vm.EditText.Find = "1,350";
        vm.EditText.Replace = "one thousand three hundred and fifty";
        await vm.EditText.ReplaceAllCommand.ExecuteAsync(null);
        await vm.EditText.SaveCommand.ExecuteAsync(null);
        Assert.False(File.Exists(dialogs.SavePdf));
        Assert.Contains(L.F("Err_EditTooLong", "one thousand three hundred and fifty", "1,350"), vm.Status);
    }

    [AvaloniaFact]
    public async Task The_find_bar_searches_highlights_and_steps_through_hits_in_any_tool()
    {
        var pdf = TextPdf("f.pdf", "Yamada wrote this", "and Yamada signed", "Nothing here");
        var (w, vm) = await Open(pdf, new FakeDialogs());
        vm.ActiveTool = Tool.Comment;                              // works over any tool
        Dispatcher.UIThread.RunJobs();
        var bar = w.FindControl<Border>("FindBar")!;
        Assert.False(bar.IsEffectivelyVisible);

        vm.Find.OpenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsEffectivelyVisible);
        Assert.True(w.FindControl<TextBox>("FindBox")!.IsFocused);

        vm.Find.Query = "yamada";
        await vm.Find.SearchSoonAsync(0);
        Assert.Equal(2, vm.Find.Hits.Count);
        Assert.Equal(L.F("Ui_FindCount", 1, 2), vm.Find.CountText);
        Assert.Equal(2, vm.Find.BoxesOn(0).Count());
        Assert.Single(vm.Find.BoxesOn(0), b => b.Current);
        vm.Find.NextCommand.Execute(null);
        Assert.Equal(L.F("Ui_FindCount", 2, 2), vm.Find.CountText);
        vm.Find.NextCommand.Execute(null);                         // wraps around
        Assert.Equal(0, vm.Find.Index);
        vm.Find.PreviousCommand.Execute(null);
        Assert.Equal(1, vm.Find.Index);

        vm.Find.Query = "absent";
        await vm.Find.SearchSoonAsync(0);
        Assert.Equal(L.Get("Ui_FindNone"), vm.Find.CountText);
        Assert.False(vm.Find.HasHits);

        vm.Find.CloseCommand.Execute(null);
        Assert.Empty(vm.Find.BoxesOn(0));                         // nothing highlighted once closed
        vm.CloseDocumentCommand.Execute(null);
        Assert.False(vm.Find.IsOpen);
    }

    [AvaloniaFact]
    public async Task Bookmarks_are_added_nested_moved_and_saved_into_a_copy()
    {
        var pdf = Form("b.pdf", pages: 3);
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "b_bookmarks.pdf") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ShowBookmarksPanel = true;
        await vm.Bookmarks.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<ListBox>("BookmarkList")!.IsEffectivelyVisible);
        Assert.True(vm.Bookmarks.IsEmpty);
        Assert.False(vm.Bookmarks.CanSave);

        await vm.Bookmarks.AddCommand.ExecuteAsync(null);           // page 1
        vm.Bookmarks.Selected!.Title = "Cover";
        vm.PageIndex = 1;
        await vm.Bookmarks.AddCommand.ExecuteAsync(null);           // page 2, after "Cover"
        vm.Bookmarks.Selected!.Title = "Details";
        vm.Bookmarks.IndentCommand.Execute(null);                   // under "Cover"
        vm.PageIndex = 2;
        vm.Bookmarks.Selected = null;
        await vm.Bookmarks.AddCommand.ExecuteAsync(null);           // page 3, at the end
        vm.Bookmarks.Selected!.Title = "Annex";
        vm.Bookmarks.UpCommand.Execute(null);                       // before "Cover"
        Assert.Equal(["Annex", "Cover", "Details"], vm.Bookmarks.Rows.Select(r => r.Title));
        Assert.Equal([0, 0, 1], vm.Bookmarks.Rows.Select(r => r.Level));
        Assert.True(vm.Bookmarks.CanSave);

        vm.Bookmarks.Selected = vm.Bookmarks.Rows[2];
        vm.Bookmarks.OutdentCommand.Execute(null);
        Assert.Equal([0, 0, 0], vm.Bookmarks.Rows.Select(r => r.Level));
        vm.Bookmarks.IndentCommand.Execute(null);

        vm.Bookmarks.Go(vm.Bookmarks.Rows[0]);                     // a click opens its page
        Assert.Equal(2, vm.PageIndex);

        await vm.Bookmarks.SaveCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        var outline = Washi.Core.Pages.PdfBookmarks.Read(dialogs.SavePdf!);
        Assert.Equal(["Annex", "Cover"], outline.Select(b => b.Title));
        Assert.Equal((2, 0), (outline[0].Page, outline[1].Page));
        Assert.Equal(("Details", 1), (outline[1].Children[0].Title, outline[1].Children[0].Page));
        Assert.Equal(3, vm.Bookmarks.Rows.Count);                   // the copy's outline is shown
        Assert.False(vm.Bookmarks.CanSave);

        vm.Bookmarks.Selected = vm.Bookmarks.Rows[1];
        vm.Bookmarks.RemoveCommand.Execute(null);                   // with "Details" under it
        Assert.Equal(["Annex"], vm.Bookmarks.Rows.Select(r => r.Title));
    }

    private sealed class FakePrinting(params Washi.Core.Printing.PrinterInfo[] printers) : Washi.Core.Printing.IPrinting
    {
        public List<(string Pdf, Washi.Core.Printing.PrintOptions Options)> Jobs { get; } = [];
        public IReadOnlyList<Washi.Core.Printing.PrinterInfo> Printers() => printers;
        public Washi.Core.Printing.PrintResult Print(string pdf, Washi.Core.Printing.PrintOptions options)
        {
            Washi.Core.Printing.PdfPrinting.Check(pdf, options);
            Jobs.Add((pdf, options));
            return new(options.Printer ?? "", "1", 1);
        }
    }

    [AvaloniaFact]
    public async Task Print_shows_its_card_and_sends_the_chosen_pages_to_the_chosen_printer()
    {
        var pdf = Form("p.pdf", pages: 3);
        var (w, vm) = await Open(pdf, new FakeDialogs());
        var fake = new FakePrinting(new("Home", false), new("Office", true));
        vm.Print.Service = fake;
        Assert.True(w.FindControl<Button>("PrintButton")!.IsEffectivelyVisible);
        vm.PageIndex = 1;
        await vm.Print.OpenCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Border>("PrintOverlay")!.IsEffectivelyVisible);
        Assert.Equal("Office", vm.Print.Printer!.Name);              // the default printer is chosen
        Assert.True(vm.Print.CanPrint);

        vm.Print.CurrentPage = true;
        vm.Print.Copies = 2;
        vm.Print.TwoSided = true;
        vm.Print.ActualSize = true;
        Assert.False(vm.Print.FitToPage);
        await vm.Print.PrintCommand.ExecuteAsync(null);
        var (file, options) = Assert.Single(fake.Jobs);
        Assert.Equal(pdf, file);
        Assert.Equal(new Washi.Core.Printing.PrintOptions("Office", 2, "2", true, false, false), options);
        Assert.False(vm.Print.IsOpen);
        Assert.Equal(L.F("Ui_Printed", 1, "p.pdf", "Office"), vm.Status);

        // a bad range keeps the card open and says why
        await vm.Print.OpenCommand.ExecuteAsync(null);
        vm.Print.SomePages = true;
        vm.Print.Range = "8";
        await vm.Print.PrintCommand.ExecuteAsync(null);
        Assert.True(vm.Print.IsOpen);
        Assert.NotEqual("", vm.Print.Error);
        Assert.Single(fake.Jobs);

        // no printer set up: nothing can be sent
        vm.Print.CancelCommand.Execute(null);
        vm.Print.Service = new FakePrinting();
        await vm.Print.OpenCommand.ExecuteAsync(null);
        Assert.Equal(L.Get("Ui_PrintNoPrinters"), vm.Print.Error);
        Assert.False(vm.Print.CanPrint);
    }

    [AvaloniaFact]
    public async Task Existing_comments_are_chosen_changed_moved_removed_and_answered()
    {
        var flat = Form("c.pdf");
        var commented = Path.Combine(_dir, "c_with.pdf");
        Washi.Core.Pages.PdfComments.Add(flat, commented,
        [
            new(0, Washi.Core.Pages.CommentKind.Note, new Washi.Core.PdfRect(500, 40, 518, 58), "Please check", Author: "Aoi"),
            new(0, Washi.Core.Pages.CommentKind.Box, new Washi.Core.PdfRect(100, 300, 200, 360)),
            new(0, Washi.Core.Pages.CommentKind.TextBox, new Washi.Core.PdfRect(300, 100, 460, 130), "Draft"),
        ]);
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "c_commented.pdf") };
        var (w, vm) = await Open(commented, dialogs);
        vm.ActiveTool = Tool.Comment;
        await vm.Comment.PrepareAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Comment.Existing.Count);
        Assert.False(vm.Comment.CanSave);

        var note = vm.Comment.HitExisting(505, 45)!;               // a click on the note's icon
        Assert.Equal("Please check", note.Text);
        vm.Comment.SelectedExisting = note;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Border>("ExistingCommentEditor")!.IsEffectivelyVisible);
        note.Text = "Checked";
        vm.Comment.ReplyDraft = "Thanks!";
        vm.Comment.ReplyCommand.Execute(null);
        vm.Comment.SetColorCommand.Execute("#24A148");
        vm.Comment.MoveExisting(note, new Washi.Core.PdfRect(520, 60, 538, 78));
        Assert.True(note.IsChanged);
        Assert.StartsWith("✎", note.Label);

        vm.Comment.SelectedExisting = vm.Comment.HitExisting(150, 330);   // the box
        vm.Comment.ToggleDeleteCommand.Execute(null);
        Assert.True(vm.Comment.CanSave);

        var text = vm.Comment.Existing.Single(e => e.Info.Type == "FreeText");
        vm.Comment.SelectedExisting = text;
        text.Text = "Final words";
        vm.Comment.RevertCommand.Execute(null);                     // changed my mind
        Assert.Equal("Draft", vm.Comment.SelectedExisting!.Text);

        await vm.Comment.SaveCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        var after = Washi.Core.Pages.PdfComments.List(dialogs.SavePdf!);
        var n = after.Single(c => c.Type == "Text" && c.ReplyTo is null);
        Assert.Equal(("Checked", "#24A148"), (n.Text, n.Color));
        Assert.Equal(520, n.Rect.X0, 1);
        Assert.Equal("Thanks!", after.Single(c => c.ReplyTo == n.Id).Text);
        Assert.DoesNotContain(after, c => c.Type == "Square");
        Assert.Equal("Draft", after.Single(c => c.Type == "FreeText").Text);
    }

    [AvaloniaFact]
    public async Task Compress_saves_a_smaller_copy_and_says_by_how_much()
    {
        // four pages whose content is stored uncompressed (as some programs write it)
        var pdf = Path.Combine(_dir, "big.pdf");
        var doc = new PdfDocument();
        for (int i = 0; i < 4; i++)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            for (int k = 0; k < 200; k++) g.DrawLine(XPens.Black, 40 + k, 40, 40 + k, 400);
        }
        doc.Options.CompressContentStreams = false;
        doc.Options.NoCompression = true;
        doc.Save(pdf);
        var dialogs = new FakeDialogs { SavePdf = Path.Combine(_dir, "big_compressed.pdf") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.ActiveTool = Tool.Compress;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Button>("CompressButton")!.IsEffectivelyVisible);
        Assert.StartsWith(L.F("Ui_CompressSize", "")[..2], vm.Compress.SizeText);
        Assert.Equal(Washi.Core.Pages.CompressLevel.Standard, vm.Compress.Level);
        vm.Compress.Light = true;
        Assert.Equal(Washi.Core.Pages.CompressLevel.Light, vm.Compress.Level);

        await vm.Compress.CompressCommand.ExecuteAsync(null);
        Assert.True(dialogs.SavePdf == vm.PdfPath, vm.Status);
        Assert.True(new FileInfo(dialogs.SavePdf!).Length < new FileInfo(pdf).Length);
        Assert.Contains("→", vm.Compress.Result);
        Assert.Equal(4, vm.PageCount);
    }

    [AvaloniaFact]
    public async Task A_digital_id_is_made_or_opened_and_signs_a_copy_whose_signature_is_listed()
    {
        var pdf = Form("d.pdf");
        var idPath = Path.Combine(_dir, "me.p12");
        var dialogs = new FakeDialogs { SaveDigitalId = idPath, DigitalId = idPath, SavePdf = Path.Combine(_dir, "d_signed.pdf") };
        var (w, vm) = await Open(pdf, dialogs);
        vm.Sign.StorePath = Path.Combine(_dir, "signature.txt");
        vm.ActiveTool = Tool.Sign;
        await vm.Sign.PrepareAsync();
        Dispatcher.UIThread.RunJobs();
        var d = vm.Sign.Digital;
        Assert.False(d.HasSignatures);
        Assert.Equal("invisible", d.Place!.Kind);

        // make an ID here (it is saved where the dialog says) …
        d.Creating = true;
        d.NewName = "Washi Tester";
        d.NewPassword = "secret-1";
        await d.CreateIdCommand.ExecuteAsync(null);
        Assert.True(File.Exists(idPath));
        Assert.Equal("Washi Tester", d.Id!.Name);
        // … or open one: a wrong password is refused, the right one adds it
        d.Ids.Clear();
        await d.OpenIdFileCommand.ExecuteAsync(null);
        d.IdPassword = "nope";
        d.UnlockIdFileCommand.Execute(null);
        Assert.Empty(d.Ids);
        d.IdPassword = "secret-1";
        d.UnlockIdFileCommand.Execute(null);
        Assert.Equal("Washi Tester", d.Id!.Name);

        // a box drawn on the page becomes a place to sign
        vm.Sign.SetInk(Washi.Core.Pages.Ink.From([[(0.0, 0.0), (20, 10), (40, 0)]]));
        vm.Sign.Place(new Washi.Core.PdfRect(500, 300, 700, 360));
        Assert.Equal("box", d.Place!.Kind);
        d.Reason = "Approved";
        Assert.True(d.CanSign);
        await d.SignCommand.ExecuteAsync(null);
        Assert.Equal(dialogs.SavePdf, vm.PdfPath);
        Assert.Empty(vm.Sign.Placed);                               // that box became the digital signature
        var row = Assert.Single(d.Signatures);
        Assert.True(row.Check.Intact);
        Assert.Equal("unverified", row.Check.Status);               // a self-signed ID
        Assert.Equal("Approved", row.Check.Reason);
        Assert.True(w.FindControl<ItemsControl>("SignatureChecks")!.IsEffectivelyVisible);
    }

    [Fact]
    public async Task The_pdfium_lock_is_released_even_when_the_caller_never_resumes()
    {
        // before: the lock was released only after a hop back to the caller's context, so a thumbnail render still
        // running when one headless test ended kept it forever, and the next test that used PDFium hung
        var old = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new StoppedContext());
        try { _ = MainViewModel.Pdfium(() => 42); }
        finally { SynchronizationContext.SetSynchronizationContext(old); }

        Assert.True(await PdfiumDocument.Gate.WaitAsync(TimeSpan.FromSeconds(5)));
        PdfiumDocument.Gate.Release();
    }

    [AvaloniaFact]
    public async Task Organize_turns_moves_removes_and_inserts_pages_then_opens_the_result()
    {
        var a = Form("a.pdf", pages: 3);
        var b = Form("b.pdf");
        var output = Path.Combine(_dir, "organized.pdf");
        var (w, vm) = await Open(a, new FakeDialogs { Pdfs = [b], SavePdf = output });
        var org = vm.Organize;

        vm.ActiveTool = Tool.Organize;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["1", "2", "3"], org.Pages.Select(p => p.Label));
        Assert.False(org.CanSave);                          // nothing changed yet
        Assert.False(org.RemoveCommand.CanExecute(null));   // nothing selected

        // the grid's selection is the view model's selection, and one page selected shows it
        var list = w.FindControl<ListBox>("OrganizeList")!;
        list.SelectedItem = org.Pages[2];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([org.Pages[2]], org.Selected);
        Assert.Equal(2, vm.PageIndex);

        org.MoveUpCommand.Execute(null);
        org.RotateRightCommand.Execute(null);
        Assert.Equal(["1", "3", "2"], org.Pages.Select(p => p.Label));
        Assert.Equal(90, org.Pages[1].Rotate);
        Assert.Equal("↻ 90°", org.Pages[1].RotateText);

        org.Selected.Clear();
        org.Selected.Add(org.Pages[0]);
        org.RemoveCommand.Execute(null);                     // the selection moves to the next page, now "3"
        Assert.Equal(["3", "2"], org.Pages.Select(p => p.Label));
        await org.InsertFilesCommand.ExecuteAsync(null);     // b.pdf goes in after the selected page
        Assert.Equal(3, org.Pages.Count);
        Assert.True(org.Pages[1].Inserted);
        Assert.Contains("b.pdf", org.Pages[1].Label);
        Assert.Equal(L.F("Ui_OrganizeSummary", 3, 1, 1, 1), org.Summary);
        Assert.True(org.CanSave);

        await org.SaveCommand.ExecuteAsync(null);
        Assert.Equal(output, vm.PdfPath);
        Assert.Equal(3, vm.PageCount);
        Assert.Equal(["1", "2", "3"], org.Pages.Select(p => p.Label));   // the new file, fresh
        Assert.False(org.CanSave);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal([90, 0, 0], doc.Pages.Cast<PdfPage>().Select(p => p.Rotate));
    }

    [AvaloniaFact]
    public async Task Pdfs_dropped_on_the_organize_tool_are_inserted_not_opened()
    {
        var a = Form("a.pdf", pages: 2);
        var (_, vm) = await Open(a, new FakeDialogs());
        vm.ActiveTool = Tool.Organize;
        Dispatcher.UIThread.RunJobs();
        await vm.DropAsync([Form("b.pdf", pages: 2)]);
        Assert.Equal(a, vm.PdfPath);
        Assert.Equal(4, vm.Organize.Pages.Count);
        Assert.Equal(2, vm.Organize.Pages.Count(p => p.Inserted));
    }

    [AvaloniaFact]
    public async Task Combine_starts_with_the_open_file_and_opens_the_result()
    {
        var a = Form("a.pdf");
        var b = Form("b.pdf", pages: 2);
        var output = Path.Combine(_dir, "ab.pdf");
        var dialogs = new FakeDialogs { Pdfs = [b], SavePdf = output };
        var (_, vm) = await Open(a, dialogs);

        vm.ActiveTool = Tool.Combine;
        for (int i = 0; i < 50 && vm.Combine.Files.Count == 0; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(["a.pdf"], vm.Combine.Files.Select(f => f.Name));
        await vm.Combine.AddFilesCommand.ExecuteAsync(null);
        Assert.True(vm.Combine.CanCombine);

        vm.Combine.Selected = vm.Combine.Files[1];
        vm.Combine.MoveUpCommand.Execute(null);
        Assert.Equal(["b.pdf", "a.pdf"], vm.Combine.Files.Select(f => f.Name));

        await vm.Combine.RunCommand.ExecuteAsync(null);
        Assert.Equal(output, vm.PdfPath);
        Assert.Equal(3, vm.PageCount);
        Assert.Empty(vm.Combine.Files);
    }

    [AvaloniaFact]
    public async Task Protected_files_are_listed_but_block_combining()
    {
        var a = Form("a.pdf");
        var locked = Path.Combine(_dir, "locked.pdf");
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(Form("src.pdf"), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.OwnerPassword = "owner-only";
            doc.SecuritySettings.PermitAssembleDocument = false;
            doc.Save(locked);
        }
        var (_, vm) = await Open(a, new FakeDialogs());
        vm.ActiveTool = Tool.Combine;
        for (int i = 0; i < 50 && vm.Combine.Files.Count == 0; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        await vm.Combine.AddAsync([locked]);
        Assert.True(vm.Combine.Files[1].Protected);
        Assert.False(vm.Combine.CanCombine);
        Assert.Contains("locked.pdf", vm.Status);
        vm.Combine.Selected = vm.Combine.Files[1];
        vm.Combine.RemoveCommand.Execute(null);
        Assert.False(vm.Combine.CanCombine);   // one file left
    }

    [AvaloniaFact]
    public async Task Dropping_files_on_combine_adds_them()
    {
        var (_, vm) = await Open(Form("a.pdf"), new FakeDialogs());
        vm.ActiveTool = Tool.Combine;
        for (int i = 0; i < 50 && vm.Combine.Files.Count == 0; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        await vm.DropAsync([Form("b.pdf"), Path.Combine(_dir, "notes.txt")]);
        Assert.Equal(2, vm.Combine.Files.Count);
    }

    [AvaloniaFact]
    public async Task Split_previews_the_files_and_writes_them_to_the_folder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "parts")).FullName;
        var (_, vm) = await Open(Form("doc.pdf", pages: 3), new FakeDialogs { Folder = folder });
        vm.ActiveTool = Tool.Split;

        Assert.Contains("p1, p2, p3", vm.Split.Preview);        // empty = one file per page
        vm.Split.Ranges = "1-2, 3";
        Assert.Contains("p1-2, p3", vm.Split.Preview);
        vm.Split.Ranges = "4";
        Assert.False(vm.Split.CanSplit);
        vm.Split.Ranges = "2-3";
        await vm.Split.RunCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(folder, "doc_p2-3.pdf")));
    }

    [AvaloniaFact]
    public async Task Ocr_with_the_ai_engine_asks_first_then_writes_a_searchable_pdf()
    {
        var output = Path.Combine(_dir, "scan_ocr.pdf");
        var dialogs = new FakeDialogs { SavePdf = output, ConfirmAnswer = false };
        var (_, vm) = await Open(Scan(), dialogs);
        var model = new ScriptedModel(_ => JsonSerializer.Serialize(new { lines = new[] { new { text = "Washi 2026", box = new[] { 100, 450, 600, 550 } } } }));
        vm.Ocr.ModelFactory = () => model;
        vm.ActiveTool = Tool.Ocr;
        await vm.Ocr.RefreshPlanAsync();
        Assert.True(vm.Ocr.CanRun);
        vm.Ocr.Engine = OcrToolViewModel.Engines.Single(e => e.Value == "ai");

        await vm.Ocr.RunCommand.ExecuteAsync(null);       // declined: nothing sent
        Assert.Equal(0, model.Calls);
        Assert.Contains("1 page image", dialogs.LastConfirm);

        dialogs.ConfirmAnswer = true;
        await vm.Ocr.RunCommand.ExecuteAsync(null);
        Assert.Equal(1, model.Calls);
        Assert.Equal(output, vm.PdfPath);                   // the result is opened
        using var d = PdfiumDocument.Open(output);
        Assert.Equal("Washi 2026", d.GetPageText(0).Trim());
        await vm.Ocr.RefreshPlanAsync();
        Assert.False(vm.Ocr.CanRun);                        // already searchable
    }

    /// <summary>Reads every image as one line of text.</summary>
    private sealed class StubOcr(string text) : Washi.Core.Ocr.IOcrEngine
    {
        public int Calls;
        public string Name => "stub";
        public string Describe => "stub";
        public bool SendsDataOffDevice => false;
        public int Dpi => 150;
        public Task<IReadOnlyList<Washi.Core.Ocr.OcrLine>> RecognizeAsync(Washi.Core.Ocr.OcrImage image, IReadOnlyList<string> languages, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<Washi.Core.Ocr.OcrLine>>([new(text, new Washi.Core.PdfRect(0, 0, image.Width, image.Height), 1)]);
        }
    }

    [AvaloniaFact]
    public async Task Printed_values_become_editable_field_values()
    {
        // a cell with something printed in it (drawn, as on many official forms), and an empty cell
        var pdf = Path.Combine(_dir, "printed.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(842);
            page.Height = XUnit.FromPoint(595);
            using var g = XGraphics.FromPdfPage(page);
            var pen = new XPen(XColors.Black, 1);
            g.DrawRectangle(pen, 40, 40, 400, 30);
            g.DrawRectangle(pen, 40, 100, 400, 30);
            // the "printed value": a small image of dark glyph-like marks, as forms that print data as pictures do
            int iw = 80, ih = 20;
            var px = new byte[iw * ih * 3];
            Array.Fill(px, (byte)255);
            for (int y = 4; y < 16; y++) for (int x = 6; x < 74; x++) if ((x / 6) % 2 == 0) px[(y * iw + x) * 3] = px[(y * iw + x) * 3 + 1] = px[(y * iw + x) * 3 + 2] = 0;
            var png = Path.Combine(_dir, "value.png");
            using (var fs = File.Create(png)) PngWriter.Write(new RgbImage(iw, ih, px), fs);
            g.DrawImage(XImage.FromFile(png), 60, 50, 40, 10);
            doc.Save(pdf);
        }
        var (_, vm) = await Open(pdf, new FakeDialogs());
        Assert.True(vm.Fields.Count >= 2, string.Join("; ", vm.Fields.Select(f => $"{f.Name} {f.X0},{f.Y0},{f.X1},{f.Y1}")) + " | " + vm.Status);
        int fields = vm.Fields.Count;
        var ocr = new StubOcr("林 燁");
        vm.OcrFactory = () => ocr;
        var typed = vm.Fields.First(f => f.Y0 > 90);                     // the empty cell: the user typed here already
        typed.Value = "入力済み";

        await vm.MovePrintedCommand.ExecuteAsync(null);
        Assert.Equal("入力済み", typed.Value);                            // typed values are never replaced

        Assert.Equal(1, ocr.Calls);                                  // one OCR call for the whole document
        Assert.Equal(Path.Combine(_dir, "printed_erased.pdf"), vm.PdfPath);
        Assert.Equal(fields, vm.Fields.Count);                       // the fields are kept
        var filled = Assert.Single(vm.Fields, f => f.Value.Length > 0 && !ReferenceEquals(f, typed));
        Assert.Equal("林 燁", filled.Value);
        Assert.InRange(filled.Y0, 35, 45);                            // the cell that held the print
        Assert.True(vm.PreviewMode);
        // a second conversion (one field, from its panel) updates the same copy: no _erased_erased.pdf
        await vm.MovePrintedCommand.ExecuteAsync(typed);                // nothing printed there: nothing to erase
        Assert.Equal(Path.Combine(_dir, "printed_erased.pdf"), vm.PdfPath);
        Assert.False(File.Exists(Path.Combine(_dir, "printed_erased_erased.pdf")));
        Assert.Single(Directory.GetFiles(_dir, "printed_erased*.pdf"));

        using var d = Washi.Core.Pdfium.PdfiumDocument.Open(vm.PdfPath!);
        var img = d.RenderPage(0, 1);
        int dark = 0;
        for (int y = 51; y < 60; y++) for (int x = 60; x < 93; x++) if (img.Pixels[(y * img.Width + x) * 3] < 128) dark++;
        Assert.Equal(0, dark);                                       // the print is gone from the copy
    }

    [AvaloniaFact]
    public async Task The_field_list_shows_what_is_in_the_fields_and_only_filled_ones_while_filling()
    {
        var (w, vm) = await Open(Form("l.pdf"), new FakeDialogs());
        var text = vm.Fields.First(f => f.Kind == Washi.Masume.FieldKind.Text);
        Assert.Equal(vm.PageFields, vm.ListFields);                 // preparing the form: every field
        Assert.Equal(text.Name, text.Display);                      // nothing better to show yet
        Assert.False(text.ShowName);

        text.Value = "林 燁";
        Assert.Equal("林 燁", text.Display);                         // the value, with the name small under it
        Assert.True(text.ShowName);

        vm.PreviewMode = true;                                      // filling: an overview of what is filled in
        Assert.Same(text, Assert.Single(vm.ListFields));
        Assert.Contains("1", vm.ListCaption);
        var empty = vm.Fields.First(f => !f.HasValue);
        vm.SelectedField = empty;                                   // not in the list: it must stay selected
        Dispatcher.UIThread.RunJobs();
        Assert.Same(empty, vm.SelectedField);
        empty.Value = empty.Kind == Washi.Masume.FieldKind.Checkbox ? "true" : "x";
        Assert.Equal(2, vm.ListFields.Count);
        Assert.False(w.FindControl<TextBox>("FieldNameBox")!.IsEffectivelyVisible);    // no design-time properties while filling
    }

    [AvaloniaFact]
    public async Task Erase_tool_shows_what_goes_then_saves_a_copy_and_opens_it()
    {
        var pdf = Form("e.pdf");
        var (w, vm) = await Open(pdf, new FakeDialogs());
        vm.OcrFactory = () => new StubOcr("x");
        vm.Erase.MakeEditable = false;                                       // erase only
        vm.ActiveTool = Tool.Erase;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.Erase.CanErase);

        await vm.Erase.AddBoxAsync(new Washi.Core.PdfRect(34, 134, 54, 154));   // around the small black square
        Assert.True(vm.Erase.CanErase);
        Assert.Equal(Washi.Core.Pages.EraseKind.Drawing, Assert.Single(vm.Erase.Targets).Kind);
        Assert.Contains("1", vm.Erase.PlanText);
        Assert.True(w.FindControl<Avalonia.Controls.Button>("EraseButton")!.IsEffectivelyEnabled);

        await vm.Erase.EraseCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(_dir, "e_erased.pdf"), vm.PdfPath);       // the copy is opened; the original stays
        Assert.True(File.Exists(pdf));
        Assert.Empty(vm.Erase.Boxes);
        Assert.Contains("e_erased.pdf", vm.Status);

        // a second erase of the same original never overwrites the first copy
        await vm.LoadAsync(pdf);
        await vm.Erase.AddBoxAsync(new Washi.Core.PdfRect(34, 134, 54, 154));
        await vm.Erase.EraseCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(_dir, "e_erased_2.pdf"), vm.PdfPath);
    }

    [AvaloniaFact]
    public async Task A_box_drawn_by_hand_becomes_an_editable_field_with_the_old_text()
    {
        var pdf = Form("h.pdf");
        var (_, vm) = await Open(pdf, new FakeDialogs());
        var ocr = new StubOcr("旧住所");
        vm.OcrFactory = () => ocr;
        // what the user already did to the fields must survive the erase
        var mine = vm.Fields.First(f => f.Kind == Washi.Masume.FieldKind.Text);
        mine.Name = "申請者氏名";
        mine.Value = "山田 太郎";
        int before = vm.Fields.Count;
        vm.ActiveTool = Tool.Erase;
        Assert.True(vm.Erase.MakeEditable);                                   // on by default
        var box = new Washi.Core.PdfRect(34, 134, 54, 154);                  // around the small black square
        await vm.Erase.AddBoxAsync(box);
        await vm.Erase.EraseCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(_dir, "h_erased.pdf"), vm.PdfPath);
        Assert.Equal(Tool.Form, vm.ActiveTool);
        Assert.True(vm.PreviewMode);
        Assert.Same(mine, vm.Fields.Single(x => x.Name == "申請者氏名"));      // same fields, not detected anew
        Assert.Equal("山田 太郎", mine.Value);
        Assert.InRange(vm.Fields.Count, before, before + 1);
        var f = Assert.Single(vm.Fields, x => x.Value == "旧住所");
        Assert.True(f.Rect.Contains(44, 144));                               // in place of the erased print
        Assert.Equal(1, ocr.Calls);
    }

    [AvaloniaFact]
    public async Task Pinch_zoom_is_free_but_clamped_and_ends_fit_width()
    {
        var (w, vm) = await Open(Form("a.pdf"), new FakeDialogs());
        w.ZoomAround(1.37, new Avalonia.Point(200, 200));
        Assert.Equal(1.37, vm.Zoom, 3);
        w.ZoomAround(40, default);
        Assert.Equal(4, vm.Zoom);
        w.ZoomAround(0.01, default);
        Assert.Equal(0.25, vm.Zoom);
        vm.ViewportWidth = 1200;                 // no longer fitted: the window size does not change the zoom
        Assert.Equal(0.25, vm.Zoom);
    }

    [AvaloniaFact]
    public void Language_choice_is_saved_applied_and_reported()
    {
        var path = Path.Combine(_dir, "settings.json");
        var before = Washi.Core.Localization.L.Culture;
        var envBefore = Environment.GetEnvironmentVariable("WASHI_LANG");
        Environment.SetEnvironmentVariable("WASHI_LANG", null);
        try
        {
            var vm = new SettingsViewModel(path);
            int changed = 0;
            vm.LanguageChanged += () => changed++;
            vm.Language = SettingsViewModel.Languages.Single(l => l.Value == "ko");
            Assert.Equal(1, changed);
            Assert.Equal("ko", Washi.Core.Localization.L.Culture.TwoLetterISOLanguageName);
            Assert.Equal("ko", Washi.App.Services.AppSettings.Load(path).Language);
            Assert.Equal("설정", Washi.Core.Localization.L.Get("Ui_Settings"));

            vm.Language = SettingsViewModel.Languages[0];       // back to the system language
            Assert.Null(Washi.App.Services.AppSettings.Load(path).Language);
            Assert.Contains("AGPL", Washi.Core.Localization.L.Get("Ui_LicenseText") + "AGPL");
            Assert.All(SettingsViewModel.Components, c => Assert.StartsWith("https://", c.Url));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WASHI_LANG", envBefore);
            Washi.Core.Localization.L.Culture = before;
        }
    }

    [AvaloniaFact]
    public void A_broken_settings_file_falls_back_to_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(Washi.App.Services.AppSettings.Load(path).Language);
    }

    [AvaloniaFact]
    public void Translated_lists_follow_a_language_switch()
    {
        var before = Washi.Core.Localization.L.Culture;
        try
        {
            Washi.Core.Localization.L.Culture = System.Globalization.CultureInfo.GetCultureInfo("ja");
            Assert.Equal("テキスト", MainViewModel.Kinds[0].Text);
            Washi.Core.Localization.L.Culture = System.Globalization.CultureInfo.GetCultureInfo("zh-Hans");
            Assert.Equal("文本", MainViewModel.Kinds[0].Text);
            Assert.DoesNotContain(OcrToolViewModel.Engines, e => e.Text.Contains("コンピュータ"));
        }
        finally { Washi.Core.Localization.L.Culture = before; }
    }

    [AvaloniaFact]
    public async Task Preview_types_into_fields_ticks_boxes_and_saves_the_values()
    {
        var output = Path.Combine(_dir, "filled.pdf");
        var (w, vm) = await Open(Form("a.pdf"), new FakeDialogs { SavePdf = output });
        var canvas = w.FindControl<Washi.App.Controls.FieldCanvas>("Canvas")!;
        var editor = w.FindControl<Avalonia.Controls.TextBox>("FieldEditor")!;
        var text = vm.Fields.First(f => f.Kind == Washi.Masume.FieldKind.Text);
        var box = vm.Fields.First(f => f.Kind == Washi.Masume.FieldKind.Checkbox);

        vm.PreviewModeOnCommand.Execute(null);
        Assert.True(vm.PreviewMode);
        Assert.False(vm.IsSelectMode);

        // a checkbox toggles
        vm.ActivateField(box);
        Assert.True(box.IsChecked);
        vm.ActivateField(box);
        Assert.False(box.IsChecked);
        vm.ActivateField(box);

        // a text field opens the inline editor over the field; leaving it keeps the text
        vm.ActivateField(text);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.IsVisible);
        Assert.Equal(text.X0 * vm.Zoom, editor.Margin.Left, 1);
        editor.Text = "林 燁";
        canvas.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.IsVisible);
        Assert.Equal("林 燁", text.Value);

        // Delete clears the value in Preview; the field stays
        vm.SelectedField = text;
        canvas.Focus();
        w.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Delete, null);
        Assert.Equal("", text.Value);
        Assert.Contains(text, vm.Fields);
        text.Value = "林 燁";

        // the values go into the saved PDF
        await vm.MakeFillableCommand.ExecuteAsync(null);
        var written = AcroFormFields.List(output).ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("林 燁", written[text.Name]);
        Assert.Equal("Yes", written[box.Name]);
        Assert.Contains("2", vm.Status);
    }

    [AvaloniaFact]
    public async Task Changing_a_fields_type_clears_its_value()
    {
        var (_, vm) = await Open(Form("a.pdf"), new FakeDialogs());
        var f = vm.Fields.First(x => x.Kind == Washi.Masume.FieldKind.Text);
        f.Value = "abc";
        f.Kind = Washi.Masume.FieldKind.Checkbox;
        Assert.Equal("", f.Value);
    }
}
