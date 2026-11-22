// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Washi.Core;
using Washi.Core.Fonts;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Ocr;
using Washi.Core.Pages;
using Washi.Core.Pdfium;
using Washi.Masume;

namespace Washi.Tools;

public static class WashiTools
{
    /// <summary>Every PDF tool, bound to one folder policy.</summary>
    public static ToolRegistry CreateRegistry(PathPolicy policy) => new ToolRegistry()
        .Add(new PdfInfoTool(policy))
        .Add(new ExtractTextTool(policy))
        .Add(new SearchTextTool(policy))
        .Add(new RenderPageTool(policy))
        .Add(new ListFieldsTool(policy))
        .Add(new DetectFieldsTool(policy))
        .Add(new MakeFillableTool(policy))
        .Add(new FillFieldsTool(policy))
        .Add(new MergePdfsTool(policy))
        .Add(new SplitPdfTool(policy))
        .Add(new OrganizePagesTool(policy))
        .Add(new ListCommentsTool(policy))
        .Add(new AddCommentsTool(policy))
        .Add(new RemoveHiddenInfoTool(policy))
        .Add(new RedactTool(policy))
        .Add(new OcrPdfTool(policy))
        .Add(new EraseContentTool(policy));

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep 燁 readable for the model
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Shared plumbing: argument parsing, the PDFium lock, error mapping.</summary>
public abstract class PdfTool(PathPolicy policy) : IWashiTool
{
    protected PathPolicy Policy { get; } = policy;
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract ToolRisk Risk { get; }
    protected abstract string Schema { get; }
    public JsonElement InputSchema => JsonDocument.Parse(Schema).RootElement.Clone();

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        bool locked = HoldsPdfiumLock;
        if (locked) await PdfiumDocument.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(arguments.ValueKind == JsonValueKind.Object ? arguments : JsonDocument.Parse("{}").RootElement, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolException e) { return ToolResult.Error(e.Message); }
        catch (EraseException e) { return ToolResult.Error(e.Message); }
        catch (RedactException e) { return ToolResult.Error(e.Message); }
        catch (PageAssemblyException e) { return ToolResult.Error(e.Message); }
        catch (OcrException e) { return ToolResult.Error(e.Message); }
        catch (FontNotUsableException e) when (e.Reason == "license")
        {
            return ToolResult.Error($"the font's licence does not allow embedding it in an editable PDF (OS/2 fsType 0x{e.FsType:X4}); choose another font");
        }
        catch (FontNotUsableException) { return ToolResult.Error("the font cannot be embedded: use a static TrueType/OpenType font (.ttf, .otf, .ttc)"); }
        catch (PdfSharp.Pdf.IO.PdfReaderException e) when (e.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Error("the PDF is protected against changes (owner password); Washi does not remove protection");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"could not process the PDF: {e.Message}");
        }
        finally
        {
            if (locked) PdfiumDocument.Gate.Release();
        }
    }

    /// <summary>False for tools that take the PDFium lock themselves around shorter sections (long OCR runs).</summary>
    protected virtual bool HoldsPdfiumLock => true;

    protected virtual Task<ToolResult> RunAsync(JsonElement a, CancellationToken ct) => Task.FromResult(Run(a));

    protected virtual ToolResult Run(JsonElement a) => throw new NotSupportedException();

    protected static string Str(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s : throw new ToolException($"'{key}' is required");

    protected static string? OptStr(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    protected static double? OptNum(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    protected static string ToJson(object o) => JsonSerializer.Serialize(o, WashiTools.Json);

    protected OpenTypeFont? FontArg(JsonElement a) =>
        OptStr(a, "embed_font") is { } f ? OpenTypeFont.Load(Policy.Font(f), (int)(OptNum(a, "font_index") ?? 0)) : null;

    protected static object Rect(PdfRect r) => new[] { Math.Round(r.X0, 1), Math.Round(r.Y0, 1), Math.Round(r.X1, 1), Math.Round(r.Y1, 1) };
}

// ---------------------------------------------------------------------- read-only

public sealed class PdfInfoTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "pdf_info";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string Description =>
        "Overview of a PDF: page count and sizes (points, upright), rotation, encryption, number of existing form fields, " +
        "whether the text layer is readable and which writing system it uses. Start here.";
    protected override string Schema => """
        {"type":"object","properties":{"path":{"type":"string","description":"PDF file"}},"required":["path"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        using var doc = PdfiumDocument.Open(path);
        var sizes = Enumerable.Range(0, doc.PageCount).Select(i =>
        {
            var info = doc.GetPageInfo(i);
            return new { page = i + 1, width_pt = Math.Round(info.Width, 1), height_pt = Math.Round(info.Height, 1), rotation = info.Rotation };
        }).ToList();
        var text = string.Concat(Enumerable.Range(0, Math.Min(3, doc.PageCount)).Select(doc.GetPageText));
        int fields;
        try { fields = AcroFormFields.List(path).Count; } catch (Exception) { fields = -1; }
        bool readable = MasumeDetector.Readable(text);
        return new ToolResult(ToJson(new
        {
            file = path,
            pages = doc.PageCount,
            encrypted = doc.IsEncrypted,
            page_sizes = sizes,
            form_fields = fields,
            text_readable = readable,
            text_language = readable && FormScripts.Detect(text) is { } s ? FormScripts.LanguageTag(s) : null,
        }));
    }
}

public sealed class ExtractTextTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "extract_text";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string Description =>
        "Text of a PDF, page by page. Empty or garbled output means a scan or fonts without a Unicode mapping: run ocr_pdf (on-device OCR) or read render_page images instead.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "pages":{"type":"string","description":"1-based pages, e.g. \"1-3,5\". Default: all"},
          "max_chars":{"type":"integer","description":"Stop after this many characters (default 40000)","minimum":100,"maximum":200000}},
         "required":["path"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        int max = (int)Math.Clamp(OptNum(a, "max_chars") ?? 40000, 100, 200000);
        using var doc = PdfiumDocument.Open(path);
        var pages = FormPipeline.ParsePages(OptStr(a, "pages"), doc.PageCount);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < doc.PageCount && sb.Length < max; i++)
        {
            if (pages is not null && !pages.Contains(i)) continue;
            sb.Append("=== page ").Append(i + 1).Append(" ===\n").Append(doc.GetPageText(i).Trim()).Append("\n\n");
        }
        var text = sb.ToString();
        if (text.Length > max) text = text[..max] + $"\n[truncated at {max} characters]";
        if (!MasumeDetector.Readable(text)) text += "\n[note: this text looks garbled; the PDF's fonts have no Unicode mapping. Use render_page.]";
        return new ToolResult(text);
    }
}

public sealed class RenderPageTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "render_page";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string Description =>
        "Renders one page as a PNG image, form fields and their values included. Use it to read scanned or garbled pages and to check a filled form.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "page":{"type":"integer","description":"1-based page number","minimum":1},
          "dpi":{"type":"number","description":"Resolution, 36-200 (default 110)","minimum":36,"maximum":200}},
         "required":["path","page"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        int page = (int)(OptNum(a, "page") ?? throw new ToolException("'page' is required"));
        double dpi = Math.Clamp(OptNum(a, "dpi") ?? 110, 36, 200);
        using var doc = PdfiumDocument.Open(path);
        if (page < 1 || page > doc.PageCount) throw new ToolException($"page must be 1-{doc.PageCount}");
        var info = doc.GetPageInfo(page - 1);
        double scale = Math.Min(dpi / 72, 4000 / Math.Max(info.Width, info.Height));
        var img = doc.RenderPage(page - 1, scale);
        using var ms = new MemoryStream();
        PngWriter.Write(img, ms);
        return new ToolResult($"page {page} of {doc.PageCount}, {img.Width}x{img.Height} px ({Math.Round(scale * 72)} dpi)",
                              [new ToolImage("image/png", ms.ToArray())]);
    }
}

public sealed class ListFieldsTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "list_fields";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string Description =>
        "Form fields already in a PDF: full name, type (text, checkbox, radio, choice, signature, button), current value, page (1-based), " +
        "rect [x0,y0,x1,y1] in points from the top-left of the upright page, max_len/comb for boxed digits, options or checkbox states, tooltip. " +
        "Call this before fill_fields to learn the names.";
    protected override string Schema => """
        {"type":"object","properties":{"path":{"type":"string","description":"PDF file"}},"required":["path"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        var fields = AcroFormFields.List(path);
        if (fields.Count == 0)
            return new ToolResult("this PDF has no form fields; use detect_fields / make_fillable to add them");
        return new ToolResult(ToJson(fields.Select(f => new
        {
            name = f.Name,
            type = f.Type,
            value = f.Value,
            page = f.Page + 1,
            rect = Rect(f.Rect),
            max_len = f.MaxLen > 0 ? f.MaxLen : (int?)null,
            comb = f.Comb ? true : (bool?)null,
            multiline = f.Multiline ? true : (bool?)null,
            read_only = f.ReadOnly ? true : (bool?)null,
            options = f.Options.Count > 0 ? f.Options : null,
            tooltip = f.Tooltip.Length > 0 ? f.Tooltip : null,
        })));
    }
}

public sealed class DetectFieldsTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "detect_fields";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string Description =>
        "Finds where a flat (non-fillable) PDF form expects input, using Masume's table-cell detection: text, amount (right-aligned, " +
        "next to a unit like 円), comb (one character per box), checkbox, radio (○ choices on one line; buttons sharing a 'group' " +
        "form one choice, each with its 'export' option) and signature (a blank labelled 署名 / Signature) fields. Returns fields.json items " +
        "(page is 0-based there; rect in points, top-left origin). Set overlay_page to also get a colour-coded review image. " +
        "Edit the list if needed and pass it to make_fillable.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "pages":{"type":"string","description":"1-based pages, e.g. \"1-2\". Default: all"},
          "overlay_page":{"type":"integer","description":"1-based page to return as a review image","minimum":1}},
         "required":["path"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        int overlay = (int)(OptNum(a, "overlay_page") ?? 0);
        using var doc = PdfiumDocument.Open(path);
        var pages = FormPipeline.ParsePages(OptStr(a, "pages"), doc.PageCount);
        var all = new List<DetectedField>();
        var images = new List<ToolImage>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (pages is not null && !pages.Contains(i) && i != overlay - 1) continue;
            var snap = doc.Snapshot(i, MasumeDetector.Scale);
            var fields = MasumeDetector.Detect(snap, out var cells);
            if (pages is null || pages.Contains(i)) all.AddRange(fields);
            if (i == overlay - 1)
            {
                using var ms = new MemoryStream();
                PngWriter.Write(Half(MasumeOverlay.Draw(snap, fields, cells)), ms);
                images.Add(new ToolImage("image/png", ms.ToArray()));
            }
        }
        var counts = string.Join(", ", all.GroupBy(f => f.Kind.ToString().ToLowerInvariant()).Select(g => $"{g.Count()} {g.Key}"));
        int pre = all.Count(f => f.Prefilled.Length > 0);
        var head = $"{all.Count} fields ({counts}); {pre} already contain printed data." +
                   (images.Count > 0 ? " Overlay: blue text, purple amount, orange comb, green checkbox, teal radio, brown signature, red dashed = printed data." : "");
        return new ToolResult(head + "\n" + FieldsJson.Serialize(all), images);
    }

    /// <summary>2x box downscale: the 4 px/pt detection raster is larger than a model needs.</summary>
    private static RgbImage Half(RgbImage s)
    {
        int w = s.Width / 2, h = s.Height / 2;
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    int i0 = ((2 * y) * s.Width + 2 * x) * 3 + c, i1 = i0 + s.Width * 3;
                    px[(y * w + x) * 3 + c] = (byte)((s.Pixels[i0] + s.Pixels[i0 + 3] + s.Pixels[i1] + s.Pixels[i1 + 3]) / 4);
                }
        return new RgbImage(w, h, px);
    }
}

