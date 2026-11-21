// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Washi.Core.Forms;
using Washi.Core.Pdfium;
using Washi.Masume;

namespace Washi.Tools;

/// <summary>Steps shared by the CLI, the tools and (later) the desktop app.</summary>
public static class FormPipeline
{
    public static readonly string[] Languages = ["auto", "ja", "zh-Hans", "zh-Hant", "ko", "en", "fr", "de"];

    public enum ScriptSource { Option, PageText, SystemLanguage }

    /// <summary>Runs Masume on every page (or the given 0-based pages) and returns the fields and the page text.</summary>
    public static (IReadOnlyList<DetectedField> Fields, string Text) Detect(string pdf, ISet<int>? pages = null)
    {
        using var doc = PdfiumDocument.Open(pdf);
        var fields = new List<DetectedField>();
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (pages is not null && !pages.Contains(i)) continue;
            var snap = doc.Snapshot(i, MasumeDetector.Scale);
            fields.AddRange(MasumeDetector.Detect(snap));
            foreach (var c in snap.Chars) text.Append(c.Text);
        }
        return (fields, text.ToString());
    }

    public static string Text(string pdf)
    {
        using var doc = PdfiumDocument.Open(pdf);
        return string.Concat(Enumerable.Range(0, doc.PageCount).SelectMany(doc.ReadChars).Select(c => c.Text));
    }

    /// <summary>--lang value, else the script of the page text, else the UI language.</summary>
    public static (FormScript Script, ScriptSource Source) ChooseScript(string? lang, string pageText, CultureInfo uiCulture)
    {
        if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (FormScripts.FromLanguage(lang), ScriptSource.Option);
        if (MasumeDetector.Readable(pageText) && FormScripts.Detect(pageText) is { } s)
            return (s, ScriptSource.PageText);
        return (FormScripts.FromCulture(uiCulture), ScriptSource.SystemLanguage);
    }

    /// <summary>"1-3,5" → {0,1,2,4}. Null or empty → null (all pages).</summary>
    public static HashSet<int>? ParsePages(string? spec, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var set = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(ends[0], out var a) || (ends.Length > 1 && !int.TryParse(ends[1], out _)))
                throw new ToolException($"bad page range '{part}' (use e.g. 1-3,5)");
            int b = ends.Length > 1 ? int.Parse(ends[1]) : a;
            if (a < 1 || b > pageCount || a > b) throw new ToolException($"page range '{part}' is outside 1-{pageCount}");
            for (int p = a; p <= b; p++) set.Add(p - 1);
        }
        return set;
    }
}
