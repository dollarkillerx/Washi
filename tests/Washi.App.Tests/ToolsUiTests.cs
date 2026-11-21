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
        editor.Text = "王 燁";
        canvas.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.IsVisible);
        Assert.Equal("王 燁", text.Value);

        // Delete clears the value in Preview; the field stays
        vm.SelectedField = text;
        canvas.Focus();
        w.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Delete, null);
        Assert.Equal("", text.Value);
        Assert.Contains(text, vm.Fields);
        text.Value = "王 燁";

        // the values go into the saved PDF
        await vm.MakeFillableCommand.ExecuteAsync(null);
        var written = AcroFormFields.List(output).ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("王 燁", written[text.Name]);
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
