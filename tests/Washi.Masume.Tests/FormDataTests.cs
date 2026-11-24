// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using PdfSharp.Pdf;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;

namespace Washi.Masume.Tests;

/// <summary>Form data out of and into a form: FDF, XFDF, CSV, JSON; one copy per row; many forms into one table.</summary>
public sealed class FormDataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-formdata-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A form with a text field, a dotted (hierarchical) name, a multi-line field, a check box and a radio group.</summary>
    private string Form()
    {
        if (File.Exists(P("form.pdf"))) return P("form.pdf");
        using (var d = new PdfDocument()) { d.AddPage(); d.Save(P("blank.pdf")); }
        AcroFormWriter.Write(P("blank.pdf"), P("flat-names.pdf"),
        [
            new FormField(0, FormFieldKind.Text, new PdfRect(40, 40, 260, 60), "氏名"),
            new FormField(0, FormFieldKind.Text, new PdfRect(40, 80, 260, 100), "申請者.電話"),
            new FormField(0, FormFieldKind.Text, new PdfRect(40, 120, 260, 180), "備考", Align: 0),
            new FormField(0, FormFieldKind.Checkbox, new PdfRect(40, 200, 52, 212), "同意"),
            new FormField(0, FormFieldKind.Radio, new PdfRect(40, 230, 52, 242), "有無", Export: "有"),
            new FormField(0, FormFieldKind.Radio, new PdfRect(80, 230, 92, 242), "有無", Export: "無"),
        ], FormScript.Japanese);
        // Washi writes no hierarchies (a dot becomes ・); other programs do: 申請者 with a kid 電話
        using (var d = PdfSharp.Pdf.IO.PdfReader.Open(P("flat-names.pdf"), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            var form = d.AcroForm!;
            var fields = (PdfArray)form.Elements.GetObject("/Fields")!;
            var kid = fields.Elements.Select(e => ((PdfSharp.Pdf.Advanced.PdfReference)e).Value).OfType<PdfDictionary>()
                            .First(f => f.Elements.GetString("/T") == "申請者・電話");
            var parent = new PdfDictionary(d);
            d.Internals.AddObject(parent);
            parent.Elements["/T"] = new PdfString("申請者", PdfStringEncoding.Unicode);
            parent.Elements["/Kids"] = new PdfArray(d, kid.Reference!);
            kid.Elements["/T"] = new PdfString("電話", PdfStringEncoding.Unicode);
            kid.Elements["/Parent"] = parent.Reference;
            fields.Elements.Remove(fields.Elements.First(e => ReferenceEquals(((PdfSharp.Pdf.Advanced.PdfReference)e).Value, kid)));
            fields.Elements.Add(parent.Reference!);
            d.Save(P("form.pdf"));
        }
        return P("form.pdf");
    }

    private string Filled()
    {
        if (!File.Exists(P("filled.pdf")))
            AcroFormFields.Fill(Form(), P("filled.pdf"), new Dictionary<string, string>
            {
                ["氏名"] = "山田 太郎 (ヤマダ)", ["申請者.電話"] = "03-1234-5678", ["備考"] = "一行目, \"引用\"\n二行目",
                ["同意"] = "true", ["有無"] = "無",
            });
        return P("filled.pdf");
    }

    private Dictionary<string, string> Fields(string pdf) => AcroFormFields.List(pdf).ToDictionary(f => f.Name, f => f.Value);

    [Theory]
    [InlineData("data.fdf")]
    [InlineData("data.xfdf")]
    [InlineData("data.csv")]
    [InlineData("data.json")]
    public void Values_go_out_to_a_data_file_and_back_into_a_blank_form(string file)
    {
        Assert.Equal(5, PdfFormData.Export(Filled(), P(file)));   // read back as written, or it throws
        var read = PdfFormData.Read(P(file)).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("山田 太郎 (ヤマダ)", read["氏名"]);
        Assert.Equal("03-1234-5678", read["申請者.電話"]);   // a dotted name survives FDF's and XFDF's tree of partial names

        var r = PdfFormData.Import(Form(), P(file), P("imported_" + file + ".pdf"));
        Assert.Equal(5, r.Filled);
        var back = Fields(P("imported_" + file + ".pdf"));
        var want = Fields(Filled());
        Assert.Equal(want["氏名"], back["氏名"]);
        Assert.Equal(want["申請者.電話"], back["申請者.電話"]);
        Assert.Equal(want["備考"].Replace("\r", "\n"), back["備考"].Replace("\r", "\n"));
        Assert.Equal(want["同意"], back["同意"]);
        Assert.Equal("無", back["有無"]);
    }

    [Fact]
    public void Fdf_and_xfdf_are_written_as_other_programs_expect_them()
    {
        PdfFormData.Export(Filled(), P("a.fdf"));
        var fdf = Encoding.Latin1.GetString(File.ReadAllBytes(P("a.fdf")));
        Assert.StartsWith("%FDF-1.2", fdf);
        var parentName = "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("申請者")) + ">";
        Assert.Contains("/T " + parentName + "/Kids[", fdf);   // a tree of partial names
        Assert.Contains("/V /1", fdf);   // the radio button's state name, not its label
        Assert.Contains("/F (filled.pdf)", fdf);

        PdfFormData.Export(Filled(), P("a.xfdf"));
        var x = System.Xml.Linq.XDocument.Load(P("a.xfdf"));
        Assert.Equal("http://ns.adobe.com/xfdf/", x.Root!.Name.NamespaceName);
        var parent = x.Descendants().Single(e => e.Name.LocalName == "field" && (string?)e.Attribute("name") == "申請者");
        Assert.Equal("電話", (string?)parent.Elements().Single().Attribute("name"));

        // a file written by another program: indirect fields, a name with an escape, a UTF-16 value
        var other = "%FDF-1.2\n1 0 obj\n<</FDF<</Fields[2 0 R 3 0 R]>>>>\nendobj\n2 0 obj\n<</T(\\346\\260\\217)/V<FEFF5C71753052A9>>>\nendobj\n"
                    + "3 0 obj\n<</T(同意)/V/Yes>>\nendobj\ntrailer\n<</Root 1 0 R>>\n%%EOF\n";
        File.WriteAllBytes(P("other.fdf"), Encoding.UTF8.GetBytes(other));
        var read = PdfFormData.Read(P("other.fdf"));
        Assert.Equal("山田助", read[0].Value);
        Assert.Equal(("同意", "Yes"), (read[1].Key, read[1].Value));
    }

    [Fact]
    public void A_table_fills_one_copy_per_row_and_many_forms_collect_into_one_table()
    {
        // a spreadsheet's CSV: quoted cells with commas and line breaks, a column the form does not have, an empty cell
        File.WriteAllText(P("people.csv"), "氏名,申請者.電話,備考,同意,有無,社員番号\r\n"
                                           + "山田 太郎,03-1111-2222,\"東京都, 千代田区\",yes,有,A001\r\n"
                                           + "佐藤 花子,,\"一行目\n二行目\",no,無,A002\r\n", new UTF8Encoding(true));
        var r = PdfFormData.FillMany(Form(), P("people.csv"), P("out"), nameColumn: "社員番号");
        Assert.Empty(r.UnknownColumns);   // the column that names the copies is not reported
        Assert.Equal(["A001.pdf", "A002.pdf"], r.Rows.Select(x => Path.GetFileName(x.Output)));
        var a = Fields(P("out/A001.pdf"));
        Assert.Equal(("山田 太郎", "03-1111-2222", "東京都, 千代田区", "有"), (a["氏名"], a["申請者.電話"], a["備考"], a["有無"]));
        var b = Fields(P("out/A002.pdf"));
        Assert.Equal("", b["申請者.電話"]);   // an empty cell leaves the field as the form has it
        Assert.Equal("Off", PdfFormData.Values(P("out/A002.pdf")).Single(v => v.Key == "同意").Value);

        // again into the same folder: the copies get new names, nothing is replaced
        var again = PdfFormData.FillMany(Form(), P("people.csv"), P("out"));
        Assert.Equal(["form_001.pdf", "form_002.pdf"], again.Rows.Select(x => Path.GetFileName(x.Output)));

        // collected: a row per form, the file first
        Assert.Equal(2, PdfFormData.Collect([P("out/A001.pdf"), P("out/A002.pdf")], P("summary.csv")));
        var table = PdfFormData.ReadCsv(P("summary.csv"));
        Assert.Equal(["file", "氏名", "備考", "同意", "有無", "申請者.電話"], table[0]);   // the form's order
        Assert.Equal(["A001.pdf", "山田 太郎"], table[1].Take(2));
        Assert.Equal("一行目\n二行目", table[2][2].Replace("\r", "\n"));
        Assert.Equal(0xEF, File.ReadAllBytes(P("summary.csv"))[0]);   // UTF-8 with its mark, for spreadsheets
    }

    [Fact]
    public void A_shift_jis_csv_from_a_japanese_spreadsheet_is_read_and_mistakes_are_refused()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(P("sjis.csv"), Encoding.GetEncoding(932).GetBytes("氏名,同意\r\n鈴木 一郎,はい\r\n"));
        Assert.Equal("鈴木 一郎", PdfFormData.Read(P("sjis.csv"))[0].Value);

        File.WriteAllText(P("wrong.csv"), "name,phone\r\nx,y\r\n");
        Assert.Equal("Err_FormDataNoMatch", Assert.Throws<PageAssemblyException>(() => PdfFormData.Import(Form(), P("wrong.csv"), P("x.pdf"))).Key);
        Assert.Equal("Err_FormDataNoColumn", Assert.Throws<PageAssemblyException>(() => PdfFormData.FillMany(Form(), P("sjis.csv"), P("o2"), "社員番号")).Key);
        Assert.Equal("Err_FormDataKind", Assert.Throws<PageAssemblyException>(() => PdfFormData.Export(Filled(), P("data.xlsx"))).Key);
        File.WriteAllText(P("taken.fdf"), "x");
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() => PdfFormData.Export(Filled(), P("taken.fdf"))).Key);
        using (var d = new PdfDocument()) { d.AddPage(); d.Save(P("plain.pdf")); }
        Assert.Equal("Err_FormDataNoFields", Assert.Throws<PageAssemblyException>(() => PdfFormData.Export(P("plain.pdf"), P("p.json"))).Key);
    }
}
