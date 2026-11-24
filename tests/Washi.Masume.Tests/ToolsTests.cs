// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Washi.Tools;

namespace Washi.Masume.Tests;

/// <summary>The tool layer as an agent uses it: detect → make_fillable → list → fill → render.</summary>
public sealed class ToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("washi-tools-").FullName;
    private readonly ToolRegistry _tools;

    public ToolsTests()
    {
        _tools = WashiTools.CreateRegistry(new PathPolicy([_root]));
        MakeFlatForm(Path.Combine(_root, "form.pdf"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A flat A4 landscape form: a labelled text row, a 12-box comb, a checkbox square.</summary>
    internal static void MakeFlatForm(string path)
    {
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
    }

    private async Task<ToolResult> Call(string tool, object args) =>
        await _tools.Find(tool)!.InvokeAsync(JsonSerializer.SerializeToElement(args));

    private static JsonElement Json(ToolResult r) => JsonDocument.Parse(r.Text).RootElement;

    [Fact]
    public async Task Agent_flow_from_flat_pdf_to_filled_form()
    {
        var info = Json(await Call("pdf_info", new { path = "form.pdf" }));
        Assert.Equal(1, info.GetProperty("pages").GetInt32());
        Assert.Equal(0, info.GetProperty("form_fields").GetInt32());

        var detect = await Call("detect_fields", new { path = "form.pdf", overlay_page = 1 });
        Assert.False(detect.IsError, detect.Text);
        Assert.Single(detect.Images!);

        var made = await Call("make_fillable", new { path = "form.pdf", lang = "ja" });
        Assert.False(made.IsError, made.Text);
        var output = Json(made).GetProperty("output").GetString()!;
        Assert.True(File.Exists(output));
        Assert.EndsWith("form_fillable.pdf", output);

        var again = await Call("make_fillable", new { path = "form.pdf" });
        Assert.True(again.IsError);
        Assert.Contains("already exists", again.Text);

        var fields = Json(await Call("list_fields", new { path = "form_fillable.pdf" })).EnumerateArray().ToList();
        var text = fields.First(f => f.GetProperty("type").GetString() == "text" && !f.TryGetProperty("comb", out _)).GetProperty("name").GetString()!;
        var comb = fields.First(f => f.TryGetProperty("comb", out _)).GetProperty("name").GetString()!;
        var box = fields.First(f => f.GetProperty("type").GetString() == "checkbox").GetProperty("name").GetString()!;

        var fill = await Call("fill_fields", new
        {
            path = "form_fillable.pdf",
            values = new Dictionary<string, object> { [text] = "林 燁", [comb] = "123456789012", [box] = true, ["nope"] = "x" },
        });
        Assert.False(fill.IsError, fill.Text);
        var fr = Json(fill);
        Assert.Equal(3, fr.GetProperty("filled").GetInt32());
        Assert.Equal("nope", fr.GetProperty("unknown_names")[0].GetString());

        var after = Json(await Call("list_fields", new { path = "form_fillable_filled.pdf" })).EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("value").GetString());
        Assert.Equal("林 燁", after[text]);
        Assert.Equal("123456789012", after[comb]);
        Assert.Equal("Yes", after[box]);

        var png = await Call("render_page", new { path = "form_fillable_filled.pdf", page = 1, dpi = 72 });
        Assert.False(png.IsError, png.Text);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png.Images![0].Data[..4]);

        // a final version: what stands between it and PDF/A, then flattened, then a PDF/A copy
        var check = Json(await Call("convert_to_pdfa", new { path = "form_fillable_filled.pdf", check_only = true }));
        Assert.False(check.GetProperty("conforms").GetBoolean());
        Assert.Contains(check.GetProperty("in_the_way").EnumerateArray(), i => i.GetProperty("kind").GetString() == "Metadata");
        var flat = await Call("flatten_pdf", new { path = "form_fillable_filled.pdf" });
        Assert.False(flat.IsError, flat.Text);
        Assert.Equal(3, Json(flat).GetProperty("fields").GetInt32());
        Assert.Contains("no form fields", (await Call("list_fields", new { path = "form_fillable_filled_flattened.pdf" })).Text);
        var pdfa = await Call("convert_to_pdfa", new { path = "form_fillable_filled_flattened.pdf" });
        Assert.False(pdfa.IsError, pdfa.Text);
        Assert.Equal("PDF/A-2b", Json(pdfa).GetProperty("standard").GetString());
        Assert.True(Json(await Call("convert_to_pdfa", new { path = "form_fillable_filled_flattened_pdfa.pdf", check_only = true })).GetProperty("conforms").GetBoolean());

        // signed (agents cannot sign: done here), then long-term validation added by the agent and checked
        var id = Washi.Core.Signatures.DigitalIds.Create("Washi Tester", "", Path.Combine(_root, "id.p12"), "secret-1");
        Washi.Core.Signatures.PdfDigitalSigner.Sign(Path.Combine(_root, "form_fillable_filled_flattened_pdfa.pdf"), Path.Combine(_root, "signed.pdf"),
                                                    new Washi.Core.Signatures.SignRequest(id, Certify: 1));
        var ltv = await Call("add_ltv", new { path = "signed.pdf" });
        Assert.False(ltv.IsError, ltv.Text);
        Assert.Equal("Signature1", Json(ltv).GetProperty("ltv")[0].GetString());
        var sig = Json(await Call("verify_signatures", new { path = "signed_ltv.pdf" })).GetProperty("signatures")[0];
        Assert.Equal(("certification", "no changes", "unverified"), (sig.GetProperty("kind").GetString(), sig.GetProperty("certification_allows").GetString(), sig.GetProperty("status").GetString()));
        Assert.True(sig.GetProperty("ltv").GetBoolean() && sig.GetProperty("changes_allowed").GetBoolean());
        Assert.Equal("validation", sig.GetProperty("changes_after")[0].GetString());
        Assert.True((await Call("add_ltv", new { path = "signed.pdf", timestamp_server = "ftp://x" })).IsError);
    }

    [Fact]
    public async Task Paths_outside_the_allowed_folders_are_refused()
    {
        var r = await Call("pdf_info", new { path = "/etc/hosts" });
        Assert.True(r.IsError);
        Assert.Contains("outside", r.Text);

        var w = await Call("make_fillable", new { path = "form.pdf", output = Path.Combine(Path.GetTempPath(), "elsewhere.pdf") });
        Assert.True(w.IsError);
        Assert.Contains("outside", w.Text);

        var f = await Call("make_fillable", new { path = "form.pdf", embed_font = "/etc/hosts" });
        Assert.True(f.IsError);
    }

    [Fact]
    public async Task Symlinks_cannot_escape_the_allowed_folders()
    {
        if (OperatingSystem.IsWindows()) return;   // creating symlinks needs extra rights there
        Directory.CreateSymbolicLink(Path.Combine(_root, "etc-link"), "/etc");
        var r = await Call("pdf_info", new { path = "etc-link/hosts" });
        Assert.True(r.IsError);
        Assert.Contains("outside", r.Text);
    }

    [Fact]
    public async Task Bad_arguments_get_clear_errors()
    {
        Assert.Contains("'path' is required", (await Call("pdf_info", new { })).Text);
        Assert.Contains("outside 1-1", (await Call("extract_text", new { path = "form.pdf", pages = "2-3" })).Text);
        Assert.Contains("page must be 1-1", (await Call("render_page", new { path = "form.pdf", page = 5 })).Text);
        Assert.Contains("lang must be", (await Call("make_fillable", new { path = "form.pdf", lang = "xx" })).Text);
    }
}

