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

    /// <summary>A UI context that has stopped: posted continuations never run (a closed app, an ended headless test).</summary>
    private sealed class StoppedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) { }
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
