// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pdfium;
using Washi.Masume;

namespace Washi.Ai;

/// <summary>What a request will send, shown to the user before anything leaves the machine.</summary>
public sealed record AiPayload(string Model, bool OffDevice, int PageImages, int Fields, int ProfileLines, int MaskedNumbers, int TextChars)
{
    public string Summary()
    {
        var parts = new List<string>();
        if (PageImages > 0) parts.Add($"{PageImages} page image(s)");
        if (Fields > 0) parts.Add($"{Fields} field description(s)");
        if (ProfileLines > 0) parts.Add($"a profile of {ProfileLines} line(s) with {MaskedNumbers} long number(s) masked");
        if (TextChars > 0) parts.Add($"{TextChars} characters of page text");
        return string.Join(", ", parts);
    }
}

public sealed record NamingResult(IReadOnlyList<DetectedField> Fields, int Renamed, int KindsChanged);

public sealed record FillPlan(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Notes, IReadOnlySet<string> SecretFields);

/// <summary>
/// The built-in AI features. Each one gathers what the model needs from the PDF, asks once
/// (per page or chunk), checks the answer against the document, and returns plain data the
/// caller writes with the existing tools.
/// </summary>
public static class AiFeatures
{
    public const int MaxImagePages = 6;
    private const int FieldsPerRequest = 120;
    private const double ImageScale = 1.6;   // ~1350 x 950 px for A4 landscape

    // ------------------------------------------------------------------ naming
    private const string NamingSystem = """
        You label the input fields of a paper form so software can fill it.
        You get one page image and a list of boxes. Box coordinates are in a 0-1000 grid over the
        page image (x to the right, y down). For every box:
        - name: what goes in it, from the printed label that describes it (usually the cell to the
          left or above, or small text inside the cell). Short, in the form's own language, unique on
          the page, no dots. Repeated rows get a suffix (e.g. 氏名_1, 氏名_2).
        - kind: text; amount (a sum of money, often next to 円/$/€); comb (one digit or character per
          printed box, e.g. a 12-box マイナンバー); checkbox (a small square to tick).
        - max_len: number of boxes for comb, else 0.
        Answer for every id you were given and nothing else.
        """;

