// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Washi.Core.Forms;

public enum FieldFormatKind
{
    None,
    /// <summary>A number: decimals, digit grouping, a currency symbol (Acrobat's AFNumber_Format).</summary>
    Number,
    /// <summary>A fraction shown as a percentage: 0.05 is "5.00%" (AFPercent_Format).</summary>
    Percent,
    /// <summary>A date in a pattern such as yyyy/mm/dd (AFDate_FormatEx).</summary>
    Date,
    /// <summary>Characters in a mask: 9 a digit, A a letter, O a letter or digit, X anything; others are kept
    /// ("999-9999" for a Japanese postal code; AFSpecial_KeystrokeEx).</summary>
    Mask,
}

/// <summary>How a field's value is shown and what it accepts.</summary>
/// <param name="Separator">Digit grouping, as Acrobat numbers it: 0 "1,234.56", 1 "1234.56", 2 "1.234,56", 3 "1234,56".</param>
/// <param name="Pattern">Date: yyyy yy mm m dd d (and mmm, mmmm for month names); mask: see <see cref="FieldFormatKind.Mask"/>.</param>
public sealed record FieldFormat(FieldFormatKind Kind, int Decimals = 2, int Separator = 0, string Currency = "", bool CurrencyFirst = true,
                                 string Pattern = "")
{
    public static FieldFormat Number(int decimals = 0, string currency = "") => new(FieldFormatKind.Number, decimals, 0, currency);
    public static FieldFormat Date(string pattern = "yyyy/mm/dd") => new(FieldFormatKind.Date, Pattern: pattern);
    public static FieldFormat Mask(string mask) => new(FieldFormatKind.Mask, Pattern: mask);
}

/// <summary>The smallest and largest number the field accepts (either may be open).</summary>
public sealed record FieldRange(double? Min, double? Max);

public enum CalcOp { Sum, Product, Average, Min, Max }

/// <summary>The field's value is computed from other fields (AFSimple_Calculate).</summary>
public sealed record FieldCalc(CalcOp Op, IReadOnlyList<string> Sources);

/// <summary>A field's format, accepted range and calculation (any may be null).</summary>
public sealed record FieldLogic(FieldFormat? Format = null, FieldRange? Range = null, FieldCalc? Calc = null)
{
    public bool IsEmpty => Format is null or { Kind: FieldFormatKind.None } && Range is null && Calc is null;

    // ------------------------------------------------------------------ values

    /// <summary>
    /// What is stored for <paramref name="input"/> (Acrobat keeps numbers plain: "1234.5", "0.05"), or an error in
    /// English and its key. Full-width digits, currency symbols and grouping are accepted as typed.
    /// </summary>
    public (string? Value, string? Error, string? Key) Accept(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return ("", null, null);
        var f = Format;
        double? number = null;
        string value = text;
        switch (f?.Kind)
        {
            case FieldFormatKind.Number:
                if (ParseNumber(text, f.Separator) is not { } n) return (null, $"'{input}' is not a number", "Err_FieldNotNumber");
                number = Math.Round(n, Math.Clamp(f.Decimals, 0, 10));
                value = Plain(number.Value);
                break;
            case FieldFormatKind.Percent:
                var t = text.Normalize(NormalizationForm.FormKC);
                bool pct = t.EndsWith('%');
                if (ParseNumber(t.TrimEnd('%', ' '), f.Separator) is not { } p) return (null, $"'{input}' is not a number", "Err_FieldNotNumber");
                number = pct || Math.Abs(p) > 1 ? p / 100 : p;   // "5%" and "5" mean 5 %; "0.05" is already a fraction
                value = Plain(Math.Round(number.Value, Math.Clamp(f.Decimals, 0, 10) + 2));
                break;
            case FieldFormatKind.Date:
                if (ParseDate(text) is not { } d) return (null, $"'{input}' is not a date", "Err_FieldNotDate");
                value = FormatDate(d, f.Pattern);
                break;
            case FieldFormatKind.Mask:
                if (ApplyMask(text, f.Pattern) is not { } m) return (null, $"'{input}' does not fit {f.Pattern}", "Err_FieldNotMask");
                value = m;
                break;
        }
        if (Range is { } r)
        {
            number ??= ParseNumber(text, f?.Separator ?? 0);
            if (number is not { } v) return (null, $"'{input}' is not a number", "Err_FieldNotNumber");
            if (r.Min is { } min && v < min || r.Max is { } max && v > max)
                return (null, $"{Plain(v)} is outside {RangeText(r)}", "Err_FieldRange");
        }
        return (value, null, null);
    }

