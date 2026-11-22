// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Washi.Core.Pages;

namespace Washi.Core.Printing;

public sealed record PrinterInfo(string Name, bool IsDefault);

/// <param name="Printer">The printer's name (null: the system's default printer).</param>
/// <param name="Pages">Pages to print, 1-based ("1-3,5"; null: all).</param>
/// <param name="FitToPage">Shrink or grow each page to the paper; otherwise print at its actual size.</param>
public sealed record PrintOptions(string? Printer = null, int Copies = 1, string? Pages = null, bool TwoSided = false,
                                  bool Grayscale = false, bool FitToPage = true);

/// <param name="JobId">The print system's id for the job, when it gives one.</param>
public sealed record PrintResult(string Printer, string? JobId, int Pages);

public sealed class PrintException(string message, string? key = null, params object[] args) : Exception(message), Localization.ILocalizedError
{
    public string? Key { get; } = key;
    public object[] Args { get; } = args;
}

/// <summary>Sends PDFs to a printer.</summary>
public interface IPrinting
{
    IReadOnlyList<PrinterInfo> Printers();
    PrintResult Print(string pdf, PrintOptions options);
}

public static class PdfPrinting
{
    /// <summary>CUPS on macOS and Linux; the Windows print system on Windows.</summary>
    public static IPrinting Default { get; } = OperatingSystem.IsWindows() ? new WindowsPrinting() : new CupsPrinting();

    /// <summary>
    /// What every printer path checks first: the file opens without a password, its permissions allow printing, the
    /// page list is valid. Returns the pages to print (1-based ranges) and how many that is.
    /// </summary>
    public static (List<(int From, int To)> Ranges, int Count, int PageCount) Check(string pdf, PrintOptions o)
    {
        if (!File.Exists(pdf)) throw new PrintException($"{pdf} does not exist", "Err_PrintMissing", Path.GetFileName(pdf));
        var protection = PdfSecurity.Inspect(pdf, PdfPasswords.For(pdf));
        if (protection.NeedsPassword || protection.Encrypted && PdfSecurity.Inspect(pdf).NeedsPassword)
            throw new PrintException($"{Path.GetFileName(pdf)} needs a password to open; the print system cannot open it",
                                     "Err_PrintPassword", Path.GetFileName(pdf));
        if (protection.Encrypted && !protection.Permissions.Print)
            throw new PrintException($"{Path.GetFileName(pdf)} does not allow printing", "Err_PrintNotAllowed", Path.GetFileName(pdf));
        if (o.Copies is < 1 or > 999) throw new PrintException("copies must be 1-999", "Err_PrintCopies");
        int count = PageAssembly.PageCount(pdf);
        var ranges = PageAssembly.ParseRanges(o.Pages, count);
        return (ranges, ranges.Sum(r => r.To - r.From + 1), count);
    }

    /// <summary>The pages as a compact list ("1-3,5"): neighbouring ranges joined.</summary>
    internal static string Spec(IEnumerable<(int From, int To)> ranges)
    {
        var merged = new List<(int From, int To)>();
        foreach (var r in ranges)
            if (merged.Count > 0 && r.From == merged[^1].To + 1) merged[^1] = (merged[^1].From, r.To);
            else merged.Add(r);
        return string.Join(",", merged.Select(r => r.From == r.To ? r.From.ToString(CultureInfo.InvariantCulture)
                                                                  : $"{r.From.ToString(CultureInfo.InvariantCulture)}-{r.To.ToString(CultureInfo.InvariantCulture)}"));
    }
}

/// <summary>
/// CUPS (macOS, Linux): lists printers with lpstat and prints with lp, which takes PDF as it is. Commands are run
/// without a shell, each argument passed on its own.
/// </summary>
public sealed partial class CupsPrinting(Func<string, IReadOnlyList<string>, (int Exit, string Out, string Err)>? run = null) : IPrinting
{
    private readonly Func<string, IReadOnlyList<string>, (int, string, string)> _run = run ?? Run;

    public IReadOnlyList<PrinterInfo> Printers()
    {
        var (exit, names, _) = _run("lpstat", ["-e"]);
        if (exit != 0) return [];
        var (_, def, _) = _run("lpstat", ["-d"]);
        // "system default destination: Name" (or a translation of it; the name is after the last colon)
        var m = DefaultRx().Match(def);
        string? dflt = m.Success ? m.Groups[1].Value.Trim() : null;
        return [.. names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct().Select(n => new PrinterInfo(n, n == dflt)).OrderByDescending(p => p.IsDefault)];
    }

    public PrintResult Print(string pdf, PrintOptions o)
    {
        var (ranges, count, pageCount) = PdfPrinting.Check(pdf, o);
        string? printer = o.Printer;
        if (string.IsNullOrWhiteSpace(printer))
        {
            var printers = Printers();
            if (printers.Count == 0)
                throw new PrintException("no printer is set up on this computer", "Ui_PrintNoPrinters");
            printer = (printers.FirstOrDefault(p => p.IsDefault) ?? printers[0]).Name;   // no default: the first one
            o = o with { Printer = printer };
        }
        var args = Arguments(pdf, o, ranges, pageCount);
        var (exit, output, error) = _run("lp", args);
        if (exit != 0)
            throw new PrintException($"the print system refused the job: {(error + output).Trim()}", "Err_PrintFailed", (error + output).Trim());
        var job = JobRx().Match(output);
        return new PrintResult(printer, job.Success ? job.Groups[1].Value : null, count * o.Copies);
    }

    internal static List<string> Arguments(string pdf, PrintOptions o, List<(int From, int To)> ranges, int pageCount)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(o.Printer)) args.AddRange(["-d", o.Printer]);
        args.AddRange(["-n", o.Copies.ToString(CultureInfo.InvariantCulture), "-t", Path.GetFileName(pdf)]);
        if (o.Copies > 1) args.AddRange(["-o", "collate=true"]);
        var spec = PdfPrinting.Spec(ranges);
        if (spec != (pageCount == 1 ? "1" : $"1-{pageCount.ToString(CultureInfo.InvariantCulture)}")) args.AddRange(["-P", spec]);   // not every page
        args.AddRange(["-o", o.TwoSided ? "sides=two-sided-long-edge" : "sides=one-sided"]);
        if (o.Grayscale) args.AddRange(["-o", "print-color-mode=monochrome", "-o", "ColorModel=Gray"]);
        args.AddRange(["-o", o.FitToPage ? "print-scaling=fit" : "print-scaling=none"]);
        args.Add("--");
        args.Add(Path.GetFullPath(pdf));
        return args;
    }

    private static (int, string, string) Run(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";   // messages we parse in English
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) { p.Kill(); return (-1, "", "timed out"); }
            return (p.ExitCode, output.Result, error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new PrintException("no print system was found (CUPS: lp, lpstat)", "Err_PrintNoSystem");
        }
    }

    [GeneratedRegex(@"[:：]\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DefaultRx();
    [GeneratedRegex(@"request id is (\S+)")]
    private static partial Regex JobRx();
}
