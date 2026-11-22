// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Washi.App;
using Washi.App.Controls;
using Washi.App.Services;
using Washi.App.ViewModels;
using Washi.Core.Forms;
using Washi.Masume;

[assembly: AvaloniaTestApplication(typeof(Washi.App.Tests.TestApp))]
[assembly: AvaloniaTestFramework]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Washi.App.Tests;

public static class TestApp
{
    // real Skia rendering, so CaptureRenderedFrame shows what a user would see
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Answers the file pickers without a user.</summary>
internal sealed class FakeDialogs : IDialogs
{
    public string? Pdf, OpenJson, SaveJson, SavePdf, Font;
    public Task<string?> OpenPdfAsync() => Task.FromResult(Pdf);
    public List<string> Pdfs = [];
    public string? Folder;
    public Task<IReadOnlyList<string>> OpenPdfsAsync() => Task.FromResult<IReadOnlyList<string>>(Pdfs);
    public Task<string?> PickFolderAsync() => Task.FromResult(Folder);
    public Task<string?> OpenFieldsAsync() => Task.FromResult(OpenJson);
    public Task<string?> SaveFieldsAsync(string suggestedName) => Task.FromResult(SaveJson);
    public Task<string?> SavePdfAsync(string suggestedName) => Task.FromResult(SavePdf);
    public Task<string?> OpenFontAsync() => Task.FromResult(Font);
    public bool ConfirmAnswer = true;
    public string? LastConfirm;
    public Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel)
    {
        LastConfirm = message;
        return Task.FromResult(ConfirmAnswer);
    }
}