// ---------------------------------------------------------------------- writing new files

public sealed class MakeFillableTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "make_fillable";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "Writes a NEW fillable PDF from a flat one: detects the fields (or uses the given fields.json items) and adds a real AcroForm. " +
        "Rotated pages are straightened. 'lang' picks the font viewers use for typed text (auto reads the page). " +
        "Kinds: text, amount, comb, checkbox, radio (one PDF field per 'group', one button per 'export' option), choice (a drop-down " +
        "list of 'options') and signature (an empty signature field). " +
        "Set embed_font to a .ttf/.otf/.ttc to embed it whole, so rare CJK characters (e.g. 燁) show in every viewer; this adds the font's size. " +
        "Never overwrites: output defaults to <name>_fillable.pdf and must not exist.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"Flat PDF form"},
          "output":{"type":"string","description":"New PDF path (default: <name>_fillable.pdf next to the input)"},
          "lang":{"type":"string","enum":["auto","ja","zh-Hans","zh-Hant","ko","en","fr","de"],"description":"Form language (default auto)"},
          "embed_font":{"type":"string","description":"Font file to embed (allowed folders or system font folders)"},
          "font_index":{"type":"integer","description":"Face index inside a .ttc collection (default 0)","minimum":0},
          "fields":{"type":"array","description":"fields.json items from detect_fields (edited or not). Default: detect now","items":{"type":"object"}}},
         "required":["path"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        var output = Policy.Output(OptStr(a, "output"), path, "_fillable");
        var lang = OptStr(a, "lang") ?? "auto";
        if (!FormPipeline.Languages.Contains(lang, StringComparer.OrdinalIgnoreCase))
            throw new ToolException($"lang must be one of {string.Join(", ", FormPipeline.Languages)}");
        var font = FontArg(a);

        IReadOnlyList<DetectedField> fields;
        string text;
        if (a.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            try { fields = FieldsJson.Parse(f.GetRawText()); }
            catch (JsonException e) { throw new ToolException($"fields: {e.Message}"); }
            text = FormPipeline.Text(path);
        }
        else
        {
            (fields, text) = FormPipeline.Detect(path);
        }
        var (script, source) = FormPipeline.ChooseScript(lang, text, L.Culture);
        var r = AcroFormWriter.Write(path, output, fields.Select(x => x.ToFormField()).ToList(), script, font);
        return new ToolResult(ToJson(new
        {
            output,
            fields_written = r.FieldsWritten,
            pages_straightened = r.PagesStraightened,
            form_language = FormScripts.LanguageTag(script),
            language_source = source.ToString(),
            embedded_font = font?.PostScriptName,
            size_mb = Math.Round(new FileInfo(output).Length / 1048576.0, 2),
            warnings = r.Warnings,
        }));
    }
}

