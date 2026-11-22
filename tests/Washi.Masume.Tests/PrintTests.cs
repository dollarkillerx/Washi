// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using Washi.Core.Pages;
using Washi.Core.Printing;

namespace Washi.Masume.Tests;

/// <summary>Printing through CUPS: the right lp arguments, the checks before a job is sent, and what the print system says.</summary>
public sealed class PrintTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-print-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private string Pdf(int pages = 3, string name = "doc.pdf")
    {
        var d = new PdfDocument();
        for (int i = 0; i < pages; i++) d.AddPage();
        d.Save(P(name));
        return P(name);
    }

    /// <summary>A fake print system: records the commands, answers like CUPS.</summary>
    private sealed class FakeCups
    {
        public List<(string File, List<string> Args)> Calls { get; } = [];
        public (int, string, string) LpAnswer = (0, "request id is Office-42 (1 file(s))\n", "");

        public (int, string, string) Run(string file, IReadOnlyList<string> args)
        {
            Calls.Add((file, [.. args]));
            return (file, args.FirstOrDefault()) switch
            {
                ("lpstat", "-e") => (0, "Home_Laser\nOffice\n", ""),
                ("lpstat", "-d") => (0, "system default destination: Office\n", ""),
                ("lp", _) => LpAnswer,
                _ => (1, "", "unknown"),
            };
        }
    }

    [Fact]
    public void Printers_are_listed_with_the_default_first()
    {
        var cups = new FakeCups();
        var printers = new CupsPrinting(cups.Run).Printers();
        Assert.Equal([new PrinterInfo("Office", true), new PrinterInfo("Home_Laser", false)], printers);
        // a translated "no default" line has no name after a colon
        Assert.All(new CupsPrinting((f, a) => a[0] == "-e" ? (0, "A\n", "") : (0, "システムのデフォルトの送信先がありません\n", "")).Printers(),
                   p => Assert.False(p.IsDefault));
    }

    [Fact]
    public void A_job_goes_to_lp_with_every_option_as_its_own_argument()
    {
        var pdf = Pdf();
        var cups = new FakeCups();
        var result = new CupsPrinting(cups.Run).Print(pdf, new PrintOptions("Office", 2, "2-3", TwoSided: true, Grayscale: true, FitToPage: false));
        Assert.Equal(("Office", "Office-42", 4), (result.Printer, result.JobId, result.Pages));
        var (file, args) = cups.Calls.Single(c => c.File == "lp");
        Assert.Equal(["-d", "Office", "-n", "2", "-t", "doc.pdf", "-o", "collate=true", "-P", "2-3", "-o", "sides=two-sided-long-edge",
                      "-o", "print-color-mode=monochrome", "-o", "ColorModel=Gray", "-o", "print-scaling=none", "--", Path.GetFullPath(pdf)], args);

        cups.Calls.Clear();
        var all = new CupsPrinting(cups.Run).Print(pdf, new PrintOptions());   // the default printer, every page, fitted
        Assert.Equal(("Office", 3), (all.Printer, all.Pages));
        var lp = cups.Calls.Single(c => c.File == "lp").Args;
        Assert.DoesNotContain("-P", lp);
        Assert.Equal(["-d", "Office"], lp.Take(2));                          // the default printer, named
        Assert.Contains("print-scaling=fit", lp);
        Assert.Contains("sides=one-sided", lp);
    }

    [Fact]
    public void Jobs_that_cannot_print_are_refused_before_anything_is_sent()
    {
        var pdf = Pdf();
        var cups = new FakeCups();
        var printing = new CupsPrinting(cups.Run);
        Assert.Equal("Err_PrintCopies", Assert.Throws<PrintException>(() => printing.Print(pdf, new PrintOptions(Copies: 0))).Key);
        Assert.Throws<PageAssemblyException>(() => printing.Print(pdf, new PrintOptions(Pages: "7")));

        PdfSecurity.Protect(pdf, P("noprint.pdf"), null, "boss", new PdfPermissions(Print: false));
        Assert.Equal("Err_PrintNotAllowed", Assert.Throws<PrintException>(() => printing.Print(P("noprint.pdf"), new PrintOptions())).Key);
        PdfSecurity.Protect(pdf, P("locked.pdf"), "open", null, PdfPermissions.All);
        Assert.Equal("Err_PrintPassword", Assert.Throws<PrintException>(() => printing.Print(P("locked.pdf"), new PrintOptions())).Key);
        Assert.Equal("Err_PrintMissing", Assert.Throws<PrintException>(() => printing.Print(P("gone.pdf"), new PrintOptions())).Key);
        Assert.DoesNotContain(cups.Calls, c => c.File == "lp");

        var none = new CupsPrinting((f, a) => (0, "", ""));
        Assert.Equal("Ui_PrintNoPrinters", Assert.Throws<PrintException>(() => none.Print(pdf, new PrintOptions())).Key);

        cups.LpAnswer = (1, "", "lp: The printer or class does not exist.");
        var e = Assert.Throws<PrintException>(() => printing.Print(pdf, new PrintOptions("Nope")));
        Assert.Equal("Err_PrintFailed", e.Key);
        Assert.Contains("does not exist", e.Message);
    }

    [Fact]
    public void The_real_print_system_can_be_asked_for_its_printers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists("/usr/bin/lpstat"), "no CUPS here");
        var printers = new CupsPrinting().Printers();   // may be none, but never fails
        Assert.True(printers.Count(p => p.IsDefault) <= 1);
    }
}
