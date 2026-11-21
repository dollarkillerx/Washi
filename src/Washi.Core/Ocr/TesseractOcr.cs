// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Washi.Core.Ocr;

/// <summary>
/// Tesseract (Apache-2.0) through its command line, on the device. Languages need their
/// traineddata installed (macOS: brew install tesseract-lang); missing ones are skipped.
/// </summary>
public sealed class TesseractOcr(string executable) : IOcrEngine
{
    public string Name => "tesseract";
    public string Describe => "Tesseract (on this machine)";
    public bool SendsDataOffDevice => false;
    public int Dpi => 300;

    /// <summary>Languages skipped because their traineddata is not installed (after a run).</summary>
    public List<string> Missing { get; } = [];

    public static string? Find()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);
        var name = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        return dirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

    private HashSet<string>? _installed;

    public async Task<IReadOnlySet<string>> InstalledAsync(CancellationToken ct = default)
    {
        if (_installed is not null) return _installed;
        var (_, output, err) = await OcrEngines.RunAsync(executable, ["--list-langs"], ct);
        _installed = [.. (output + "\n" + err).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.Contains(' '))];
        return _installed;
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(OcrImage image, IReadOnlyList<string> languages, CancellationToken ct = default)
    {
        var installed = await InstalledAsync(ct);
        var wanted = (languages.Count > 0 ? languages : OcrLanguages.All).Select(OcrLanguages.Tesseract).ToList();
        var use = wanted.Where(installed.Contains).ToList();
        foreach (var m in wanted.Except(use)) if (!Missing.Contains(m)) Missing.Add(m);
        if (use.Count == 0)
            throw new OcrException($"tesseract has none of the languages {string.Join("+", wanted)} installed (installed: {string.Join(", ", installed)}); macOS: brew install tesseract-lang", "Err_OcrTesseractLanguages", string.Join("+", wanted), string.Join(", ", installed));

        var png = Path.Combine(Path.GetTempPath(), $"washi-ocr-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(png, image.Png, ct);
        try
        {
            var (code, output, err) = await OcrEngines.RunAsync(executable,
                [png, "stdout", "-l", string.Join('+', use), "--psm", "3", "--dpi", Dpi.ToString(CultureInfo.InvariantCulture), "tsv"], ct);
            if (code != 0) throw new OcrException("tesseract failed: " + err.Trim(), "Err_OcrFailed", "Tesseract", err.Trim());
            return ParseTsv(output, cjk: use.Any(l => l is "jpn" or "chi_sim" or "chi_tra" or "kor"));
        }
        finally { File.Delete(png); }
    }

    /// <summary>TSV word rows grouped into lines (block, paragraph, line).</summary>
    internal static List<OcrLine> ParseTsv(string tsv, bool cjk)
    {
        var lines = new List<OcrLine>();
        var groups = new Dictionary<(int, int, int), List<(string Text, double X0, double Y0, double X1, double Y1, double Conf)>>();
        var order = new List<(int, int, int)>();
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var c = row.TrimEnd('\r').Split('\t');
            if (c.Length < 12 || c[0] != "5" || c[11].Trim().Length == 0) continue;
            var key = (int.Parse(c[2], CultureInfo.InvariantCulture), int.Parse(c[3], CultureInfo.InvariantCulture), int.Parse(c[4], CultureInfo.InvariantCulture));
            double x = D(c[6]), y = D(c[7]), w = D(c[8]), h = D(c[9]);
            if (!groups.TryGetValue(key, out var g)) { groups[key] = g = []; order.Add(key); }
            g.Add((c[11], x, y, x + w, y + h, D(c[10]) / 100));
        }
        foreach (var key in order)
        {
            var words = groups[key];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                // CJK words come out one character apart: join them without spaces
                if (i > 0 && !(cjk && IsCjk(words[i - 1].Text[^1]) && IsCjk(words[i].Text[0]))) sb.Append(' ');
                sb.Append(words[i].Text);
            }
            lines.Add(new OcrLine(sb.ToString(),
                new PdfRect(words.Min(w => w.X0), words.Min(w => w.Y0), words.Max(w => w.X1), words.Max(w => w.Y1)),
                words.Average(w => Math.Max(0, w.Conf))));
        }
        return lines;
    }

    private static bool IsCjk(char ch) => ch is >= '　' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯';

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