    /// <summary>What the field shows for a stored value ("1234.5" as "1,234.50", "0.05" as "5.00%").</summary>
    public string Display(string stored)
    {
        if (stored.Length == 0 || Format is not { } f) return stored;
        switch (f.Kind)
        {
            case FieldFormatKind.Number when double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var n):
                var s = Group(Math.Abs(n), f.Decimals, f.Separator);
                s = f.Currency.Length == 0 ? s : f.CurrencyFirst ? f.Currency + s : s + f.Currency;
                return n < 0 ? "-" + s : s;
            case FieldFormatKind.Percent when double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var p):
                return (p < 0 ? "-" : "") + Group(Math.Abs(p * 100), f.Decimals, f.Separator) + "%";
            default:
                return stored;
        }
    }

    public static string RangeText(FieldRange r) => (r.Min, r.Max) switch
    {
        ({ } a, { } b) => $"{Plain(a)}–{Plain(b)}",
        ({ } a, null) => $"≥ {Plain(a)}",
        (null, { } b) => $"≤ {Plain(b)}",
        _ => "",
    };

    public static string Plain(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Group(double v, int decimals, int separator)
    {
        var s = v.ToString("N" + Math.Clamp(decimals, 0, 10).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);   // 1,234.56
        return separator switch
        {
            1 => s.Replace(",", ""),
            2 => s.Replace(",", "\u0001").Replace(".", ",").Replace("\u0001", "."),
            3 => s.Replace(",", "").Replace(".", ","),
            _ => s,
        };
    }

    /// <summary>A number as people type it: "1,234.5", "¥1,234", "1.234,5" (with the comma style), "－５", "(12)".</summary>
    public static double? ParseNumber(string text, int separator = 0)
    {
        var t = text.Normalize(NormalizationForm.FormKC).Trim();
        bool negative = t.StartsWith('(') && t.EndsWith(')') || t.Contains('-') || t.Contains('−') || t.StartsWith('▲') || t.StartsWith('△');
        var sb = new StringBuilder();
        char dec = separator is 2 or 3 ? ',' : '.';
        foreach (var c in t)
            if (char.IsAsciiDigit(c)) sb.Append(c);
            else if (c == dec) sb.Append('.');
        if (sb.Length == 0 || sb.ToString().Count(c => c == '.') > 1) return null;
        if (!double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        // anything but digits, separators, signs, spaces and currency marks makes it not a number
        if (t.Any(c => char.IsLetter(c) && !"円元원".Contains(c) && !char.IsSymbol(c))) return null;
        return negative ? -v : v;
    }

    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>A date as people type it: 2026-11-22, 2026/11/22, 2026年11月22日, 令和8年11月22日, 22.11.2026 (day first when
    /// the first number cannot be a year).</summary>
    public static DateOnly? ParseDate(string text)
    {
        var t = text.Normalize(NormalizationForm.FormKC).Trim();
        var era = Regex.Match(t, @"^(令和|平成|昭和|R|H|S)\s*(\d{1,2}|元)\s*[年./-]\s*(\d{1,2})\s*[月./-]\s*(\d{1,2})");
        if (era.Success)
        {
            int baseYear = era.Groups[1].Value switch { "令和" or "R" => 2018, "平成" or "H" => 1988, _ => 1925 };
            int y = era.Groups[2].Value == "元" ? 1 : int.Parse(era.Groups[2].Value, CultureInfo.InvariantCulture);
            return Make(baseYear + y, era.Groups[3].Value, era.Groups[4].Value);
        }
        var nums = Regex.Matches(t, @"\d+").Select(m => m.Value).ToList();
        if (nums.Count == 1 && nums[0].Length == 8) return Make(int.Parse(nums[0][..4], CultureInfo.InvariantCulture), nums[0][4..6], nums[0][6..]);
        if (nums.Count != 3) return null;
        if (nums[0].Length == 4) return Make(int.Parse(nums[0], CultureInfo.InvariantCulture), nums[1], nums[2]);
        if (nums[2].Length == 4)
        {
            int a = int.Parse(nums[0], CultureInfo.InvariantCulture), b = int.Parse(nums[1], CultureInfo.InvariantCulture);
            // 11/22/2026 (month first) unless the first cannot be a month: 22.11.2026
            return a > 12 ? Make(int.Parse(nums[2], CultureInfo.InvariantCulture), nums[1], nums[0])
                          : Make(int.Parse(nums[2], CultureInfo.InvariantCulture), nums[0], nums[1]) ?? Make(int.Parse(nums[2], CultureInfo.InvariantCulture), nums[1], nums[0]);
        }
        return null;

        static DateOnly? Make(int y, string m, string d) =>
            int.TryParse(m, out var mm) && int.TryParse(d, out var dd) && mm is >= 1 and <= 12 && dd >= 1 && y is >= 1 and <= 9999
            && dd <= DateTime.DaysInMonth(y, mm) ? new DateOnly(y, mm, dd) : null;
    }

    /// <summary>Acrobat's date pattern letters: yyyy yy mmmm mmm mm m dd d.</summary>
    public static string FormatDate(DateOnly d, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) pattern = "yyyy/mm/dd";
        var sb = new StringBuilder();
        for (int i = 0; i < pattern.Length;)
        {
            string Take(string token) { i += token.Length; return token; }
            if (pattern.AsSpan(i).StartsWith("yyyy")) { Take("yyyy"); sb.Append(d.Year.ToString("D4", CultureInfo.InvariantCulture)); }
            else if (pattern.AsSpan(i).StartsWith("yy")) { Take("yy"); sb.Append((d.Year % 100).ToString("D2", CultureInfo.InvariantCulture)); }
            else if (pattern.AsSpan(i).StartsWith("mmmm")) { Take("mmmm"); sb.Append(Months[d.Month - 1]); }
            else if (pattern.AsSpan(i).StartsWith("mmm")) { Take("mmm"); sb.Append(Months[d.Month - 1][..3]); }
            else if (pattern.AsSpan(i).StartsWith("mm")) { Take("mm"); sb.Append(d.Month.ToString("D2", CultureInfo.InvariantCulture)); }
            else if (pattern[i] == 'm') { i++; sb.Append(d.Month.ToString(CultureInfo.InvariantCulture)); }
            else if (pattern.AsSpan(i).StartsWith("dd")) { Take("dd"); sb.Append(d.Day.ToString("D2", CultureInfo.InvariantCulture)); }
            else if (pattern[i] == 'd') { i++; sb.Append(d.Day.ToString(CultureInfo.InvariantCulture)); }
            else sb.Append(pattern[i++]);
        }
        return sb.ToString();
    }

    /// <summary>The characters of <paramref name="text"/> placed into <paramref name="mask"/>, or null when they do not fit.</summary>
    public static string? ApplyMask(string text, string mask)
    {
        var chars = text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)).ToList();
        int slots = mask.Count(c => c is '9' or 'A' or 'O' or 'X');
        // the typed separators may be left out ("1234567" for "999-9999") or typed as they are
        var data = chars.Count == mask.Length ? chars.Where((c, i) => mask[i] is '9' or 'A' or 'O' or 'X').ToList()
                                              : chars.Where(char.IsLetterOrDigit).ToList();
        if (data.Count != slots) return null;
        var sb = new StringBuilder();
        int k = 0;
        foreach (var m in mask)
        {
            if (m is '9' or 'A' or 'O' or 'X')
            {
                var c = data[k++];
                bool ok = m switch { '9' => char.IsAsciiDigit(c), 'A' => char.IsLetter(c), 'O' => char.IsLetterOrDigit(c), _ => true };
                if (!ok) return null;
                sb.Append(c);
            }
            else sb.Append(m);
        }
        return sb.ToString();
    }

    /// <summary>The calculated value from the sources' stored values (empty ones count as 0, except for the average).</summary>
    public static string Calculate(FieldCalc calc, IReadOnlyDictionary<string, string> stored, int separator = 0)
    {
        var values = calc.Sources.Select(s => stored.TryGetValue(s, out var v) && v.Trim().Length > 0 ? ParseNumber(v, separator) : null).ToList();
        var present = values.OfType<double>().ToList();
        double result = calc.Op switch
        {
            CalcOp.Sum => present.Sum(),
            CalcOp.Product => present.Count == 0 ? 0 : present.Aggregate(1.0, (a, b) => a * b),
            CalcOp.Average => present.Count == 0 ? 0 : present.Average(),
            CalcOp.Min => present.Count == 0 ? 0 : present.Min(),
            _ => present.Count == 0 ? 0 : present.Max(),
        };
        return Plain(Math.Round(result, 10));
    }

    /// <summary>The order to calculate fields in (sources before the fields that use them); a cycle is an error.</summary>
    public static IReadOnlyList<string> CalculationOrder(IReadOnlyDictionary<string, FieldCalc> calcs)
    {
        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 visiting, 2 done
        foreach (var name in calcs.Keys) Visit(name);
        return order;

        void Visit(string name)
        {
            if (state.GetValueOrDefault(name) == 2) return;
            if (state.GetValueOrDefault(name) == 1)
                throw new Pages.PageAssemblyException($"the calculation of {name} depends on itself", "Err_FieldCalcCycle", name);
            state[name] = 1;
            if (calcs.TryGetValue(name, out var c))
                foreach (var s in c.Sources) if (calcs.ContainsKey(s)) Visit(s);
            state[name] = 2;
            if (calcs.ContainsKey(name)) order.Add(name);
        }
    }
}