public sealed class FillFieldsTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "fill_fields";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "Writes a NEW PDF with form fields filled in. 'values' maps full field names (from list_fields) to values: text as strings, " +
        "checkboxes as true/false, radio groups and choices as one of their options. Washi draws each value itself, so it looks the " +
        "same in every viewer. Set embed_font to embed a font for characters the form's font lacks. " +
        "Never overwrites: output defaults to <name>_filled.pdf and must not exist. Check the result with render_page.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"Fillable PDF"},
          "values":{"type":"object","description":"field name -> value","additionalProperties":{"type":["string","boolean","number"]}},
          "output":{"type":"string","description":"New PDF path (default: <name>_filled.pdf next to the input)"},
          "embed_font":{"type":"string","description":"Font file to embed and use for the filled text"},
          "font_index":{"type":"integer","description":"Face index inside a .ttc collection (default 0)","minimum":0}},
         "required":["path","values"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        if (!a.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Object)
            throw new ToolException("'values' must be an object of field name -> value");
        var values = new Dictionary<string, string>();
        foreach (var prop in v.EnumerateObject())
            values[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString()!,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => prop.Value.GetRawText(),
                _ => throw new ToolException($"value of '{prop.Name}' must be a string, boolean or number"),
            };
        var output = Policy.Output(OptStr(a, "output"), path, "_filled");
        var r = AcroFormFields.Fill(path, output, values, FontArg(a));
        return new ToolResult(ToJson(new
        {
            output,
            filled = r.Filled,
            unknown_names = r.UnknownNames,
            notes = r.Notes,
            hint = r.UnknownNames.Count > 0 ? "call list_fields for the exact names" : null,
        }), IsError: r.Filled == 0 && values.Count > 0);
    }
}

