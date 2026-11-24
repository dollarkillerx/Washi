// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Pages;

/// <summary>A layer (optional content group): <paramref name="Id"/> tells layers with the same name apart.</summary>
/// <param name="Visible">Shown when the file opens (in the default configuration).</param>
/// <param name="Locked">The file asks viewers not to let it be switched.</param>
/// <param name="Pages">The pages that draw on it (0-based).</param>
public sealed record LayerInfo(int Id, string Name, bool Visible, bool Locked, IReadOnlyList<int> Pages);

/// <summary>
/// Layers (optional content, PDF 32000 8.11): lists them, and writes a copy in which chosen layers are shown or hidden
/// when the file opens. Viewers, Washi's included, draw and print what the default configuration shows.
/// </summary>
public static class PdfLayers
{
    public static IReadOnlyList<LayerInfo> List(string path)
    {
        using var d = Open(path);
        return List(d);
    }

    internal static IReadOnlyList<LayerInfo> List(PdfDocument d)
    {
        if (Deref(d.Internals.Catalog.Elements["/OCProperties"]) is not PdfDictionary props || Deref(props.Elements["/OCGs"]) is not PdfArray ocgs)
            return [];
        var config = Deref(props.Elements["/D"]) as PdfDictionary;
        bool baseOff = config is not null && Deref(config.Elements["/BaseState"]) is PdfName { Value: "/OFF" };
        var on = Numbers(config, "/ON");
        var off = Numbers(config, "/OFF");
        var locked = Numbers(config, "/Locked");
        var pages = Usage(d);
        var list = new List<LayerInfo>();
        foreach (var e in ocgs.Elements)
        {
            if (e is not PdfReference r || r.Value is not PdfDictionary g) continue;
            int id = r.ObjectNumber;
            var name = (Deref(g.Elements["/Name"]) as PdfString)?.Value ?? $"Layer {id}";
            bool visible = baseOff ? on.Contains(id) : !off.Contains(id);
            list.Add(new LayerInfo(id, name, visible, locked.Contains(id), pages.TryGetValue(id, out var p) ? [.. p.Order()] : []));
        }
        return list;
    }

    private static HashSet<int> Numbers(PdfDictionary? d, string key) =>
        d is not null && Deref(d.Elements[key]) is PdfArray a ? [.. a.Elements.OfType<PdfReference>().Select(r => r.ObjectNumber)] : [];

    /// <summary>Which pages use each layer: in their resources' marked-content properties, and on their images and forms.</summary>
    private static Dictionary<int, HashSet<int>> Usage(PdfDocument d)
    {
        var map = new Dictionary<int, HashSet<int>>();
        for (int p = 0; p < d.PageCount; p++)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            Resources(Deref(d.Pages[p].Elements["/Resources"]) as PdfDictionary, p, 0);
            if (Deref(d.Pages[p].Elements["/Annots"]) is PdfArray annots)
                foreach (var a in annots.Elements.Select(Deref).OfType<PdfDictionary>()) Mark(a.Elements["/OC"], p);

            void Resources(PdfDictionary? res, int page, int depth)
            {
                if (res is null || depth > 8 || !seen.Add(res)) return;
                if (Deref(res.Elements["/Properties"]) is PdfDictionary props)
                    foreach (var k in props.Elements.Keys) Mark(props.Elements[k], page);
                if (Deref(res.Elements["/XObject"]) is PdfDictionary xs)
                    foreach (var k in xs.Elements.Keys)
                        if (Deref(xs.Elements[k]) is PdfDictionary x)
                        {
                            Mark(x.Elements["/OC"], page);
                            Resources(Deref(x.Elements["/Resources"]) as PdfDictionary, page, depth + 1);
                        }
            }
        }
        return map;

        void Mark(PdfItem? item, int page)
        {
            if (item is not PdfReference r || r.Value is not PdfDictionary oc) return;
            if (Deref(oc.Elements["/Type"]) is PdfName { Value: "/OCMD" })
            {
                var groups = Deref(oc.Elements["/OCGs"]);
                foreach (var g in groups is PdfArray a ? a.Elements.OfType<PdfReference>() : groups is null ? [] : [(PdfReference)oc.Elements["/OCGs"]!])
                    Add(g.ObjectNumber, page);
            }
            else Add(r.ObjectNumber, page);
        }

        void Add(int id, int page)
        {
            if (!map.TryGetValue(id, out var set)) map[id] = set = [];
            set.Add(page);
        }
    }

    /// <summary>
    /// Writes <paramref name="output"/> with the layers in <paramref name="show"/> shown and those in
    /// <paramref name="hide"/> hidden when it opens. Layers are named by name or by id; a name shared by several layers
    /// changes all of them. The copy is read back to check it.
    /// </summary>
    public static IReadOnlyList<LayerInfo> SetVisible(string input, string output, IReadOnlyCollection<string> show, IReadOnlyCollection<string> hide)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        if (show.Count == 0 && hide.Count == 0) throw new PageAssemblyException("name the layers to show or hide", "Err_LayerNothing");
        if (show.Intersect(hide).FirstOrDefault() is { } both) throw new PageAssemblyException($"'{both}' cannot be both shown and hidden", "Err_LayerBoth", both);
        bool[] want;   // by place in the list of layers (PDFsharp numbers objects anew when it saves)
        using (var d = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            var layers = List(d);
            if (layers.Count == 0) throw new PageAssemblyException("the file has no layers", "Err_LayersNone");
            want = [.. layers.Select(l => l.Visible)];
            foreach (var (names, visible) in new[] { (show, true), (hide, false) })
                foreach (var n in names)
                {
                    var hit = Enumerable.Range(0, layers.Count)
                        .Where(i => layers[i].Name == n || layers[i].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == n).ToList();
                    if (hit.Count == 0) throw new PageAssemblyException($"there is no layer '{n}'", "Err_LayerNone", n);
                    foreach (var i in hit) want[i] = visible;
                }

            var props = (PdfDictionary)Deref(d.Internals.Catalog.Elements["/OCProperties"])!;
            var ocgs = (PdfArray)Deref(props.Elements["/OCGs"])!;
            var config = Deref(props.Elements["/D"]) as PdfDictionary;
            if (config is null)
            {
                config = new PdfDictionary(d);
                props.Elements["/D"] = config;
            }
            var onArr = new PdfArray(d);
            var offArr = new PdfArray(d);
            int at = 0;
            foreach (var e in ocgs.Elements)
                if (e is PdfReference { Value: PdfDictionary } r) (want[at++] ? onArr : offArr).Elements.Add(r);
            config.Elements.Remove("/BaseState");   // ON by default; everything is listed anyway
            config.Elements["/ON"] = onArr;
            config.Elements["/OFF"] = offArr;
            d.Save(output);
        }

        var back = List(output);
        if (back.Count != want.Length || back.Where((l, i) => l.Visible != want[i]).Any())
        {
            File.Delete(output);
            throw new PageAssemblyException("the layers did not read back as set; the copy was not kept", "Err_LayerCheck");
        }
        return back;
    }

    private static PdfDocument Open(string path)
    {
        try { return PdfReader.Open(path, PdfPasswords.For(path) ?? "", PdfDocumentOpenMode.Import); }
        catch (PdfReaderException e) when (PdfSecurity.IsPasswordProblem(e))
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)} is protected with a password", "Err_NeedsPassword", Path.GetFileName(path));
        }
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
