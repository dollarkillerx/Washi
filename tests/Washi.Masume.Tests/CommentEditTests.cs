// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Pages;

namespace Washi.Masume.Tests;

/// <summary>Changing comments already in a PDF: text, colour, place, removal with replies, and replies.</summary>
public sealed class CommentEditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-comment-edit-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A page with one comment of each kind Washi draws, and a circle written by another program (no /NM).</summary>
    private string Commented()
    {
        ToolsTests.MakeFlatForm(P("flat.pdf"));
        PdfComments.Add(P("flat.pdf"), P("c.pdf"),
        [
            new(0, CommentKind.Highlight, new PdfRect(40, 40, 200, 60), Lines: [new PdfRect(40, 40, 200, 60)]),
            new(0, CommentKind.Note, new PdfRect(300, 40, 318, 58), "Check this", Author: "Aoi"),
            new(0, CommentKind.TextBox, new PdfRect(300, 100, 460, 130), "Old words"),
            new(0, CommentKind.Pen, new PdfRect(50, 200, 90, 240), Strokes: [[(50, 200), (90, 240)]]),
            new(0, CommentKind.Box, new PdfRect(100, 300, 200, 360)),
        ]);
        using (var d = PdfReader.Open(P("c.pdf"), PdfDocumentOpenMode.Modify))
        {
            var circle = new PdfDictionary(d);
            d.Internals.AddObject(circle);
            circle.Elements.SetName("/Type", "/Annot");
            circle.Elements.SetName("/Subtype", "/Circle");
            circle.Elements["/Rect"] = new PdfArray(d, new PdfReal(400), new PdfReal(100), new PdfReal(450), new PdfReal(150));
            circle.Elements["/Contents"] = new PdfString("someone else's circle");
            d.Pages[0].Elements.GetArray("/Annots")!.Elements.Add(circle.Reference!);
            d.Save(P("c2.pdf"));
        }
        return P("c2.pdf");
    }

    [Fact]
    public void Comments_are_listed_with_ids_colours_and_what_can_change()
    {
        var list = PdfComments.List(Commented());
        Assert.Equal(6, list.Count);
        Assert.All(list.Take(5), c => Assert.StartsWith("washi-", c.Id));
        Assert.Equal("p1-6", list[5].Id);                               // no /NM: its page and place
        Assert.Equal("#FFE14D", list[0].Color);
        Assert.True(list[0].Redrawable && !list[0].Movable);             // a highlight stays on its text
        Assert.True(list[2].Resizable);
        Assert.False(list[5].Redrawable);
    }

    [Fact]
    public void Text_colour_and_place_change_and_the_appearance_is_redrawn()
    {
        var input = Commented();
        var c = PdfComments.List(input);
        var (changed, removed, replies) = PdfComments.Change(input, P("out.pdf"),
        [
            new(c[0].Id, Color: "#00FF00"),
            new(c[1].Id, Text: "Checked, thanks", Rect: new PdfRect(500, 40, 518, 58), Reply: "Agreed", Author: "Ren"),
            new(c[2].Id, Text: "New words here", Rect: new PdfRect(300, 100, 500, 160)),
            new(c[4].Id, Rect: new PdfRect(110, 320, 250, 380), Color: "#DA1E28"),
            new(c[5].Id, Text: "a new note on the circle"),
        ]);
        Assert.Equal((5, 0, 1), (changed, removed, replies));
        var after = PdfComments.List(P("out.pdf")).ToDictionary(x => x.Id);
        Assert.Equal("#00FF00", after[c[0].Id].Color);
        Assert.Equal("Checked, thanks", after[c[1].Id].Text);
        Assert.Equal(500, after[c[1].Id].Rect.X0, 1);
        Assert.Equal(("New words here", 200.0), (after[c[2].Id].Text, Math.Round(after[c[2].Id].Rect.Width)));
        Assert.Equal((110.0, 140.0), (Math.Round(after[c[4].Id].Rect.X0), Math.Round(after[c[4].Id].Rect.Width)));
        Assert.Equal("a new note on the circle", after[c[5].Id].Text);
        var reply = Assert.Single(after.Values, x => x.ReplyTo == c[1].Id);
        Assert.Equal(("Agreed", "Ren"), (reply.Text, reply.Author));

        // the text box shows its new words (drawn into its appearance)
        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        var box = d.Pages[0].Elements.GetArray("/Annots")!.Elements.Select(e => ((PdfSharp.Pdf.Advanced.PdfReference)e).Value).OfType<PdfDictionary>()
                   .Single(a => a.Elements.GetName("/Subtype") == "/FreeText");
        var ap = ((PdfSharp.Pdf.Advanced.PdfReference)box.Elements.GetDictionary("/AP")!.Elements["/N"]!).Value as PdfDictionary;
        Assert.Contains("<4E>", System.Text.Encoding.ASCII.GetString(ap!.Stream!.UnfilteredValue));   // "N" of "New"
    }

    [Fact]
    public void A_removed_comment_takes_its_replies_and_what_cannot_change_is_refused()
    {
        var input = Commented();
        var c = PdfComments.List(input);
        PdfComments.Change(input, P("replied.pdf"), [new(c[1].Id, Reply: "first"), new(c[1].Id, Reply: "second")]);
        var withReplies = PdfComments.List(P("replied.pdf"));
        Assert.Equal(2, withReplies.Count(x => x.ReplyTo == c[1].Id));

        var (_, removed, _) = PdfComments.Change(P("replied.pdf"), P("removed.pdf"), [new(c[1].Id, Delete: true), new(c[3].Id, Delete: true)]);
        Assert.Equal(4, removed);                                         // the note, its two replies, the pen
        var left = PdfComments.List(P("removed.pdf"));
        Assert.Equal(4, left.Count);
        Assert.DoesNotContain(left, x => x.Id == c[1].Id || x.ReplyTo == c[1].Id);

        Assert.Equal("Err_CommentNotRedrawable", Assert.Throws<PageAssemblyException>(() =>
            PdfComments.Change(input, P("x.pdf"), [new(c[5].Id, Color: "#000000")])).Key);
        Assert.Equal("Err_CommentNotMovable", Assert.Throws<PageAssemblyException>(() =>
            PdfComments.Change(input, P("y.pdf"), [new(c[0].Id, Rect: new PdfRect(0, 0, 10, 10))])).Key);
        Assert.Equal("Err_NoSuchComment", Assert.Throws<PageAssemblyException>(() =>
            PdfComments.Change(input, P("z.pdf"), [new("nope", Text: "x")])).Key);
        Assert.False(File.Exists(P("x.pdf")) || File.Exists(P("y.pdf")) || File.Exists(P("z.pdf")));
    }

    [Fact]
    public async Task Agents_change_comments_by_id()
    {
        var input = Commented();
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var list = await tools.Find("list_comments")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input }));
        var first = System.Text.Json.JsonDocument.Parse(list.Text).RootElement[1];
        Assert.Equal("Text", first.GetProperty("type").GetString());
        Assert.True(first.GetProperty("can_move").GetBoolean());
        var id = first.GetProperty("id").GetString();

        var r = await tools.Find("edit_comments")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input, changes = new object[] { new { id, text = "Done", reply = "OK by me", author = "Kai" } },
        }));
        Assert.False(r.IsError, r.Text);
        var after = PdfComments.List(P("c2_commented.pdf"));
        Assert.Equal("Done", after.Single(c => c.Id == id).Text);
        Assert.Equal("OK by me", after.Single(c => c.ReplyTo == id).Text);
        var bad = await tools.Find("edit_comments")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input, changes = new object[] { new { id = "nope", delete = true } }, output = "x.pdf",
        }));
        Assert.True(bad.IsError);
    }
}