// ---------------------------------------------------------------------- pages and OCR
public sealed class MergePdfsTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "merge_pdfs";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "Writes a NEW PDF with all pages of the given PDFs in order. Form fields are kept and stay fillable; fields whose names " +
        "would clash get a suffix (_2, _3). Files whose permissions forbid reusing pages are refused. " +
        "Never overwrites: output defaults to <first name>_merged.pdf and must not exist.";
    protected override string Schema => """
        {"type":"object","properties":{
          "paths":{"type":"array","items":{"type":"string"},"minItems":2,"description":"PDF files, in order"},
          "output":{"type":"string","description":"New PDF path (default: <first name>_merged.pdf next to the first input)"}},
         "required":["paths"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        if (!a.TryGetProperty("paths", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 2)
            throw new ToolException("'paths' must list at least two PDF files");
        var inputs = arr.EnumerateArray().Select(x => Policy.Input(x.GetString() ?? "")).ToList();
        var output = Policy.Output(OptStr(a, "output"), inputs[0], "_merged");
        int pages = PageAssembly.Merge(inputs, output);
        return new ToolResult(ToJson(new { output, pages, form_fields = AcroFormFields.List(output).Count }));
    }
}

public sealed class SplitPdfTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "split_pdf";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "Writes NEW PDFs, one per page range (\"1-3,5\" makes two files; \"each\" one file per page), named <name>_p1-3.pdf etc. " +
        "in output_dir (default <name>_split next to the input; created if missing). Form fields on the copied pages are kept. " +
        "Never overwrites existing files.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "pages":{"type":"string","description":"Ranges, e.g. \"1-3,5\", or \"each\" (default)"},
          "output_dir":{"type":"string","description":"Folder for the parts (default <name>_split next to the input)"}},
         "required":["path"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        var dir = Policy.OutputFolder(OptStr(a, "output_dir"), path, "_split");
        var ranges = PageAssembly.ParseRanges(OptStr(a, "pages"), PageAssembly.PageCount(path));
        var parts = PageAssembly.Split(path, ranges, dir);
        return new ToolResult(ToJson(new
        {
            output_dir = dir,
            files = parts.Select(x => new { path = x.Path, first_page = x.FirstPage, last_page = x.LastPage }),
        }));
    }
}

public sealed class OrganizePagesTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "organize_pages";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "Writes a NEW PDF with exactly the listed pages, in the listed order: reorder, delete (leave a page out), rotate, and " +
        "insert pages from other PDFs in one call. Each entry is {page (1-based), path (optional, default: the main path), " +
        "rotate (optional, degrees clockwise, a multiple of 90, added to the page's own rotation)}. A page may appear only once. " +
        "Form fields on the pages are kept and stay fillable. Never overwrites: output defaults to <name>_organized.pdf.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"The main PDF file"},
          "pages":{"type":"array","minItems":1,"description":"The pages of the new file, in order","items":{"type":"object","properties":{
            "page":{"type":"integer","minimum":1},
            "path":{"type":"string","description":"Another PDF to take this page from (default: the main path)"},
            "rotate":{"type":"integer","description":"Extra clockwise rotation: 90, 180, 270 or -90"}},
            "required":["page"],"additionalProperties":false}},
          "output":{"type":"string","description":"New PDF path (default: <name>_organized.pdf next to the input)"}},
         "required":["path","pages"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        if (!a.TryGetProperty("pages", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            throw new ToolException("'pages' must list at least one page");
        var picks = new List<PagePick>();
        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty("page", out var pg) || !pg.TryGetInt32(out var page) || page < 1)
                throw new ToolException("each entry needs a 1-based 'page'");
            var src = e.TryGetProperty("path", out var ps) && ps.GetString() is { Length: > 0 } other ? Policy.Input(other) : path;
            int rot = e.TryGetProperty("rotate", out var r) && r.TryGetInt32(out var deg) ? deg : 0;
            picks.Add(new PagePick(src, page, rot));
        }
        var output = Policy.Output(OptStr(a, "output"), path, "_organized");
        int pages = PageAssembly.Organize(picks, output);
        return new ToolResult(ToJson(new { output, pages, form_fields = AcroFormFields.List(output).Count }));
    }
}

public sealed class RemoveHiddenInfoTool(PathPolicy p) : PdfTool(p)
{
    private static readonly Dictionary<string, HiddenInfo> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["metadata"] = HiddenInfo.Metadata, ["attachments"] = HiddenInfo.Attachments, ["scripts"] = HiddenInfo.Scripts,
        ["comments"] = HiddenInfo.Comments, ["private_data"] = HiddenInfo.PrivateData,
    };

    public override string Name => "remove_hidden_info";
    public override ToolRisk Risk => ToolRisk.Write;
    protected override bool HoldsPdfiumLock => false;
    public override string Description =>
        "Finds and removes information a PDF carries besides its pages: metadata (title, author, dates, the program that made it, " +
        "XMP), attachments, scripts and actions (JavaScript, launching programs, submitting data, XFA), comments, private " +
        "application data and thumbnails, and earlier versions kept by incremental saves (always removed). With dry_run it only " +
        "lists what is there. Pages, form fields and links stay. Password-protected files are refused. Never overwrites: output " +
        "defaults to <name>_cleaned.pdf.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "remove":{"type":"array","items":{"type":"string","enum":["metadata","attachments","scripts","comments","private_data"]},
                    "description":"What to remove (default: all of it)"},
          "dry_run":{"type":"boolean","description":"Only list what the file carries; write nothing"},
          "output":{"type":"string","description":"New PDF path (default: <name>_cleaned.pdf next to the input)"}},
         "required":["path"],"additionalProperties":false}
        """;

    private static object Describe(IEnumerable<HiddenItem> items) => items.Select(i => new
    {
        kind = Kinds.FirstOrDefault(k => k.Value == i.Kind).Key ?? "earlier_versions", count = i.Count, details = i.Details,
    });

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        if (a.TryGetProperty("dry_run", out var dry) && dry.ValueKind == JsonValueKind.True)
            return new ToolResult(ToJson(new { found = Describe(PdfCleaner.Inspect(path)) }));
        var remove = new HashSet<HiddenInfo> { HiddenInfo.EarlierVersions };
        if (a.TryGetProperty("remove", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
                remove.Add(e.GetString() is { } k && Kinds.TryGetValue(k, out var kind) ? kind : throw new ToolException($"unknown kind '{e}'"));
        }
        else remove.UnionWith(Kinds.Values);
        var output = Policy.Output(OptStr(a, "output"), path, "_cleaned");
        var removed = PdfCleaner.Clean(path, output, remove);
        return new ToolResult(ToJson(new { output, removed = Describe(removed), left = Describe(PdfCleaner.Inspect(output)) }));
    }
}

