// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Pages;

namespace Washi.Masume.Tests;

/// <summary>Bookmarks: read the outline (with its destinations), edit it, write it back without losing what was not changed.</summary>
public sealed class BookmarkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-bookmarks-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>
    /// Four A4 pages and an outline written by hand: "Intro" (page 1) with "Part a" (page 2, 100 pt from the top,
    /// through a named destination) and "Part b" (page 3, a GoTo action), then a red bold "Website" that opens a URL.
    /// </summary>
    private string Book()
    {
        var doc = new PdfDocument();
        for (int i = 0; i < 4; i++) { var p = doc.AddPage(); p.Width = XUnit.FromPoint(595); p.Height = XUnit.FromPoint(842); }
        PdfDictionary Item(string title)
        {
            var d = new PdfDictionary(doc);
            doc.Internals.AddObject(d);
            d.Elements["/Title"] = new PdfString(title);
            return d;
        }
        void Link(PdfDictionary parent, params PdfDictionary[] kids)
        {
            parent.Elements["/First"] = kids[0].Reference;
            parent.Elements["/Last"] = kids[^1].Reference;
            for (int i = 0; i < kids.Length; i++)
            {
                kids[i].Elements["/Parent"] = parent.Reference;
                if (i > 0) kids[i].Elements["/Prev"] = kids[i - 1].Reference;
                if (i + 1 < kids.Length) kids[i].Elements["/Next"] = kids[i + 1].Reference;
            }
        }
        var root = new PdfDictionary(doc);
        doc.Internals.AddObject(root);
        var intro = Item("Intro");
        intro.Elements["/Dest"] = new PdfArray(doc, doc.Pages[0].Reference!, new PdfName("/Fit"));
        var a = Item("Part a");
        a.Elements["/Dest"] = new PdfString("part-a");
        var b = Item("Part b");
        var goTo = new PdfDictionary(doc);
        goTo.Elements.SetName("/S", "/GoTo");
        goTo.Elements["/D"] = new PdfArray(doc, doc.Pages[2].Reference!, new PdfName("/FitH"), new PdfReal(842));
        b.Elements["/A"] = goTo;
        var web = Item("Website");
        var uri = new PdfDictionary(doc);
        uri.Elements.SetName("/S", "/URI");
        uri.Elements["/URI"] = new PdfString("https://example.com/");
        web.Elements["/A"] = uri;
        web.Elements["/C"] = new PdfArray(doc, new PdfReal(1), new PdfReal(0), new PdfReal(0));
        web.Elements.SetInteger("/F", 2);
        Link(root, intro, web);
        Link(intro, a, b);
        intro.Elements.SetInteger("/Count", 2);
        root.Elements.SetInteger("/Count", 4);
        doc.Internals.Catalog.Elements["/Outlines"] = root.Reference;
        // the named destination, in a name tree
        var names = new PdfDictionary(doc);
        var tree = new PdfDictionary(doc);
        tree.Elements["/Names"] = new PdfArray(doc, new PdfString("part-a"),
            new PdfArray(doc, doc.Pages[1].Reference!, new PdfName("/XYZ"), new PdfReal(0), new PdfReal(742), PdfNull.Value));
        names.Elements["/Dests"] = tree;
        doc.Internals.Catalog.Elements["/Names"] = names;
        var path = P("book.pdf");
        doc.Save(path);
        return path;
    }

    [Fact]
    public void The_outline_is_read_with_pages_positions_and_nesting()
    {
        var outline = PdfBookmarks.Read(Book());
        Assert.Equal(["Intro", "Website"], outline.Select(b => b.Title));
        var intro = outline[0];
        Assert.Equal((0, (double?)null, true), (intro.Page, intro.Top, intro.Open));
        Assert.Equal(["Part a", "Part b"], intro.Children.Select(b => b.Title));
        Assert.Equal(1, intro.Children[0].Page);
        Assert.Equal(100, intro.Children[0].Top!.Value, 1);         // named destination, 742 from the bottom of 842
        Assert.Equal(2, intro.Children[1].Page);                      // a GoTo action
        Assert.Equal(0, intro.Children[1].Top!.Value, 1);
        Assert.Equal(-1, outline[1].Page);                            // a web link: not a page of this file
        Assert.Equal([0, 1, 2, 3], outline.SelectMany(b => b.Flatten()).Select(b => b.Source!.Value));
    }

    [Fact]
    public void An_edited_outline_is_written_and_what_was_not_changed_keeps_its_action_and_style()
    {
        var input = Book();
        var old = PdfBookmarks.Read(input);
        var intro = old[0];
        // rename Intro, drop Part a, move Part b up a level and to page 4, add a new one with a position
        var edited = new List<Bookmark>
        {
            intro with { Title = "Introduction", Children = [] },
            intro.Children[1] with { Page = 3, Top = 200 },
            new("Appendix", 3, 400, [Bookmark.Make("Sources", 3)]),
            old[1],
        };
        PdfBookmarks.Write(input, P("out.pdf"), edited);

        var back = PdfBookmarks.Read(P("out.pdf"));
        Assert.Equal(["Introduction", "Part b", "Appendix", "Website"], back.Select(b => b.Title));
        Assert.Equal((3, 200.0), (back[1].Page, Math.Round(back[1].Top!.Value)));
        Assert.Equal((3, 400.0), (back[2].Page, Math.Round(back[2].Top!.Value)));
        Assert.Equal("Sources", Assert.Single(back[2].Children).Title);

        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        var items = new List<PdfDictionary>();
        for (var i = (PdfDictionary?)((PdfReference)((PdfDictionary)((PdfReference)d.Internals.Catalog.Elements["/Outlines"]!).Value).Elements["/First"]!).Value;
             i is not null; i = (i.Elements["/Next"] as PdfReference)?.Value as PdfDictionary)
            items.Add(i);
        var web = items[^1];
        var action = (PdfDictionary)(web.Elements["/A"] is PdfReference r ? r.Value : web.Elements["/A"]!);
        Assert.Equal("https://example.com/", ((PdfString)action.Elements["/URI"]!).Value);   // the link still works
        Assert.Equal(2, web.Elements.GetInteger("/F"));                                     // still bold
        Assert.NotNull(web.Elements["/C"]);                                                 // still red
        Assert.Equal(4, d.PageCount);
    }

    [Fact]
    public void An_empty_outline_removes_the_bookmarks_and_bad_ones_are_refused()
    {
        var input = Book();
        PdfBookmarks.Write(input, P("none.pdf"), []);
        Assert.Empty(PdfBookmarks.Read(P("none.pdf")));

        Assert.Equal("Err_BookmarkPage", Assert.Throws<PageAssemblyException>(() =>
            PdfBookmarks.Write(input, P("x.pdf"), [Bookmark.Make("Too far", 9)])).Key);
        Assert.Equal("Err_BookmarkTitle", Assert.Throws<PageAssemblyException>(() =>
            PdfBookmarks.Write(input, P("y.pdf"), [Bookmark.Make("  ", 0)])).Key);
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() =>
            PdfBookmarks.Write(input, P("none.pdf"), [Bookmark.Make("A", 0)])).Key);
        Assert.False(File.Exists(P("x.pdf")) || File.Exists(P("y.pdf")));
    }

    [Fact]
    public void A_file_without_bookmarks_gets_its_first_ones()
    {
        var doc = new PdfDocument();
        doc.AddPage(); doc.AddPage();
        doc.Save(P("plain.pdf"));
        Assert.Empty(PdfBookmarks.Read(P("plain.pdf")));
        PdfBookmarks.Write(P("plain.pdf"), P("marked.pdf"), [Bookmark.Make("一章 はじめに", 0), Bookmark.Make("二章", 1)]);
        Assert.Equal(["一章 はじめに", "二章"], PdfBookmarks.Read(P("marked.pdf")).Select(b => b.Title));
    }

    [Fact]
    public void The_text_form_round_trips_and_a_link_matched_by_title_keeps_its_action()
    {
        var input = Book();
        var lines = PdfBookmarks.ToLines(PdfBookmarks.Read(input)).ToList();
        Assert.Equal(["Intro\t1", "  Part a\t2", "  Part b\t3", "Website\t-"], lines);
        var edited = lines.Append("Back cover\t4").Prepend("# my outline").ToList();
        var outline = PdfBookmarks.FromLines(edited);
        Assert.Null(outline[0].Source);                                   // from text: no source
        PdfBookmarks.Write(input, P("t.pdf"), outline);
        Assert.Equal(["Intro\t1", "  Part a\t2", "  Part b\t3", "Website\t-", "Back cover\t4"], PdfBookmarks.ToLines(PdfBookmarks.Read(P("t.pdf"))));
        using var d = PdfReader.Open(P("t.pdf"), PdfDocumentOpenMode.Import);
        Assert.Contains(d.Internals.GetAllObjects().OfType<PdfDictionary>(), o =>
            (o.Elements["/A"] is PdfReference r ? r.Value : o.Elements["/A"]) is PdfDictionary a && a.Elements.GetString("/URI") == "https://example.com/");

        Assert.Throws<PageAssemblyException>(() => PdfBookmarks.FromLines(["No page here"]));
        Assert.Throws<PageAssemblyException>(() => PdfBookmarks.FromLines(["Title\tx"]));
    }

    [Fact]
    public async Task Agents_list_and_set_bookmarks()
    {
        var input = Book();
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var list = await tools.Find("list_bookmarks")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input }));
        Assert.False(list.IsError, list.Text);
        var root = System.Text.Json.JsonDocument.Parse(list.Text).RootElement;
        Assert.Equal("Intro", root[0].GetProperty("title").GetString());
        Assert.Equal(2, root[0].GetProperty("children")[0].GetProperty("page").GetInt32());
        Assert.False(root[1].TryGetProperty("page", out _));             // a web link: no page

        var set = await tools.Find("set_bookmarks")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input,
            bookmarks = new object[] { new { title = "目次", page = 1, children = new object[] { new { title = "第1章", page = 2, top = 300 } } } },
        }));
        Assert.False(set.IsError, set.Text);
        var back = PdfBookmarks.Read(P("book_bookmarks.pdf"));
        Assert.Equal("第1章", Assert.Single(Assert.Single(back).Children).Title);
        Assert.Equal(300, back[0].Children[0].Top!.Value, 1);
        var bad = await tools.Find("set_bookmarks")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input, bookmarks = new object[] { new { title = "x", page = 99 } }, output = "x.pdf",
        }));
        Assert.True(bad.IsError);
    }

    [Fact]
    public void Titles_in_pdf_doc_encoding_read_as_a_reader_shows_them() =>
        Assert.Equal("What’s New • “Quoted” — done…", PdfBookmarks.Clean("What\u0090s New \u0080 \u008dQuoted\u008e \u0084 done\u0083\n"));
}
