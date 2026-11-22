// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Forms;

namespace Washi.Masume.Tests;

/// <summary>Form rules: formats, ranges and calculations, written as Acrobat's field actions and applied when filling.</summary>
public sealed class FieldLogicTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-logic-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    // ------------------------------------------------------------------ values

    [Theory]
    [InlineData("1,234", "1234", "¥1,234")]
    [InlineData("１２，３４５円", "12345", "¥12,345")]
    [InlineData("¥ 98,000", "98000", "¥98,000")]
    [InlineData("-500", "-500", "-¥500")]
    [InlineData("1234.6", "1235", "¥1,235")]
    public void Numbers_are_stored_plain_and_shown_formatted(string typed, string stored, string shown)
    {
        var logic = new FieldLogic(FieldFormat.Number(0, "¥"));
        var (value, error, _) = logic.Accept(typed);
        Assert.Null(error);
        Assert.Equal(stored, value);
        Assert.Equal(shown, logic.Display(value!));
    }

    [Fact]
    public void Other_formats_accept_what_people_type()
    {
        var euro = new FieldLogic(new FieldFormat(FieldFormatKind.Number, 2, Separator: 2, Currency: " €", CurrencyFirst: false));
        Assert.Equal("1234.5", euro.Accept("1.234,5").Value);
        Assert.Equal("1.234,50 €", euro.Display("1234.5"));

        var percent = new FieldLogic(new FieldFormat(FieldFormatKind.Percent, 1));
        Assert.Equal("0.08", percent.Accept("8%").Value);
        Assert.Equal("0.08", percent.Accept("8").Value);
        Assert.Equal("8.0%", percent.Display("0.08"));

        var date = new FieldLogic(FieldFormat.Date("yyyy/mm/dd"));
        Assert.Equal("2026/11/22", date.Accept("2026-11-22").Value);
        Assert.Equal("2026/11/22", date.Accept("２０２６年１１月２２日").Value);
        Assert.Equal("2026/11/22", date.Accept("令和8年11月22日").Value);
        Assert.Equal("2019/05/01", date.Accept("令和元年5月1日").Value);
        Assert.Equal("Err_FieldNotDate", date.Accept("2026/02/30").Key);
        Assert.Equal("22 Nov 2026", FieldLogic.FormatDate(new DateOnly(2026, 11, 22), "d mmm yyyy"));

        var postal = new FieldLogic(FieldFormat.Mask("999-9999"));
        Assert.Equal("123-4567", postal.Accept("1234567").Value);
        Assert.Equal("123-4567", postal.Accept("１２３－４５６７").Value);
        Assert.Equal("Err_FieldNotMask", postal.Accept("12-34").Key);

        var age = new FieldLogic(FieldFormat.Number(0), new FieldRange(0, 120));
        Assert.Null(age.Accept("42").Error);
        Assert.Equal("Err_FieldRange", age.Accept("150").Key);
        Assert.Equal("Err_FieldNotNumber", age.Accept("forty").Key);
        Assert.Equal("", age.Accept("  ").Value);          // empty is always allowed
    }

    [Fact]
    public void Calculations_run_sources_first_and_refuse_cycles()
    {
        var calcs = new Dictionary<string, FieldCalc>
        {
            ["total"] = new(CalcOp.Sum, ["subtotal", "tax"]),
            ["subtotal"] = new(CalcOp.Sum, ["a", "b"]),
        };
        Assert.Equal(["subtotal", "total"], FieldLogic.CalculationOrder(calcs));
        Assert.Equal("30", FieldLogic.Calculate(new FieldCalc(CalcOp.Sum, ["a", "b", "c"]), new Dictionary<string, string> { ["a"] = "10", ["b"] = "20" }));
        Assert.Equal("15", FieldLogic.Calculate(new FieldCalc(CalcOp.Average, ["a", "b", "c"]), new Dictionary<string, string> { ["a"] = "10", ["b"] = "20" }));
        Assert.Equal("200", FieldLogic.Calculate(new FieldCalc(CalcOp.Product, ["a", "b"]), new Dictionary<string, string> { ["a"] = "10", ["b"] = "20" }));
        calcs["a"] = new(CalcOp.Sum, ["total"]);
        Assert.Throws<Washi.Core.Pages.PageAssemblyException>(() => FieldLogic.CalculationOrder(calcs));
    }

    // ------------------------------------------------------------------ in a PDF

    /// <summary>An invoice: two amounts, their total, a tax rate, a date and a postal code, written by Washi.</summary>
    private string Invoice()
    {
        ToolsTests.MakeFlatForm(P("flat.pdf"));
        FormField F(string name, double y, FieldLogic? logic) =>
            new(0, FormFieldKind.Amount, new PdfRect(40, y, 300, y + 20), name, Align: 2, Logic: logic);
        var yen = FieldFormat.Number(0, "¥");
        AcroFormWriter.Write(P("flat.pdf"), P("invoice.pdf"),
        [
            F("item1", 40, new(yen, new FieldRange(0, null))),
            F("item2", 70, new(yen, new FieldRange(0, null))),
            F("total", 100, new(yen, Calc: new FieldCalc(CalcOp.Sum, ["item1", "item2"]))),
            F("rate", 130, new(new FieldFormat(FieldFormatKind.Percent, 0))),
            F("date", 160, new(FieldFormat.Date("yyyy/mm/dd"))),
            F("postal", 190, new(FieldFormat.Mask("999-9999"))),
            F("note", 220, null),
        ], FormScript.Japanese);
        return P("invoice.pdf");
    }

    [Fact]
    public void Rules_are_written_as_acrobat_field_actions_and_read_back()
    {
        var fields = AcroFormFields.List(Invoice()).ToDictionary(f => f.Name);
        Assert.Equal(FieldFormat.Number(0, "¥"), fields["item1"].Logic!.Format);
        Assert.Equal(new FieldRange(0, null), fields["item1"].Logic!.Range);
        Assert.Equal(CalcOp.Sum, fields["total"].Logic!.Calc!.Op);
        Assert.Equal(["item1", "item2"], fields["total"].Logic!.Calc!.Sources);
        Assert.Equal(FieldFormatKind.Percent, fields["rate"].Logic!.Format!.Kind);
        Assert.Equal("yyyy/mm/dd", fields["date"].Logic!.Format!.Pattern);
        Assert.Equal("999-9999", fields["postal"].Logic!.Format!.Pattern);
        Assert.Null(fields["note"].Logic);

        using var d = PdfReader.Open(P("invoice.pdf"), PdfDocumentOpenMode.Import);
        var form = d.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;
        Assert.Single(form.Elements.GetArray("/CO")!.Elements);          // the total is in the calculation order
        var js = string.Join("\n", d.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Select(o => o.Elements.GetDictionary("/AA")).OfType<PdfDictionary>()
            .SelectMany(aa => aa.Elements.Values.OfType<PdfDictionary>()).Select(a => a.Elements.GetString("/JS")));
        Assert.Contains("AFNumber_Format(0, 0, 0, 0, \"¥\", true);", js);
        Assert.Contains("AFSimple_Calculate(\"SUM\", new Array (\"item1\", \"item2\"));", js);
        Assert.Contains("AFRange_Validate(true, 0, false, 0);", js);
        Assert.Contains("AFSpecial_KeystrokeEx(\"999-9999\");", js);
    }

    [Fact]
    public void Filling_checks_values_formats_them_and_works_out_the_total()
    {
        var r = AcroFormFields.Fill(Invoice(), P("filled.pdf"), new Dictionary<string, string>
        {
            ["item1"] = "1,200", ["item2"] = "３００", ["rate"] = "10%", ["date"] = "令和8年11月22日", ["postal"] = "1500001",
            ["total"] = "999",                 // calculated: not taken
        });
        var fields = AcroFormFields.List(P("filled.pdf")).ToDictionary(f => f.Name);
        Assert.Equal(("1200", "¥1,200"), (fields["item1"].Value, fields["item1"].Shown));
        Assert.Equal(("1500", "¥1,500"), (fields["total"].Value, fields["total"].Shown));
        Assert.Equal(("0.1", "10%"), (fields["rate"].Value, fields["rate"].Shown));
        Assert.Equal("2026/11/22", fields["date"].Value);
        Assert.Equal("150-0001", fields["postal"].Value);
        Assert.Contains(r.Notes, n => n.StartsWith("total: calculated from"));
        Assert.Contains(r.Notes, n => n.StartsWith("total: calculated as ¥1,500"));

        // the appearance shows the formatted value
        using (var d = PdfReader.Open(P("filled.pdf"), PdfDocumentOpenMode.Import))
        {
            var all = string.Join("\n", d.Internals.GetAllObjects().OfType<PdfDictionary>().Where(o => o.Stream is not null)
                .Select(o => System.Text.Encoding.Latin1.GetString(o.Stream!.UnfilteredValue)));
            Assert.Contains("/Tx BMC", all);
        }

        // bad values are refused with the reason; the total follows a later change
        var r2 = AcroFormFields.Fill(P("filled.pdf"), P("refill.pdf"), new Dictionary<string, string>
        {
            ["item1"] = "-5", ["date"] = "someday", ["item2"] = "800",
        });
        Assert.Contains(r2.Notes, n => n.StartsWith("item1:") && n.Contains("outside"));
        Assert.Contains(r2.Notes, n => n.StartsWith("date:") && n.Contains("not a date"));
        var again = AcroFormFields.List(P("refill.pdf")).ToDictionary(f => f.Name);
        Assert.Equal("1200", again["item1"].Value);             // kept
        Assert.Equal("2000", again["total"].Value);             // 1200 + 800
    }

    [Fact]
    public void Scripts_written_by_acrobat_are_understood()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        var field = new PdfDictionary(doc);
        var aa = new PdfDictionary(doc);
        PdfDictionary Js(string s)
        {
            var a = new PdfDictionary(doc);
            a.Elements.SetName("/S", "/JavaScript");
            a.Elements["/JS"] = new PdfString(s);
            return a;
        }
        aa.Elements["/F"] = Js("AFNumber_Format(2, 1, 0, 0, \"$\", true);");
        aa.Elements["/K"] = Js("AFNumber_Keystroke(2, 1, 0, 0, \"$\", true);");
        aa.Elements["/V"] = Js("AFRange_Validate(false, 0, true, 1000);");
        aa.Elements["/C"] = Js("AFSimple_Calculate('AVG', new Array ('Line.1', \"Line, 2\"));");
        field.Elements["/AA"] = aa;
        var logic = FieldScripts.Read(field)!;
        Assert.Equal(new FieldFormat(FieldFormatKind.Number, 2, 1, "$"), logic.Format);
        Assert.Equal(new FieldRange(null, 1000), logic.Range);
        Assert.Equal(CalcOp.Average, logic.Calc!.Op);
        Assert.Equal(["Line.1", "Line, 2"], logic.Calc.Sources);

        aa.Elements["/F"] = Js("AFSpecial_Format(2);");
        aa.Elements.Remove("/K");
        Assert.Equal("(999) 999-9999", FieldScripts.Read(field)!.Format!.Pattern);
    }

    [Fact]
    public void A_calculation_that_loops_is_reported_and_left_out_of_the_order()
    {
        ToolsTests.MakeFlatForm(P("flat.pdf"));
        var r = AcroFormWriter.Write(P("flat.pdf"), P("loop.pdf"),
        [
            new(0, FormFieldKind.Amount, new PdfRect(40, 40, 200, 60), "a", Logic: new(Calc: new FieldCalc(CalcOp.Sum, ["b"]))),
            new(0, FormFieldKind.Amount, new PdfRect(40, 70, 200, 90), "b", Logic: new(Calc: new FieldCalc(CalcOp.Sum, ["a", "ghost"]))),
        ], FormScript.Latin);
        Assert.Contains(r.Warnings, w => w.Contains("ghost"));
        Assert.Contains(r.Warnings, w => w.Contains("depends on itself"));
    }
}