public sealed class SearchTextTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "search_text";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string Description =>
        "Finds text in a PDF's text layer: each hit's page (1-based), the text, the words around it, and its boxes " +
        "[x0,y0,x1,y1] in points from the top-left of the upright page (one per line it spans). Case, full-width and " +
        "half-width forms and spaces do not matter. Scanned pages need ocr_pdf first.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "text":{"type":"string","description":"What to find"},
          "match_case":{"type":"boolean"},
          "whole_words":{"type":"boolean","description":"A hit must not be part of a longer word"},
          "max":{"type":"integer","description":"At most this many hits (default 200)"}},
         "required":["path","text"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        int max = (int)Math.Clamp(OptNum(a, "max") ?? 200, 1, 2000);
        var hits = PdfSearch.Find(path, Str(a, "text"),
                                  a.TryGetProperty("match_case", out var mc) && mc.ValueKind == JsonValueKind.True,
                                  a.TryGetProperty("whole_words", out var ww) && ww.ValueKind == JsonValueKind.True, max + 1);
        return new ToolResult(ToJson(new
        {
            count = Math.Min(hits.Count, max), more = hits.Count > max,
            hits = hits.Take(max).Select(h => new
            {
                page = h.Page + 1, text = h.Text, context = h.Context,
                boxes = h.Boxes.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }.Select(v => Math.Round(v, 1))),
            }),
        }));
    }
}