public sealed class EditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-app-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A flat form: a wide text cell, a 12-box comb, a checkbox square.</summary>
    private string FlatForm()
    {
        var path = Path.Combine(_dir, "form.pdf");
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(842);
        page.Height = XUnit.FromPoint(595);
        using (var g = XGraphics.FromPdfPage(page))
        {
            var pen = new XPen(XColors.Black, 1);
            g.DrawRectangle(pen, 40, 40, 400, 30);
            g.DrawRectangle(pen, 40, 90, 240, 30);
            for (int i = 1; i < 12; i++) g.DrawLine(pen, 40 + i * 20, 112, 40 + i * 20, 120);
            g.DrawRectangle(pen, 40, 140, 8, 8);
        }
        doc.Save(path);
        return path;
    }

    private static async Task<(MainWindow Window, MainViewModel Vm, FieldCanvas Canvas)> Open(string pdf, FakeDialogs? dialogs = null)
    {
        var window = new MainWindow(dialogs ?? new FakeDialogs());
        window.Show();
        await window.ViewModel.LoadAsync(pdf);
        Dispatcher.UIThread.RunJobs();
        return (window, window.ViewModel, window.FindControl<FieldCanvas>("Canvas")!);
    }

    /// <summary>Window coordinates of a point given in page points.</summary>
    private static Point At(MainWindow w, FieldCanvas c, double xPt, double yPt) =>
        c.TranslatePoint(new Point(xPt * c.Zoom, yPt * c.Zoom), w)!.Value;

    [AvaloniaFact]
    public async Task Opening_a_pdf_renders_the_page_and_detects_fields()
    {
        var (_, vm, _) = await Open(FlatForm());
        Assert.NotNull(vm.PageImage);
        Assert.Equal(842, vm.PageWidth, 0);
        Assert.Contains(vm.Fields, f => f.Kind == FieldKind.Comb && f.MaxLen == 12);
        Assert.Contains(vm.Fields, f => f.Kind == FieldKind.Checkbox);
        Assert.Equal(vm.Fields.Count, vm.PageFields.Count);
    }

    [AvaloniaFact]
    public async Task Dragging_a_field_moves_it_and_dragging_its_corner_resizes_it()
    {
        var (w, vm, c) = await Open(FlatForm());
        var f = vm.Fields.First(x => x.Kind == FieldKind.Text);
        var (x0, y0, x1, y1) = (f.X0, f.Y0, f.X1, f.Y1);
        var mid = ((x0 + x1) / 2, (y0 + y1) / 2);

        w.MouseDown(At(w, c, mid.Item1, mid.Item2), MouseButton.Left);
        w.MouseMove(At(w, c, mid.Item1 + 10, mid.Item2 + 5));
        w.MouseUp(At(w, c, mid.Item1 + 10, mid.Item2 + 5), MouseButton.Left);
        Assert.Same(f, vm.SelectedField);
        Assert.Equal(x0 + 10, f.X0, 1);
        Assert.Equal(y0 + 5, f.Y0, 1);
        Assert.Equal(x1 - x0, f.X1 - f.X0, 1);   // size unchanged

        w.MouseDown(At(w, c, f.X1, f.Y1), MouseButton.Left);
        w.MouseMove(At(w, c, f.X1 + 20, f.Y1 + 8));
        w.MouseUp(At(w, c, f.X1 + 20, f.Y1 + 8), MouseButton.Left);
        Assert.Equal(x1 - x0 + 20, f.X1 - f.X0, 1);
        Assert.Equal(y1 - y0 + 8, f.Y1 - f.Y0, 1);
    }

    [AvaloniaFact]
    public async Task Draw_mode_creates_a_field_and_delete_removes_it()
    {
        var (w, vm, c) = await Open(FlatForm());
        int before = vm.Fields.Count;
        vm.DrawMode = true;
        w.MouseDown(At(w, c, 500, 300), MouseButton.Left);
        w.MouseMove(At(w, c, 620, 330));
        w.MouseUp(At(w, c, 620, 330), MouseButton.Left);

        Assert.Equal(before + 1, vm.Fields.Count);
        var created = vm.SelectedField!;
        Assert.Equal(500, created.X0, 1);
        Assert.Equal(120, created.X1 - created.X0, 1);
        Assert.Equal(FieldKind.Text, created.Kind);

        // a click without dragging does not leave a zero-size field behind
        w.MouseDown(At(w, c, 700, 500), MouseButton.Left);
        w.MouseUp(At(w, c, 700, 500), MouseButton.Left);
        Assert.Equal(before + 1, vm.Fields.Count);

        vm.SelectedField = created;
        c.Focus();
        w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.Equal(before, vm.Fields.Count);
        Assert.Null(vm.SelectedField);
    }

    [AvaloniaFact]
    public async Task Arrow_keys_nudge_the_selected_field()
    {
        var (w, vm, c) = await Open(FlatForm());
        var f = vm.SelectedField = vm.Fields[0];
        double x = f.X0;
        c.Focus();
        w.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        w.KeyPress(Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
        Assert.Equal(x + 5.5, f.X0, 2);
    }

    [AvaloniaFact]
    public async Task Edited_fields_round_trip_through_json_and_become_a_fillable_pdf()
    {
        var dialogs = new FakeDialogs
        {
            SaveJson = Path.Combine(_dir, "fields.json"),
            OpenJson = Path.Combine(_dir, "fields.json"),
            SavePdf = Path.Combine(_dir, "out.pdf"),
        };
        var (_, vm, _) = await Open(FlatForm(), dialogs);
        var comb = vm.Fields.First(f => f.Kind == FieldKind.Comb);
        comb.Name = "マイナンバー";
        comb.MaxLen = 12;
        vm.Fields.Remove(vm.Fields.First(f => f.Kind == FieldKind.Checkbox));
        int count = vm.Fields.Count;

        await vm.SaveFieldsCommand.ExecuteAsync(null);
        vm.Fields.Clear();
        await vm.LoadFieldsCommand.ExecuteAsync(null);
        Assert.Equal(count, vm.Fields.Count);
        Assert.Contains(vm.Fields, f => f.Name == "マイナンバー" && f.MaxLen == 12);

        await vm.MakeFillableCommand.ExecuteAsync(null);
        var written = AcroFormFields.List(dialogs.SavePdf);
        Assert.Equal(count, written.Count);
        Assert.Contains(written, f => f.Name == "マイナンバー" && f.Comb && f.MaxLen == 12);
    }

    [AvaloniaFact]
    public async Task Changing_the_type_in_the_panel_updates_the_field()
    {
        var (_, vm, _) = await Open(FlatForm());
        var f = vm.SelectedField = vm.Fields.First(x => x.Kind == FieldKind.Text);
        f.Kind = FieldKind.Amount;
        Assert.Equal(2, f.Align);              // amounts are right-aligned
        f.Kind = FieldKind.Comb;
        Assert.True(f.MaxLen > 0);             // a comb needs a box count
    }

    /// <summary>
    /// Writes screenshots for a human to look at when WASHI_SCREENSHOT_DIR is set
    /// (optionally WASHI_SCREENSHOT_PDF for a real form). Does nothing otherwise.
    /// </summary>
    [AvaloniaFact]
    public async Task Screenshot()
    {
        if (Environment.GetEnvironmentVariable("WASHI_SCREENSHOT_DIR") is not { Length: > 0 } outDir) return;
        var pdf = Environment.GetEnvironmentVariable("WASHI_SCREENSHOT_PDF") is { Length: > 0 } p ? p : FlatForm();
        var culture = Washi.Core.Localization.L.Culture.Name;
        void Shot(Window win, string name)
        {
            Dispatcher.UIThread.RunJobs();
            win.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{name}_{culture}.png"));
        }

        var empty = new MainWindow(new FakeDialogs()) { Width = 1440, Height = 900 };
        empty.Show();
        Shot(empty, "empty");
        empty.Close();

        var w = new MainWindow(new FakeDialogs()) { Width = 1440, Height = 900 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var vm = w.ViewModel;
        await vm.LoadAsync(pdf);
        Dispatcher.UIThread.RunJobs();
        vm.FitWidthCommand.Execute(null);
        vm.SelectedField = vm.PageFields.FirstOrDefault(f => f.Kind == FieldKind.Comb) ?? vm.PageFields.FirstOrDefault();
        Shot(w, "editor");

        // Preview: a filled comb (dummy digits), a ticked box, and the inline editor open on another comb
        var combs = vm.PageFields.Where(f => f.Kind == FieldKind.Comb).ToList();
        vm.PreviewMode = true;
        if (combs.Count > 0) combs[0].Value = "123456789012";
        if (vm.PageFields.FirstOrDefault(f => f.Kind == FieldKind.Checkbox) is { } tick) tick.Value = "true";
        if (vm.PageFields.FirstOrDefault(f => f.Kind == FieldKind.Text) is { } name) name.Value = "サンプル 太郎";
        if (combs.Count > 1) { vm.ActivateField(combs[1]); Dispatcher.UIThread.RunJobs(); w.FindControl<TextBox>("FieldEditor")!.Text = "4020"; }
        Shot(w, "preview");
        vm.PreviewMode = false;

        vm.ShowPagesPanel = true;
        await Task.Delay(1500);   // thumbnails render in the background
        Shot(w, "pages");

        vm.ActiveTool = Tool.Ocr;
        await vm.Ocr.RefreshPlanAsync();
        Shot(w, "ocr");

        vm.ActiveTool = Tool.Erase;
        await vm.Erase.AddBoxAsync(new Washi.Core.PdfRect(180, 40, 540, 125));
        Shot(w, "erase");

        vm.Erase.Reset();
        vm.ActiveTool = Tool.Organize;
        Dispatcher.UIThread.RunJobs();
        if (vm.Organize.Pages.Count > 1)
        {
            vm.Organize.Selected.Add(vm.Organize.Pages[1]);
            vm.Organize.RotateRightCommand.Execute(null);
        }
        await Task.Delay(300);
        Shot(w, "organize");

        // Fill & Sign with a made-up signature (kept in a temp file, never the user's own)
        var store = Path.Combine(Path.GetTempPath(), $"washi-sig-{Guid.NewGuid():N}.txt");
        vm.Sign.StorePath = store;
        vm.ActiveTool = Tool.Sign;
        await vm.Sign.PrepareAsync();
        IReadOnlyList<(double, double)> Wave(double x0, double amp, double f) =>
            [.. Enumerable.Range(0, 60).Select(i => (x0 + i * 2.0, 30 + amp * Math.Sin(i / 60.0 * f) + 4 * Math.Sin(i / 3.0)))];
        vm.Sign.SetInk(Washi.Core.Pages.Ink.From([Wave(0, 12, 9), Wave(130, 8, 14)]));
        if (vm.Sign.Spots.FirstOrDefault() is { } spot) vm.Sign.SignSpotCommand.Execute(spot);
        else vm.Sign.Place(new Washi.Core.PdfRect(80, 120, 280, 170));
        Dispatcher.UIThread.RunJobs();
        Shot(w, "sign");
        File.Delete(store);

        // Comment: a highlight across the top lines, a note, a text box, a pen mark and a box
        vm.ActiveTool = Tool.Comment;
        await vm.Comment.PrepareAsync();
        vm.PageIndex = 0;
        await vm.Comment.PlaceAsync(new Washi.Core.PdfRect(30, 60, 600, 110));
        vm.Comment.Mode = Washi.Core.Pages.CommentKind.Note;
        vm.Comment.Draft = "確認してください";
        await vm.Comment.PlaceAsync(new Washi.Core.PdfRect(560, 40, 561, 41));
        vm.Comment.Mode = Washi.Core.Pages.CommentKind.TextBox;
        vm.Comment.Draft = "Line 1 is missing";
        await vm.Comment.PlaceAsync(new Washi.Core.PdfRect(330, 140, 520, 170));
        vm.Comment.Mode = Washi.Core.Pages.CommentKind.Pen;
        vm.Comment.AddStroke([.. Enumerable.Range(0, 40).Select(i => (60.0 + i * 4, 200.0 + 12 * Math.Sin(i / 4.0)))]);
        vm.Comment.Mode = Washi.Core.Pages.CommentKind.Box;
        await vm.Comment.PlaceAsync(new Washi.Core.PdfRect(40, 240, 300, 300));
        vm.Comment.Mode = Washi.Core.Pages.CommentKind.Highlight;
        Dispatcher.UIThread.RunJobs();
        Shot(w, "comment");

        // Protect: the panel with limits ticked (nothing is written)
        vm.Comment.Reset();
        vm.ActiveTool = Tool.Protect;
        await vm.Protect.PrepareAsync();
        vm.Protect.OpenPassword = "example";
        vm.Protect.Restrict = true;
        vm.Protect.PermissionsPassword = "example-2";
        Dispatcher.UIThread.RunJobs();
        Shot(w, "protect");
        vm.Protect.Reset();

        vm.ActiveTool = Tool.Redact;
        vm.Redact.Words = Environment.GetEnvironmentVariable("WASHI_SCREENSHOT_REDACT") ?? "氏名";
        vm.Redact.Phones = true;
        await vm.Redact.FindCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Shot(w, "redact");
        vm.Redact.Reset();

        vm.ActiveTool = Tool.Form;
        vm.Find.OpenCommand.Execute(null);
        vm.Find.Query = Environment.GetEnvironmentVariable("WASHI_SCREENSHOT_FIND") ?? "氏名";
        await vm.Find.SearchSoonAsync(0);
        Dispatcher.UIThread.RunJobs();
        Shot(w, "find");
        vm.Find.CloseCommand.Execute(null);

        vm.ShowBookmarksPanel = true;
        await vm.Bookmarks.LoadAsync();
        if (vm.Bookmarks.Rows.Count > 4) vm.Bookmarks.Go(vm.Bookmarks.Rows[4]);
        for (int i = 0; i < 20; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }   // the page is drawn, then scrolled
        Shot(w, "bookmarks");
        vm.ShowPagesPanel = true;

        await vm.Print.OpenCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Shot(w, "print");
        vm.Print.CancelCommand.Execute(null);

        vm.Erase.Reset();
        vm.ActiveTool = Tool.Form;
        if (Environment.GetEnvironmentVariable("WASHI_SCREENSHOT_MOVE") is { Length: > 0 })
        {
            await vm.MovePrintedCommand.ExecuteAsync(null);
            vm.SelectedField = vm.Fields.FirstOrDefault(f => f.Value.Length > 0);
            Shot(w, "moved");
            File.WriteAllLines(Path.Combine(outDir, "moved_values.txt"),
                [vm.Status, .. vm.Fields.Where(f => f.Value.Length > 0).Select(f => $"p{f.Page + 1} {f.Name}: {f.Value}")]);
        }

        vm.ActiveTool = Tool.Ask;
        Shot(w, "assistant");

        w.ShowSettings("mcp");
        Shot(w, "settings_mcp");
        w.ShowSettings("ai");
        Shot(w, "settings_ai");
        w.ShowSettings("about");
        Shot(w, "about");
    }
}

public sealed class EditorAiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-app-ai-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Model(bool offDevice) : Washi.Ai.IAiModel
    {
        public int Calls;
        public string Describe => "fake";
        public bool SendsDataOffDevice => offDevice;
        public bool AcceptsImages => true;
        public Task<string> CompleteAsync(Washi.Ai.AiRequest r, CancellationToken ct = default)
        {
            Calls++;
            var json = r.Prompt[(r.Prompt.IndexOf("Boxes:\n", StringComparison.Ordinal) + 7)..];
            var ids = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray().Select(b => b.GetProperty("id").GetInt32());
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
            {
                fields = ids.Select(id => new { id, name = $"AI_{id}", kind = "text", max_len = 0 }),
            }));
        }
    }

    private async Task<(MainViewModel Vm, FakeDialogs Dialogs)> Open()
    {
        var path = Path.Combine(_dir, "form.pdf");
        var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page)) g.DrawRectangle(new XPen(XColors.Black, 1), 40, 40, 300, 30);
        doc.Save(path);
        var dialogs = new FakeDialogs();
        var w = new MainWindow(dialogs);
        w.Show();
        await w.ViewModel.LoadAsync(path);
        return (w.ViewModel, dialogs);
    }

    [AvaloniaFact]
    public async Task Cloud_models_ask_first_and_cancel_sends_nothing()
    {
        var (vm, dialogs) = await Open();
        var model = new Model(offDevice: true);
        vm.ModelFactory = () => model;
        dialogs.ConfirmAnswer = false;
        await vm.NameWithAiCommand.ExecuteAsync(null);
        Assert.Equal(0, model.Calls);
        Assert.Contains("fake", dialogs.LastConfirm);
        Assert.DoesNotContain(vm.Fields, f => f.Name.StartsWith("AI_"));

        dialogs.ConfirmAnswer = true;
        await vm.NameWithAiCommand.ExecuteAsync(null);
        Assert.Equal(1, model.Calls);
        Assert.All(vm.Fields, f => Assert.StartsWith("AI_", f.Name));
    }

    [AvaloniaFact]
    public async Task Local_models_do_not_need_a_confirmation()
    {
        var (vm, dialogs) = await Open();
        vm.ModelFactory = () => new Model(offDevice: false);
        await vm.NameWithAiCommand.ExecuteAsync(null);
        Assert.Null(dialogs.LastConfirm);
        Assert.All(vm.Fields, f => Assert.StartsWith("AI_", f.Name));
    }
}