/// <summary>The real server process, driven by the official MCP client SDK.</summary>
public sealed class McpServerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("washi-mcp-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Lists_tools_and_answers_calls_over_stdio()
    {
        ToolsTests.MakeFlatForm(Path.Combine(_root, "form.pdf"));
        var server = Path.Combine(AppContext.BaseDirectory, "washi-mcp.dll");
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "washi",
            Command = "dotnet",
            Arguments = [server, "--allow", _root],
        }));

        var tools = await client.ListToolsAsync();
        Assert.Equal(["add_comments", "add_ltv", "collect_form_data", "compare_pdfs", "compress_pdf", "convert_to_pdfa", "crop_pages", "detect_fields", "edit_attachments", "edit_comments", "edit_links", "edit_page_images", "edit_text", "encrypt_for_certificates", "erase_content", "export_form_data", "export_page_images", "extract_attachments", "extract_text", "fill_fields", "fill_form_from_table", "flatten_pdf", "images_to_pdf", "import_form_data", "list_attachments", "list_bookmarks", "list_comments", "list_fields", "list_layers", "list_links", "list_page_images", "list_page_labels", "make_fillable", "merge_pdfs", "ocr_pdf", "organize_pages", "pdf_info", "redact", "remove_hidden_info", "render_page", "search_text", "set_bookmarks", "set_layers", "set_page_labels", "split_pdf", "stamp_pages", "verify_signatures"],
                     tools.Select(t => t.Name).Order());
        Assert.True(tools.Single(t => t.Name == "pdf_info").ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tools.Single(t => t.Name == "fill_fields").ProtocolTool.Annotations!.ReadOnlyHint);

        var info = await client.CallToolAsync("pdf_info", new Dictionary<string, object?> { ["path"] = "form.pdf" });
        Assert.NotEqual(true, info.IsError);
        Assert.Contains("\"pages\": 1", ((TextContentBlock)info.Content[0]).Text);

        var page = await client.CallToolAsync("render_page", new Dictionary<string, object?> { ["path"] = "form.pdf", ["page"] = 1 });
        Assert.Contains(page.Content, c => c is ImageContentBlock { MimeType: "image/png" });

        var refused = await client.CallToolAsync("pdf_info", new Dictionary<string, object?> { ["path"] = "/etc/hosts" });
        Assert.True(refused.IsError);
    }
}