public sealed class RedactTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "redact";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "Redaction: removes words, kinds of personal data or areas from a PDF for real and covers them with black boxes. Text " +
        "under a box is taken out glyph by glyph (the rest of the line stays in place), pixels of images under it are blanked, " +
        "drawings inside it dropped, and comments and form fields there removed; the same words are masked with ■ in document " +
        "properties, bookmarks and tags. The copy is checked before it is kept (the words are searched for again, no character " +
        "is left under a box, nothing changed outside the boxes). With dry_run it only lists the matches. Scanned pages need " +
        "ocr_pdf first so their text can be found. Never overwrites: output defaults to <name>_redacted.pdf.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "find":{"type":"array","items":{"type":"string"},"description":"Words or phrases (case, full/half width and spaces do not matter)"},
          "patterns":{"type":"array","items":{"type":"string","enum":["email","phone","number"]},
                      "description":"Kinds of data: e-mail addresses, phone numbers, long numbers (7+ digits: ID, account, card)"},
          "regex":{"type":"array","items":{"type":"string"},"description":".NET regular expressions over the page text"},
          "areas":{"type":"array","items":{"type":"object","properties":{
                     "page":{"type":"integer","description":"1-based"},
                     "rect":{"type":"array","items":{"type":"number"},"minItems":4,"maxItems":4,
                             "description":"[x0,y0,x1,y1] in points from the top-left of the upright page"}},
                   "required":["page","rect"],"additionalProperties":false},
                   "description":"Areas to black out besides the matches (a photo, a signature)"},
          "match_case":{"type":"boolean"},
          "whole_words":{"type":"boolean","description":"A word must not be part of a longer word"},
          "dry_run":{"type":"boolean","description":"Only list the matches; write nothing"},
          "output":{"type":"string","description":"New PDF path (default: <name>_redacted.pdf next to the input)"}},
         "required":["path"],"additionalProperties":false}
        """;

    private static List<string> Strings(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        var patterns = Strings(a, "patterns").Select(p => p.ToLowerInvariant() switch
        {
            "email" => RedactPattern.Email,
            "phone" => RedactPattern.Phone,
            "number" => RedactPattern.Number,
            _ => throw new ToolException($"unknown pattern '{p}': use email, phone or number"),
        }).ToList();
        var search = new RedactSearch(Strings(a, "find"), patterns, Strings(a, "regex"),
                                      a.TryGetProperty("match_case", out var mc) && mc.ValueKind == JsonValueKind.True,
                                      a.TryGetProperty("whole_words", out var ww) && ww.ValueKind == JsonValueKind.True);
        var found = PdfRedactor.Find(path, search);
        var matches = found.Select(m => new
        {
            page = m.Page + 1, source = m.Source.ToString().ToLowerInvariant(), text = m.Text, context = m.Context,
            boxes = m.Boxes.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }.Select(v => Math.Round(v, 1))),
        }).ToList();
        if (a.TryGetProperty("dry_run", out var dry) && dry.ValueKind == JsonValueKind.True)
            return new ToolResult(ToJson(new { matches }));

        var areas = PdfRedactor.Areas(found).ToList();
        if (a.TryGetProperty("areas", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
            {
                if (!e.TryGetProperty("page", out var pg) || !pg.TryGetInt32(out var page) || !e.TryGetProperty("rect", out var r)
                    || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4)
                    throw new ToolException("each area needs a page (1-based) and a rect [x0,y0,x1,y1]");
                var v = r.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                areas.Add(new RedactArea(page - 1, new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]))));
            }
        if (areas.Count == 0) return new ToolResult(ToJson(new { matches, output = (string?)null, note = "nothing found: nothing was written" }));
        var output = Policy.Output(OptStr(a, "output"), path, "_redacted");
        var report = PdfRedactor.Redact(path, output, areas, search.IsEmpty ? null : search);
        return new ToolResult(ToJson(new
        {
            output, matches = matches.Count, removed = new
            {
                characters = report.Glyphs, image_areas = report.Images, drawings = report.Paths,
                annotations_and_fields = report.Annotations, other_texts = report.Strings,
            },
            verified = "searched again: nothing found; no character left under a box; nothing changed outside the boxes",
        }));
    }
}

public sealed class ListCommentsTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "list_comments";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    protected override bool HoldsPdfiumLock => false;
    public override string Description =>
        "Review comments (annotations) already in a PDF: type (Highlight, Text = sticky note, FreeText, Ink, Square ...), page " +
        "(1-based), rect [x0,y0,x1,y1] in points from the top-left of the upright page, text, author, modified date. Form fields and links are not listed.";
    protected override string Schema => """
        {"type":"object","properties":{"path":{"type":"string","description":"PDF file"}},"required":["path"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        return new ToolResult(ToJson(PdfComments.List(path).Select(c => new
        {
            type = c.Type, page = c.Page + 1, rect = new[] { c.Rect.X0, c.Rect.Y0, c.Rect.X1, c.Rect.Y1 }.Select(v => Math.Round(v, 1)),
            text = c.Text, author = c.Author, modified = c.Modified,
        })));
    }
}

