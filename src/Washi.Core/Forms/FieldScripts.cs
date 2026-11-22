// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Washi.Core.Forms;

/// <summary>
/// <see cref="FieldLogic"/> as the field actions Acrobat writes and every full PDF viewer runs: the AF functions of the
/// Acrobat JavaScript API (AFNumber_Format, AFPercent_*, AFDate_*Ex, AFSpecial_*, AFRange_Validate,
/// AFSimple_Calculate) in the field's /AA, and the AcroForm's /CO (calculation order). Only these calls are read and
/// written; other scripts are left alone and not run.
/// </summary>
public static partial class FieldScripts
{
    // ------------------------------------------------------------------ reading

    /// <summary>The logic a field's /AA asks for (null when it has none Washi knows).</summary>
    public static FieldLogic? Read(PdfDictionary field)
    {
        var aa = Inherited(field, "/AA") as PdfDictionary;
        if (aa is null) return null;
        string k = Script(aa, "/K"), f = Script(aa, "/F"), v = Script(aa, "/V"), c = Script(aa, "/C");
        var logic = new FieldLogic(Format(f, k), Range(v), Calc(c));
        return logic.IsEmpty ? null : logic;
    }

    private static FieldFormat? Format(string f, string k)
    {
        var all = f + "\n" + k;
        if (NumberRx().Match(all) is { Success: true } n)
        {
            var args = Args(n.Groups[2].Value);
            var kind = n.Groups[1].Value == "Percent" ? FieldFormatKind.Percent : FieldFormatKind.Number;
            bool prepend = args.Count <= 5 || !args[5].Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
            return new FieldFormat(kind, Int(args, 0, 2), Int(args, 1, 0), kind == FieldFormatKind.Number ? Str(args, 4) : "", prepend);
        }
        if (DateRx().Match(all) is { Success: true } d) return FieldFormat.Date(Str(Args(d.Groups[1].Value), 0));
        if (MaskRx().Match(all) is { Success: true } m) return FieldFormat.Mask(Str(Args(m.Groups[1].Value), 0));
        if (SpecialRx().Match(all) is { Success: true } s)
            return FieldFormat.Mask(Int(Args(s.Groups[1].Value), 0, 0) switch { 1 => "99999-9999", 2 => "(999) 999-9999", 3 => "999-99-9999", _ => "99999" });
        return null;
    }

    private static FieldRange? Range(string v)
    {
        if (RangeRx().Match(v) is not { Success: true } m) return null;
        var a = Args(m.Groups[1].Value);
        bool hasMin = Bool(a, 0), hasMax = Bool(a, 2);
        if (!hasMin && !hasMax) return null;
        return new FieldRange(hasMin ? Num(a, 1) : null, hasMax ? Num(a, 3) : null);
    }

