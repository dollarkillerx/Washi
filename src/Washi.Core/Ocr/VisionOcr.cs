// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;

namespace Washi.Core.Ocr;

/// <summary>
/// Apple's Vision text recognition (macOS 10.15+), on the device. A small Swift helper ships as
/// source inside Washi and is compiled on first use with the Xcode command line tools, then cached
/// under ~/Library/Caches/washi (keyed by a hash of its source).
/// </summary>
public sealed class VisionOcr : IOcrEngine
{
    public string Name => "vision";
    public string Describe => "Apple Vision (on this Mac)";
    public bool SendsDataOffDevice => false;
    public int Dpi => 300;

    public static bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists("/usr/bin/xcrun")
        && (File.Exists(HelperPath) || Directory.Exists("/Library/Developer/CommandLineTools") || Directory.Exists("/Applications/Xcode.app"));

    private static readonly Lazy<string> Source = new(() =>
    {
        using var s = typeof(VisionOcr).Assembly.GetManifestResourceStream("Washi.Core.Ocr.vision-ocr.swift")
                      ?? throw new OcrException("the Vision helper source is missing from this build");
        return new StreamReader(s).ReadToEnd();
    });

    private static string HelperPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "washi",
        "vision-ocr-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Source.Value)))[..12].ToLowerInvariant());

    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    /// <summary>Compiles the helper if needed (about 10 s, once). Returns its path.</summary>
    public static async Task<string> EnsureHelperAsync(CancellationToken ct = default)
    {
        var path = HelperPath;
        if (File.Exists(path)) return path;
        await BuildLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var src = Path.Combine(dir, $"vision-ocr-{Environment.ProcessId}.swift");
            var tmp = path + $".{Environment.ProcessId}.tmp";
            await File.WriteAllTextAsync(src, Source.Value, ct);
            try
            {
                var (code, _, err) = await OcrEngines.RunAsync("/usr/bin/xcrun", ["swiftc", "-O", "-o", tmp, src], ct);
                if (code != 0) throw new OcrException("could not compile the Vision OCR helper: " + err.Trim());
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                File.Delete(src);
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            return path;
        }
        finally { BuildLock.Release(); }
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(OcrImage image, IReadOnlyList<string> languages, CancellationToken ct = default)
    {
        var helper = await EnsureHelperAsync(ct);
        var png = Path.Combine(Path.GetTempPath(), $"washi-ocr-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(png, image.Png, ct);
        try
        {
            var langs = string.Join(',', languages.Select(OcrLanguages.Vision));
            var (code, output, err) = await OcrEngines.RunAsync(helper, [png, langs, "accurate"], ct);
            if (code != 0) throw new OcrException("Vision OCR failed: " + err.Trim());
            using var json = JsonDocument.Parse(output);
            var lines = new List<OcrLine>();
            foreach (var l in json.RootElement.GetProperty("lines").EnumerateArray())
            {
                double x = l.GetProperty("x").GetDouble(), y = l.GetProperty("y").GetDouble();
                double w = l.GetProperty("w").GetDouble(), h = l.GetProperty("h").GetDouble();
                var text = l.GetProperty("text").GetString() ?? "";
                if (text.Trim().Length > 0)
                    lines.Add(new OcrLine(text, new PdfRect(x, y, x + w, y + h), l.GetProperty("conf").GetDouble()));
            }
            return lines;
        }
        finally { File.Delete(png); }
    }
}