public sealed class AddCommentsTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "add_comments";
    public override ToolRisk Risk => ToolRisk.Write;
    protected override bool HoldsPdfiumLock => false;
    public override string Description =>
        "Writes a NEW PDF with review comments added as standard annotations that every viewer shows. Each comment: page (1-based), " +
        "kind (highlight, underline, strike_out, note, text_box, pen, box), rect [x0,y0,x1,y1] in points from the top-left of the " +
        "upright page (as extract_text / detect_fields report), text (for note and text_box), optional color \"#RRGGBB\", optional " +
        "lines (one rect per marked line, for highlight/underline/strike_out; default: rect), optional strokes ([[[x,y],...],...] for pen), " +
        "optional author. Form fields are kept. Never overwrites: output defaults to <name>_commented.pdf.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "comments":{"type":"array","minItems":1,"items":{"type":"object","properties":{
            "page":{"type":"integer","minimum":1},
            "kind":{"type":"string","enum":["highlight","underline","strike_out","note","text_box","pen","box"]},
            "rect":{"type":"array","items":{"type":"number"},"minItems":4,"maxItems":4},
            "text":{"type":"string"},
            "color":{"type":"string"},
            "lines":{"type":"array","items":{"type":"array","items":{"type":"number"},"minItems":4,"maxItems":4}},
            "strokes":{"type":"array","items":{"type":"array","items":{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":2}}},
            "author":{"type":"string"}},
            "required":["page","kind","rect"],"additionalProperties":false}},
          "output":{"type":"string","description":"New PDF path (default: <name>_commented.pdf next to the input)"}},
         "required":["path","comments"],"additionalProperties":false}
        """;

    private static PdfRect Rect(JsonElement e)
    {
        var v = e.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (v.Length != 4) throw new ToolException("a rect is [x0, y0, x1, y1]");
        return new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        if (!a.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            throw new ToolException("'comments' must list at least one comment");
        var specs = new List<CommentSpec>();
        foreach (var c in arr.EnumerateArray())
        {
            if (!c.TryGetProperty("page", out var pg) || !pg.TryGetInt32(out var page) || page < 1) throw new ToolException("each comment needs a 1-based 'page'");
            var kind = (c.TryGetProperty("kind", out var k) ? k.GetString() : null) switch
            {
                "highlight" => CommentKind.Highlight, "underline" => CommentKind.Underline, "strike_out" => CommentKind.StrikeOut,
                "note" => CommentKind.Note, "text_box" => CommentKind.TextBox, "pen" => CommentKind.Pen, "box" => CommentKind.Box,
                var other => throw new ToolException($"unknown kind '{other}'"),
            };
            if (!c.TryGetProperty("rect", out var re)) throw new ToolException("each comment needs a 'rect'");
            var rect = Rect(re);
            string text = c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (kind is CommentKind.Note or CommentKind.TextBox && text.Trim().Length == 0) throw new ToolException($"a {kind} needs 'text'");
            IReadOnlyList<PdfRect>? lines = c.TryGetProperty("lines", out var ls) ? [.. ls.EnumerateArray().Select(Rect)] : null;
            IReadOnlyList<IReadOnlyList<(double, double)>>? strokes = c.TryGetProperty("strokes", out var st)
                ? [.. st.EnumerateArray().Select(s => (IReadOnlyList<(double, double)>)[.. s.EnumerateArray().Select(pt => (pt[0].GetDouble(), pt[1].GetDouble()))])]
                : null;
            if (kind == CommentKind.Pen && strokes is not { Count: > 0 }) throw new ToolException("a pen comment needs 'strokes'");
            specs.Add(new CommentSpec(page - 1, kind, rect, text, c.TryGetProperty("color", out var col) ? col.GetString() ?? "" : "",
                                      lines, strokes, c.TryGetProperty("author", out var au) ? au.GetString() ?? "" : ""));
        }
        var output = Policy.Output(OptStr(a, "output"), path, "_commented");
        int n = PdfComments.Add(path, output, specs);
        return new ToolResult(ToJson(new { output, comments = n }));
    }
}

public sealed class OcrPdfTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "ocr_pdf";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "On-device OCR (Apple Vision on macOS, else Tesseract): recognises pages that have no readable text and writes a NEW " +
        "searchable PDF (an invisible text layer; the page image is unchanged). Returns the recognised text per page. " +
        "Pages that already have text are skipped unless force is true. Nothing leaves the machine. " +
        "Never overwrites: output defaults to <name>_ocr.pdf and must not exist. Takes a few seconds per page.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file (a scan, or a PDF whose text is garbled)"},
          "output":{"type":"string","description":"New PDF path (default: <name>_ocr.pdf next to the input)"},
          "engine":{"type":"string","enum":["auto","vision","tesseract"],"description":"OCR engine (default auto)"},
          "lang":{"type":"string","description":"Languages, comma-separated: ja, zh-Hans, zh-Hant, ko, en, fr, de (default: the engine detects)"},
          "pages":{"type":"string","description":"1-based pages, e.g. \"1-3,5\". Default: all"},
          "force":{"type":"boolean","description":"Also recognise pages that already have text (default false)"}},
         "required":["path"],"additionalProperties":false}
        """;

    // OCR takes seconds per page: PDFium is locked only while pages are rendered, not while they are read.
    protected override bool HoldsPdfiumLock => false;

    protected override async Task<ToolResult> RunAsync(JsonElement a, CancellationToken ct)
    {
        var path = Policy.Input(Str(a, "path"));
        var output = Policy.Output(OptStr(a, "output"), path, "_ocr");
        var options = new OcrOptions
        {
            Languages = OcrLanguages.Parse(OptStr(a, "lang")),
            Force = a.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True,
            Pages = OptStr(a, "pages") is { Length: > 0 } spec
                ? PageAssembly.ParseRanges(spec, PageAssembly.PageCount(path)).SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1)).ToHashSet()
                : null,
        };
        var engine = OcrEngines.Create(OptStr(a, "engine") ?? "auto");
        var r = await OcrPipeline.RunAsync(path, output, engine, options, ct: ct);
        const int MaxChars = 30000;
        int budget = MaxChars;
        return new ToolResult(ToJson(new
        {
            output = r.Output,
            engine = r.Engine,
            pages_recognized = r.Recognized,
            missing_languages = engine is TesseractOcr { Missing.Count: > 0 } t ? t.Missing : null,
            pages = r.Pages.Where(x => x.Status != OcrPageStatus.NotSelected).Select(x =>
            {
                var text = x.Text;
                if (text.Length > budget) text = text[..Math.Max(0, budget)] + "…(truncated; use extract_text on the output)";
                budget -= text.Length;
                return new { page = x.Page, status = Snake(x.Status), text = x.Status == OcrPageStatus.Recognized ? text : null };
            }),
            note = r.Output is null ? "no page needed OCR, so no file was written (set force to recognise pages that have text)" : null,
        }));
    }

    private static string Snake(OcrPageStatus s) => s switch
    {
        OcrPageStatus.Recognized => "recognized",
        OcrPageStatus.HasText => "has_text",
        OcrPageStatus.AlreadySearchable => "already_searchable",
        _ => "not_selected",
    };
}