    private static FieldCalc? Calc(string c)
    {
        if (CalcRx().Match(c) is not { Success: true } m) return null;
        var op = m.Groups[1].Value.ToUpperInvariant() switch
        {
            "SUM" => CalcOp.Sum, "PRD" => CalcOp.Product, "AVG" => CalcOp.Average, "MIN" => CalcOp.Min, "MAX" => CalcOp.Max,
            _ => (CalcOp?)null,
        };
        if (op is null) return null;
        var names = StringRx().Matches(m.Groups[2].Value).Select(s => Unescape(s.Groups[s.Groups[1].Success ? 1 : 2].Value)).ToList();
        return names.Count == 0 ? null : new FieldCalc(op.Value, names);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Sets (or clears) the field's /AA actions for <paramref name="logic"/>; other actions in /AA are kept.</summary>
    public static void Write(PdfDocument doc, PdfDictionary field, FieldLogic? logic)
    {
        var aa = Inherited(field, "/AA") as PdfDictionary;
        if (aa is null)
        {
            if (logic is null || logic.IsEmpty) return;
            aa = new PdfDictionary(doc);
            field.Elements["/AA"] = aa;
        }
        foreach (var key in new[] { "/K", "/F", "/V", "/C" }) aa.Elements.Remove(key);
        if (logic is not null)
        {
            if (logic.Format is { Kind: not FieldFormatKind.None } f)
            {
                var (format, keystroke) = f.Kind switch
                {
                    FieldFormatKind.Number => ($"AFNumber_Format({f.Decimals}, {f.Separator}, 0, 0, {Quote(f.Currency)}, {(f.CurrencyFirst ? "true" : "false")});",
                                               $"AFNumber_Keystroke({f.Decimals}, {f.Separator}, 0, 0, {Quote(f.Currency)}, {(f.CurrencyFirst ? "true" : "false")});"),
                    FieldFormatKind.Percent => ($"AFPercent_Format({f.Decimals}, {f.Separator});", $"AFPercent_Keystroke({f.Decimals}, {f.Separator});"),
                    FieldFormatKind.Date => ($"AFDate_FormatEx({Quote(f.Pattern)});", $"AFDate_KeystrokeEx({Quote(f.Pattern)});"),
                    _ => ($"AFSpecial_KeystrokeEx({Quote(f.Pattern)});", $"AFSpecial_KeystrokeEx({Quote(f.Pattern)});"),
                };
                aa.Elements["/F"] = Action(doc, format);
                aa.Elements["/K"] = Action(doc, keystroke);
            }
            if (logic.Range is { } r && (r.Min is not null || r.Max is not null))
                aa.Elements["/V"] = Action(doc, $"AFRange_Validate({B(r.Min)}, {N(r.Min)}, {B(r.Max)}, {N(r.Max)});");
            if (logic.Calc is { Sources.Count: > 0 } c)
                aa.Elements["/C"] = Action(doc, $"AFSimple_Calculate(\"{Op(c.Op)}\", new Array ({string.Join(", ", c.Sources.Select(Quote))}));");
        }
        if (aa.Elements.Count == 0) field.Elements.Remove("/AA");

        static string B(double? v) => v is null ? "false" : "true";
        static string N(double? v) => FieldLogic.Plain(v ?? 0);
    }

    /// <summary>Sets the AcroForm's /CO: the calculated fields, sources before the fields that use them.</summary>
    public static void WriteOrder(PdfDocument doc, PdfDictionary form, IEnumerable<(string Name, PdfDictionary Field, FieldCalc Calc)> calculated)
    {
        var list = calculated.ToList();
        if (list.Count == 0) { form.Elements.Remove("/CO"); return; }
        var byName = list.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var order = FieldLogic.CalculationOrder(byName.ToDictionary(p => p.Key, p => p.Value.Calc));
        var co = new PdfArray(doc);
        foreach (var name in order)
            if (byName[name].Field.Reference is { } r) co.Elements.Add(r);
        form.Elements["/CO"] = co;
    }

    private static string Op(CalcOp op) => op switch
    {
        CalcOp.Sum => "SUM", CalcOp.Product => "PRD", CalcOp.Average => "AVG", CalcOp.Min => "MIN", _ => "MAX",
    };

    private static PdfDictionary Action(PdfDocument doc, string js)
    {
        var a = new PdfDictionary(doc);
        a.Elements.SetName("/S", "/JavaScript");
        a.Elements["/JS"] = new PdfString(js, PdfStringEncoding.Unicode);
        return a;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string Unescape(string s) => Regex.Replace(s, @"\\(.)", "$1");

    // ------------------------------------------------------------------ helpers

    private static string Script(PdfDictionary aa, string key)
    {
        if (Deref(aa.Elements[key]) is not PdfDictionary a) return "";
        return Deref(a.Elements["/JS"]) switch
        {
            PdfString s => s.Value,
            PdfDictionary { Stream: not null } st => Encoding.UTF8.GetString(st.Stream.UnfilteredValue),
            _ => "",
        };
    }

    /// <summary>The arguments of a call, split at top-level commas (strings may hold commas).</summary>
    private static List<string> Args(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString && ch == '\\' && i + 1 < text.Length) { sb.Append(ch).Append(text[++i]); continue; }
            if (ch is '"' or '\'') inString = !inString;
            if (ch == ',' && !inString) { list.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString().Trim());
        return list;
    }

    private static int Int(List<string> a, int i, int fallback) =>
        i < a.Count && int.TryParse(a[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static double? Num(List<string> a, int i) =>
        i < a.Count && double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static bool Bool(List<string> a, int i) => i < a.Count && a[i].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    private static string Str(List<string> a, int i) => i < a.Count && a[i].Length >= 2 ? Unescape(a[i][1..^1]) : "";

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    private static PdfItem? Inherited(PdfDictionary d, string key)
    {
        for (var node = d; node is not null; node = Deref(node.Elements["/Parent"]) as PdfDictionary)
            if (Deref(node.Elements[key]) is { } v) return v;
        return null;
    }

    [GeneratedRegex(@"AF(Number|Percent)_(?:Format|Keystroke)\s*\(([^)]*)\)")]
    private static partial Regex NumberRx();
    [GeneratedRegex(@"AFDate_(?:Format|Keystroke)Ex\s*\(([^)]*)\)")]
    private static partial Regex DateRx();
    [GeneratedRegex(@"AFSpecial_KeystrokeEx\s*\(([^)]*)\)")]
    private static partial Regex MaskRx();
    [GeneratedRegex(@"AFSpecial_(?:Format|Keystroke)\s*\(([^)]*)\)")]
    private static partial Regex SpecialRx();
    [GeneratedRegex(@"AFRange_Validate\s*\(([^)]*)\)")]
    private static partial Regex RangeRx();
    [GeneratedRegex(@"AFSimple_Calculate\s*\(\s*[""'](\w+)[""']\s*,([^;]*)", RegexOptions.Singleline)]
    private static partial Regex CalcRx();
    [GeneratedRegex(@"""((?:\\.|[^""\\])*)""|'((?:\\.|[^'\\])*)'")]
    private static partial Regex StringRx();
}