    private static readonly JsonElement NamingSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["fields"],"properties":{
          "fields":{"type":"array","items":{"type":"object","additionalProperties":false,
            "required":["id","name","kind","max_len"],"properties":{
              "id":{"type":"integer"},"name":{"type":"string"},
              "kind":{"type":"string","enum":["text","amount","comb","checkbox"]},
              "max_len":{"type":"integer"}}}}}}
        """).RootElement.Clone();

    public static AiPayload DescribeNaming(IAiModel model, IReadOnlyList<DetectedField> fields) =>
        new(model.Describe, model.SendsDataOffDevice, model.AcceptsImages ? fields.Select(f => f.Page).Distinct().Count() : 0,
            fields.Count, 0, 0, 0);

    public static async Task<NamingResult> NameFieldsAsync(IAiModel model, string pdf, IReadOnlyList<DetectedField> fields,
                                                           CancellationToken ct = default)
    {
        var result = fields.ToArray();
        int renamed = 0, kinds = 0;
        foreach (var page in fields.Select((f, i) => (f, i)).GroupBy(t => t.f.Page))
        {
            var (image, w, h, text) = await RenderAsync(pdf, page.Key, model.AcceptsImages);
            foreach (var chunk in page.Chunk(FieldsPerRequest))
            {
                var boxes = chunk.Select(t => new
                {
                    id = t.i,
                    kind = t.f.Kind.ToString().ToLowerInvariant(),
                    box = Norm(t.f.Rect, w, h),
                    printed_label = t.f.Label.Length > 0 ? t.f.Label : null,
                }).ToList();
                var prompt = $"Page {page.Key + 1}. Boxes:\n{JsonSerializer.Serialize(boxes, JsonOpts)}" +
                             (model.AcceptsImages ? "" : $"\n\nThe page image is not available. Page text:\n{text}");
                var answer = await model.CompleteAsync(new AiRequest(NamingSystem, prompt, image is null ? [] : [image], NamingSchema), ct);
                foreach (var item in Parse(answer).GetProperty("fields").EnumerateArray())
                {
                    int id = item.GetProperty("id").GetInt32();
                    if (id < 0 || id >= result.Length || result[id].Page != page.Key) continue;   // ignore made-up ids
                    var name = Clean(item.GetProperty("name").GetString());
                    var kind = item.GetProperty("kind").GetString() switch
                    {
                        "amount" => FieldKind.Amount, "comb" => FieldKind.Comb, "checkbox" => FieldKind.Checkbox, _ => FieldKind.Text,
                    };
                    int maxLen = item.TryGetProperty("max_len", out var ml) && ml.TryGetInt32(out var m) ? m : 0;
                    var f = result[id];
                    // the model picks among text, amount, comb and checkbox only: radio, drop-down and signature stay
                    if (f.Kind is FieldKind.Radio or FieldKind.Choice or FieldKind.Signature) kind = f.Kind;
                    if (kind != f.Kind) kinds++;
                    if (name.Length > 0 && name != f.Name) renamed++;
                    result[id] = f with
                    {
                        Name = name.Length > 0 ? name : f.Name,
                        Kind = kind,
                        MaxLen = kind == FieldKind.Comb ? Math.Clamp(maxLen > 0 ? maxLen : f.MaxLen, 1, 64) : 0,
                        Align = kind switch { FieldKind.Amount => 2, FieldKind.Comb => 1, _ => f.Kind == FieldKind.Amount ? 0 : f.Align },
                    };
                }
            }
        }
        return new NamingResult(Unique(result), renamed, kinds);
    }

    // ------------------------------------------------------------------ filling
    private const string FillSystem = """
        You fill in a form for the person described in the profile.
        You get the form's fields (name, type, page, position in a 0-1000 grid over the page image,
        box count for comb fields, choices) and page images. Rules:
        - Fill a field only when the profile clearly holds its answer. Never invent data.
        - Write values the way the form asks: e.g. Japanese era years (令和/平成) when the form prints
          them, katakana for フリガナ fields, digits only for comb fields.
        - Checkboxes: "true" or "false". Choice and radio fields: one of their options, exactly.
        - Long numbers in the profile are hidden as {{SECRET_n}} placeholders; use the placeholder
          itself as the value where that number belongs (it is replaced locally afterwards).
        - Use notes for anything the person should check or that you could not fill.
        """;

    private static readonly JsonElement FillSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["values","notes"],"properties":{
          "values":{"type":"array","items":{"type":"object","additionalProperties":false,
            "required":["name","value"],"properties":{"name":{"type":"string"},"value":{"type":"string"}}}},
          "notes":{"type":"array","items":{"type":"string"}}}}
        """).RootElement.Clone();

    public static AiPayload DescribeFill(IAiModel model, IReadOnlyList<FieldInfo> fields, string profile)
    {
        var masker = new SecretMasker();
        masker.Mask(profile);
        int pages = model.AcceptsImages ? Math.Min(MaxImagePages, fields.Where(f => f.Page >= 0).Select(f => f.Page).Distinct().Count()) : 0;
        return new(model.Describe, model.SendsDataOffDevice, pages, fields.Count,
                   profile.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, masker.Count, 0);
    }

    public static async Task<FillPlan> PlanFillAsync(IAiModel model, string pdf, IReadOnlyList<FieldInfo> fields, string profile,
                                                     CancellationToken ct = default)
    {
        var masker = new SecretMasker();
        var masked = masker.Mask(profile);
        var writable = fields.Where(f => !f.ReadOnly && f.Type is "text" or "checkbox" or "radio" or "choice").ToList();

        var images = new List<AiImage>();
        var sizes = new Dictionary<int, (double W, double H)>();
        foreach (var p in writable.Where(f => f.Page >= 0).Select(f => f.Page).Distinct().Order())
        {
            var (img, w, h, _) = await RenderAsync(pdf, p, model.AcceptsImages && images.Count < MaxImagePages);
            sizes[p] = (w, h);
            if (img is not null) images.Add(img);
        }
        var described = writable.Select(f => new
        {
            name = f.Name,
            type = f.Comb ? "comb" : f.Type,
            page = f.Page + 1,
            box = f.Page >= 0 && sizes.TryGetValue(f.Page, out var s) ? Norm(f.Rect, s.W, s.H) : null,
            boxes = f.Comb ? f.MaxLen : (int?)null,
            options = f.Options.Count > 0 ? f.Options : null,
            tooltip = f.Tooltip.Length > 0 ? f.Tooltip : null,
        });
        var prompt = "Profile:\n" + masked +
                     (masker.Count > 0 ? "\n\nHidden numbers:\n" + string.Join("\n", masker.Hints) : "") +
                     "\n\nFields:\n" + JsonSerializer.Serialize(described, JsonOpts);
        var answer = Parse(await model.CompleteAsync(new AiRequest(FillSystem, prompt, images, FillSchema), ct));

        var byName = writable.ToDictionary(f => f.Name);
        var values = new Dictionary<string, string>();
        var notes = answer.GetProperty("notes").EnumerateArray().Select(n => masker.Unmask(n.GetString() ?? "")).ToList();
        var secretFields = new HashSet<string>();
        foreach (var v in answer.GetProperty("values").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString() ?? "";
            var value = v.GetProperty("value").GetString() ?? "";
            if (!byName.TryGetValue(name, out var field)) { notes.Add($"ignored unknown field '{name}'"); continue; }
            if (masker.HasPlaceholder(value)) secretFields.Add(name);
            value = masker.Unmask(value, digitsOnly: field.Comb);
            if (field.Options.Count > 0 && field.Type is "choice" or "radio" && !field.Options.Contains(value))
            {
                notes.Add($"{name}: '{value}' is not one of its options, skipped");
                continue;
            }
            values[name] = value;
        }
        return new FillPlan(values, notes, secretFields);
    }

    // ------------------------------------------------------------------ asking
    private const string AskSystem = """
        You answer questions about a PDF document the user gives you, from its text or page images.
        Quote the relevant part when it helps, say which page it is on, and say so plainly when the
        document does not answer the question. Answer in the language of the question.
        """;

    public static (string Text, int Pages, bool Readable) ReadDocument(string pdf, int maxChars = 60000)
    {
        PdfiumDocument.Gate.Wait();
        try
        {
            using var doc = PdfiumDocument.Open(pdf);
            var sb = new System.Text.StringBuilder();
            var content = new System.Text.StringBuilder();
            for (int i = 0; i < doc.PageCount && sb.Length < maxChars; i++)
            {
                var page = doc.GetPageText(i).Trim();
                content.Append(page);
                sb.Append("=== page ").Append(i + 1).Append(" ===\n").Append(page).Append("\n\n");
            }
            var text = sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
            // judge the pages' own text, not the headers we added (an empty page is not "readable")
            return (text, doc.PageCount, MasumeDetector.Readable(content.ToString()));
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    public static AiPayload DescribeAsk(IAiModel model, string pdf)
    {
        var (text, pages, readable) = ReadDocument(pdf);
        return readable || !model.AcceptsImages
            ? new(model.Describe, model.SendsDataOffDevice, 0, 0, 0, 0, text.Length)
            : new(model.Describe, model.SendsDataOffDevice, Math.Min(pages, MaxImagePages), 0, 0, 0, 0);
    }

    public static async Task<string> AskAsync(IAiModel model, string pdf, string question, CancellationToken ct = default)
    {
        var (text, pages, readable) = ReadDocument(pdf);
        var images = new List<AiImage>();
        if (!readable && model.AcceptsImages)
            for (int p = 0; p < Math.Min(pages, MaxImagePages); p++)
                if ((await RenderAsync(pdf, p, true)).Image is { } img) images.Add(img);
        var prompt = (images.Count > 0 ? "The document's text layer is unreadable; read the page images.\n\n" : "Document text:\n" + text + "\n\n")
                     + "Question: " + question;
        return (await model.CompleteAsync(new AiRequest(AskSystem, prompt, images), ct)).Trim();
    }

    // ------------------------------------------------------------------ helpers
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static int[] Norm(PdfRect r, double w, double h) =>
        [(int)(r.X0 / w * 1000), (int)(r.Y0 / h * 1000), (int)Math.Ceiling(r.X1 / w * 1000), (int)Math.Ceiling(r.Y1 / h * 1000)];

    private static async Task<(AiImage? Image, double W, double H, string Text)> RenderAsync(string pdf, int page, bool withImage)
    {
        await PdfiumDocument.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var doc = PdfiumDocument.Open(pdf);
                var info = doc.GetPageInfo(page);
                AiImage? image = null;
                if (withImage)
                {
                    using var ms = new MemoryStream();
                    PngWriter.Write(doc.RenderPage(page, ImageScale), ms);
                    image = new AiImage("image/png", ms.ToArray());
                }
                return (image, info.Width, info.Height, withImage ? "" : doc.GetPageText(page));
            }).ConfigureAwait(false);
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static JsonElement Parse(string answer)
    {
        var s = answer.Trim();
        if (s.StartsWith("```")) s = s[(s.IndexOf('\n') + 1)..].TrimEnd('`', '\n', ' ');   // JSON mode models sometimes fence it
        try { return JsonDocument.Parse(s).RootElement.Clone(); }
        catch (JsonException) { throw new AiException("the model did not answer with valid JSON", "Err_AiBadJson"); }
    }

    private static string Clean(string? name) =>
        new string((name ?? "").Trim().Replace('.', '・').Where(c => !char.IsControl(c)).ToArray()) is var n && n.Length > 60 ? n[..60] : n;

    private static List<DetectedField> Unique(IEnumerable<DetectedField> fields)
    {
        var seen = new Dictionary<string, int>();
        return fields.Select(f =>
        {
            seen[f.Name] = seen.GetValueOrDefault(f.Name) + 1;
            return seen[f.Name] == 1 ? f : f with { Name = $"{f.Name}_{seen[f.Name]}" };
        }).ToList();
    }
}