public sealed class EraseContentTool(PathPolicy p) : PdfTool(p)
{
    public override string Name => "erase_content";
    public override ToolRisk Risk => ToolRisk.Write;
    public override string Description =>
        "Writes a NEW PDF with the printed content inside boxes removed for real (deleted from the page, not covered): " +
        "text, images and drawings, e.g. wrong data already printed on a form before filling it again. Boxes use the same " +
        "coordinates as list_fields / detect_fields (points, top-left origin, upright page); a field's rect is a good box. " +
        "By default coloured printing (the form itself) and thin table lines stay. A text line that runs outside a box is " +
        "kept and reported. The result is checked: no text left where it was erased, no pixel changed outside the boxes. " +
        "Never overwrites; render_page the result to check.";
    protected override string Schema => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"PDF file"},
          "boxes":{"type":"array","minItems":1,"items":{"type":"object","properties":{
              "page":{"type":"integer","minimum":1,"description":"1-based page"},
              "rect":{"type":"array","items":{"type":"number"},"minItems":4,"maxItems":4,"description":"[x0, y0, x1, y1] in points"}},
            "required":["page","rect"],"additionalProperties":false}},
          "keep_colored":{"type":"boolean","description":"Keep coloured ink (the form's printing); erase only black/grey (default true)"},
          "rules":{"type":"boolean","description":"Also erase thin table lines and comb dividers (default false)"},
          "output":{"type":"string","description":"New PDF path (default: <name>_erased.pdf next to the input)"}},
         "required":["path","boxes"],"additionalProperties":false}
        """;

    protected override ToolResult Run(JsonElement a)
    {
        var path = Policy.Input(Str(a, "path"));
        var output = Policy.Output(OptStr(a, "output"), path, "_erased");
        if (!a.TryGetProperty("boxes", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            throw new ToolException("'boxes' is required: [{\"page\":1,\"rect\":[x0,y0,x1,y1]}]");
        var regions = new List<EraseRegion>();
        foreach (var b in arr.EnumerateArray())
        {
            if (!b.TryGetProperty("page", out var pg) || !pg.TryGetInt32(out var page) || page < 1) throw new ToolException("each box needs a 1-based 'page'");
            if (!b.TryGetProperty("rect", out var r) || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4)
                throw new ToolException("each box needs 'rect': [x0, y0, x1, y1]");
            var v = r.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            regions.Add(new EraseRegion(page - 1, new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]))));
        }
        bool Flag(string k, bool d) => a.TryGetProperty(k, out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean() : d;
        var report = PdfEraser.Erase(path, output, regions, new EraseOptions(KeepColored: Flag("keep_colored", true), Rules: Flag("rules", false)));
        return new ToolResult(ToJson(new
        {
            output,
            removed = new { text = report.TextRemoved, images = report.ImagesRemoved, drawings = report.DrawingsRemoved },
            image_streams_dropped = report.ImagesDropped,
            check = new { characters_left = report.RemainingChars, pixels_changed_outside = report.ChangedPixelsOutside },
            kept = report.Kept.Select(k => new { page = k.Page + 1, kind = k.Kind.ToString().ToLowerInvariant(), rect = Rect(k.Rect), reason = k.Reason }),
        }));
    }
}
