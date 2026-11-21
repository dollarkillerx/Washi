// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Washi.Core;
using Washi.Core.Ocr;

namespace Washi.Ai;

/// <summary>
/// OCR by a vision model (Claude, GPT, DeepSeek, a local model). Good at handwriting and messy
/// scans; its boxes are approximate (a 0-1000 grid), so the searchable layer is less exact than
/// with Vision or Tesseract. Page images leave the machine unless the model is local, so callers
/// ask first (<see cref="DescribeOcr"/>).
/// </summary>
public sealed class AiOcrEngine : IOcrEngine
{
    private readonly IAiModel _model;

    public AiOcrEngine(IAiModel model)
    {
        if (!model.AcceptsImages)
            throw new AiException($"{model.Describe} is set up without images (vision off); OCR needs a model that reads images", "Err_AiNoVision", model.Describe);
        _model = model;
    }

    public string Name => "ai";
    public string Describe => _model.Describe;
    public bool SendsDataOffDevice => _model.SendsDataOffDevice;
    public int Dpi => 150;

    public static AiPayload DescribeOcr(IAiModel model, int pages) =>
        new(model.Describe, model.SendsDataOffDevice, pages, 0, 0, 0, 0);

    private const string System = """
        You transcribe a scanned page exactly, for a searchable PDF.
        Return every line of text on the page, in reading order, as printed or handwritten: do not
        translate, correct, summarise or add anything. Keep numbers and spacing as they appear.
        For each line give its box in a 0-1000 grid over the page image (x0, y0 = top left;
        x1, y1 = bottom right; x to the right, y down). A vertical (縦書き) column is one line.
        If a word is illegible, write what you can read and mark the rest with ?.
        """;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["lines"],"properties":{
          "lines":{"type":"array","items":{"type":"object","additionalProperties":false,
            "required":["text","box"],"properties":{
              "text":{"type":"string"},
              "box":{"type":"array","items":{"type":"integer"}}}}}}}
        """).RootElement.Clone();

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(OcrImage image, IReadOnlyList<string> languages, CancellationToken ct = default)
    {
        var hint = languages.Count > 0 ? $"The page is probably in: {string.Join(", ", languages)}.\n" : "";
        var answer = await _model.CompleteAsync(
            new AiRequest(System, hint + "Transcribe this page.", [new AiImage("image/png", image.Png)], Schema), ct);
        return Parse(answer, image.Width, image.Height);
    }

    internal static List<OcrLine> Parse(string answer, int width, int height)
    {
        var s = answer.Trim();
        if (s.StartsWith("```")) s = s[(s.IndexOf('\n') + 1)..].TrimEnd('`', '\n', ' ');
        JsonElement root;
        try { root = JsonDocument.Parse(s).RootElement.Clone(); }
        catch (JsonException) { throw new AiException("the model did not answer with valid JSON", "Err_AiBadJson"); }
        var lines = new List<OcrLine>();
        if (!root.TryGetProperty("lines", out var arr) || arr.ValueKind != JsonValueKind.Array) return lines;
        foreach (var l in arr.EnumerateArray())
        {
            var text = l.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
            if (text.Length == 0 || !l.TryGetProperty("box", out var b) || b.ValueKind != JsonValueKind.Array) continue;
            var v = b.EnumerateArray().Select(x => x.TryGetDouble(out var d) ? Math.Clamp(d, 0, 1000) : 0).ToArray();
            if (v.Length != 4) continue;
            double x0 = Math.Min(v[0], v[2]), x1 = Math.Max(v[0], v[2]), y0 = Math.Min(v[1], v[3]), y1 = Math.Max(v[1], v[3]);
            if (x1 - x0 < 1 || y1 - y0 < 1) continue;
            lines.Add(new OcrLine(text, new PdfRect(x0 / 1000 * width, y0 / 1000 * height, x1 / 1000 * width, y1 / 1000 * height), 0.5));
        }
        return lines;
    }
}
