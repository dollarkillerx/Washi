// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Core.Ocr;

/// <summary>A page image handed to an OCR engine (PNG bytes).</summary>
public sealed record OcrImage(byte[] Png, int Width, int Height);

/// <summary>One recognised line. The box is in image pixels (engines) or upright page points (results), top-left origin.</summary>
public sealed record OcrLine(string Text, PdfRect Box, double Confidence);

public sealed class OcrException(string message) : Exception(message);

public interface IOcrEngine
{
    /// <summary>Short id: vision, tesseract, ai.</summary>
    string Name { get; }

    /// <summary>What the user sees, e.g. "Apple Vision (on this Mac)".</summary>
    string Describe { get; }

    /// <summary>True when page images leave this machine.</summary>
    bool SendsDataOffDevice { get; }

    /// <summary>Resolution to render pages at.</summary>
    int Dpi { get; }

    /// <summary>Recognises the lines on one page image. Boxes in image pixels.</summary>
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(OcrImage image, IReadOnlyList<string> languages, CancellationToken ct = default);
}

/// <summary>Washi's language codes and how each engine names them.</summary>
public static class OcrLanguages
{
    public static readonly string[] All = ["ja", "zh-Hans", "zh-Hant", "ko", "en", "fr", "de"];

    /// <summary>"ja,en" → ["ja","en"]; "auto"/empty → null (the engine decides).</summary>
    public static IReadOnlyList<string> Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return [];
        var list = new List<string>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = raw.ToLowerInvariant() switch
            {
                "zh" or "zh-cn" or "zh-hans" => "zh-Hans",
                "zh-tw" or "zh-hk" or "zh-hant" => "zh-Hant",
                var c => All.FirstOrDefault(a => a.Equals(c, StringComparison.OrdinalIgnoreCase)),
            };
            if (code is null) throw new OcrException($"unknown OCR language '{raw}' (use {string.Join(", ", All)})");
            if (!list.Contains(code)) list.Add(code);
        }
        return list;
    }

    public static string Vision(string code) => code switch
    {
        "ja" => "ja-JP", "ko" => "ko-KR", "en" => "en-US", "fr" => "fr-FR", "de" => "de-DE", _ => code,
    };

    public static string Tesseract(string code) => code switch
    {
        "ja" => "jpn", "zh-Hans" => "chi_sim", "zh-Hant" => "chi_tra", "ko" => "kor",
        "en" => "eng", "fr" => "fra", "de" => "deu", _ => code,
    };
}

/// <summary>The on-device engines. The AI engine lives in Washi.Ai (it needs a model and consent).</summary>
public static class OcrEngines
{
    public static string[] Names => ["auto", "vision", "tesseract", "ai"];

    /// <summary>"auto": Apple Vision on macOS, otherwise Tesseract if installed.</summary>
    public static IOcrEngine Create(string? name)
    {
        switch ((name ?? "auto").ToLowerInvariant())
        {
            case "vision":
                return VisionOcr.IsAvailable ? new VisionOcr()
                    : throw new OcrException("Apple Vision OCR needs macOS with the Xcode command line tools (xcode-select --install)");
            case "tesseract":
                return TesseractOcr.Find() is { } t ? new TesseractOcr(t)
                    : throw new OcrException("tesseract was not found; install it (macOS: brew install tesseract tesseract-lang)");
            case "auto":
                if (VisionOcr.IsAvailable) return new VisionOcr();
                if (TesseractOcr.Find() is { } path) return new TesseractOcr(path);
                throw new OcrException("no on-device OCR engine: on macOS install the Xcode command line tools, elsewhere install tesseract");
            case "ai":
                throw new OcrException("the AI engine is created by Washi.Ai (it needs a model and your consent)");
            default:
                throw new OcrException($"unknown OCR engine '{name}' (use {string.Join(", ", Names)})");
        }
    }

    internal static async Task<(int Code, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new OcrException($"cannot start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } throw; }
        return (p.ExitCode, await stdout, await stderr);
    }
}
