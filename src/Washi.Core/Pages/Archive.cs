// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Fonts;
using Washi.Core.Forms;
using Washi.Core.Pdfium;
using Washi.Core.Signatures;

namespace Washi.Core.Pages;

/// <summary>
/// Something between a file and PDF/A-2b (or, after converting, something Washi changed to get there): what it is (a
/// key such as <c>Fonts</c>), how many, and details (font names, page numbers…).
/// </summary>
public sealed record ArchiveItem(string Kind, int Count, IReadOnlyList<string> Details);

/// <summary>What converting did: the changes, by kind, and the file's PDF/A part and level.</summary>
public sealed record ArchiveResult(IReadOnlyList<ArchiveItem> Changes, string Conformance);

/// <summary>
/// PDF/A-2b, the archive form of PDF (ISO 19005-2, level B: the pages look the same in years to come). <see cref="Check"/>
/// lists what stands in a file's way; <see cref="Convert"/> writes a copy that conforms: every font embedded (one that
/// is only named is replaced by a font of this computer, each character staying where it was), colours tied to sRGB,
/// no scripts or actions that run, no attachments, comments and fields that carry their own appearance, and XMP
/// metadata that says it is PDF/A and agrees with the document properties. The copy is checked again, rendered next to
/// the original and its text read back.
/// </summary>
public static partial class PdfArchive
{
    private const int FlagInvisible = 1, FlagHidden = 2, FlagPrint = 4, FlagNoView = 32, FlagToggleNoView = 256;

    private static readonly HashSet<string> ForbiddenActions = new(StringComparer.Ordinal)
    {
        "/Launch", "/Sound", "/Movie", "/ResetForm", "/ImportData", "/Hide", "/SetOCGState", "/Rendition", "/Trans", "/GoTo3DView",
        "/JavaScript", "/RichMediaExecute", "/GoToE",
    };

    private static readonly HashSet<string> AllowedNamedActions = new(StringComparer.Ordinal) { "/NextPage", "/PrevPage", "/FirstPage", "/LastPage" };

    private static readonly HashSet<string> ForbiddenAnnotations = new(StringComparer.Ordinal)
    {
        "/3D", "/Sound", "/Screen", "/Movie", "/RichMedia", "/FileAttachment",
    };

    /// <summary>What stands between the file and PDF/A-2b, by kind; empty when Washi finds nothing (the rules it checks).</summary>
    public static IReadOnlyList<ArchiveItem> Check(string path)
    {
        var items = new List<ArchiveItem>();
        var bytes = File.ReadAllBytes(path);
        var header = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 16));
        if (!HeaderRx().IsMatch(header)) items.Add(new("Version", 1, [header.Split('\n', '\r')[0]]));
        if (Washi.Core.Pages.PdfSecurity.Inspect(path, PdfPasswords.For(path)).Encrypted) items.Add(new("Encrypted", 1, []));
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        items.AddRange(Inspect(doc, fix: false).Items);
        var meta = MetadataProblems(doc);
        if (meta.Count > 0) items.Add(new("Metadata", meta.Count, meta));
        return items;
    }

    /// <param name="flatten">Flatten form fields and comments first (<see cref="PdfFlattener"/>, checked on its own).</param>
    public static ArchiveResult Convert(string input, string output, bool flatten = false)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        if (flatten)
        {
            var flat = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $".washi-pdfa-flat-{Guid.NewGuid():N}.pdf");
            try
            {
                FlattenResult? f = null;
                if (PdfPasswords.For(input) is { } pw) PdfPasswords.Remember(flat, pw);   // the flattened copy keeps the protection
                try { f = PdfFlattener.Flatten(input, flat); }
                catch (PageAssemblyException e) when (e.Key == "Err_FlattenNone") { }
                if (f is null) return Convert(input, output);
                var r = Convert(flat, output);
                return r with { Changes = [new ArchiveItem("Flattened", f.Fields + f.Comments, []), .. r.Changes] };
            }
            finally
            {
                if (File.Exists(flat)) File.Delete(flat);
                PdfPasswords.Forget(flat);
            }
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var tmp1 = Path.Combine(dir, $".washi-pdfa-{Guid.NewGuid():N}.pdf");
        var tmp2 = Path.Combine(dir, $".washi-pdfa-{Guid.NewGuid():N}.pdf");
        try
        {
            List<ArchiveItem> changes;
            List<string> replacedFonts;
            using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
            {
                if (Signed(doc)) throw new PageAssemblyException("the file is signed: making it PDF/A would break the signatures", "Err_PdfaSigned");
                var (found, fonts) = Inspect(doc, fix: true);
                changes = found;
                replacedFonts = fonts;
                if (Washi.Core.Pages.PdfSecurity.Inspect(input, PdfPasswords.For(input)).Encrypted)
                {
                    doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
                    changes.Add(new("Encrypted", 1, []));
                }
                if (doc.Version != 17) doc.Version = 17;
                doc.Info.Elements.Remove("/Trapped");
                doc.Info.Elements.SetString("/ModDate", PdfDigitalSigner.PdfDate(DateTimeOffset.Now));
                SubsetFont.FinishAll(doc);
                doc.Options.CompressContentStreams = true;
                doc.Save(tmp1);
            }
            // PDFsharp writes every object on its own: packed into object streams again, as a compressed file has them
            if (ObjectStreams.Pack(File.ReadAllBytes(tmp1)) is { } packed) File.WriteAllBytes(tmp1, packed);
            File.WriteAllBytes(tmp2, WithMetadata(tmp1));
            changes.Add(new("Metadata", 1, []));
            Verify(input, tmp2, replacedFonts);
            File.Move(tmp2, output);
            return new ArchiveResult(changes, "PDF/A-2b");
        }
        finally
        {
            if (File.Exists(tmp1)) File.Delete(tmp1);
            if (File.Exists(tmp2)) File.Delete(tmp2);
        }
    }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try { return PdfReader.Open(path, PdfPasswords.For(path) ?? "", mode); }
        catch (PdfReaderException) when (mode == PdfDocumentOpenMode.Import)
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)} is protected with a password", "Err_NeedsPassword", Path.GetFileName(path));
        }
    }

    private static bool Signed(PdfDocument doc) =>
        AcroFormFields.Terminals(doc).Any(t => AcroFormFields.Inherited(t.Field, "/FT") is PdfName { Value: "/Sig" } && AcroFormFields.Inherited(t.Field, "/V") is not null);

    // ------------------------------------------------------------------ the rules (found, and fixed when fix is true)

    private sealed class Tally
    {
        private readonly Dictionary<string, (int Count, List<string> Details)> _d = new(StringComparer.Ordinal);
        public void Add(string kind, string? detail = null)
        {
            if (!_d.TryGetValue(kind, out var v)) v = (0, []);
            if (detail is not null && !v.Details.Contains(detail) && v.Details.Count < 20) v.Details.Add(detail);
            _d[kind] = (v.Count + 1, v.Details);
        }
        public List<ArchiveItem> Items => [.. _d.Select(kv => new ArchiveItem(kv.Key, kv.Value.Count, kv.Value.Details))];
    }

    private static (List<ArchiveItem> Items, List<string> Fonts) Inspect(PdfDocument doc, bool fix)
    {
        var t = new Tally();
        var catalog = doc.Internals.Catalog;
        var form = Deref(catalog.Elements["/AcroForm"]) as PdfDictionary;
        var problems = new List<string>();

        // document-level things that run or point outside
        if (Deref(catalog.Elements["/Names"]) is PdfDictionary names)
        {
            if (names.Elements.ContainsKey("/JavaScript")) { t.Add("Scripts", "JavaScript"); if (fix) names.Elements.Remove("/JavaScript"); }
            if (Deref(names.Elements["/EmbeddedFiles"]) is PdfDictionary files)
            {
                foreach (var n in EmbeddedNames(files)) t.Add("Attachments", n);
                if (fix) names.Elements.Remove("/EmbeddedFiles");
            }
        }
        foreach (var key in new[] { "/Collection", "/Requirements", "/NeedsRendering" })
            if (catalog.Elements.ContainsKey(key)) { t.Add(key == "/Collection" ? "Attachments" : "Other", key.TrimStart('/')); if (fix) catalog.Elements.Remove(key); }
        if (form is not null && form.Elements.ContainsKey("/XFA")) { t.Add("Xfa"); if (fix) form.Elements.Remove("/XFA"); }
        // Reader's usage rights (a signature that unlocks features in Adobe Reader; no longer valid once the file changes)
        if (Deref(catalog.Elements["/Perms"]) is PdfDictionary perms)
            foreach (var key in new[] { "/UR3", "/UR" })
                if (perms.Elements.ContainsKey(key))
                {
                    t.Add("Other", "UsageRights");
                    if (fix) { perms.Elements.Remove(key); if (perms.Elements.Count == 0) catalog.Elements.Remove("/Perms"); }
                }

        // every dictionary: actions, AA, images, graphics states, streams from outside, optional content
        foreach (var d in AllDictionaries(doc))
        {
            if (d.Elements.ContainsKey("/AA")) { t.Add("Actions", "AA"); if (fix) d.Elements.Remove("/AA"); }
            // numbers PDF/A does not allow (beyond 32-bit integers)
            foreach (var key in d.Elements.Keys.ToList())
                if (d.Elements[key] is PdfLongInteger { Value: > int.MaxValue or < int.MinValue } big)
                {
                    t.Add("Other", "number");
                    if (fix) d.Elements[key] = new PdfInteger(big.Value > 0 ? int.MaxValue : int.MinValue);
                }
            // a font's list of its glyphs (CIDSet, CharSet) must be exact when present; PDF/A-2 does not need it, so a copy
            // leaves it out rather than trust the one another program wrote
            if (fix && NameOf(d, "/Type") == "/FontDescriptor")
                foreach (var key in new[] { "/CIDSet", "/CharSet" })
                    if (d.Elements.ContainsKey(key)) { t.Add("Other", key.TrimStart('/')); d.Elements.Remove(key); }
            foreach (var key in new[] { "/A", "/OpenAction", "/Next" })
                if (Deref(d.Elements[key]) is PdfDictionary act && Forbidden(act) is { } what)
                {
                    t.Add(what == "/JavaScript" ? "Scripts" : "Actions", what.TrimStart('/'));
                    if (fix) d.Elements.Remove(key);
                }
                else if (Deref(d.Elements[key]) is PdfArray next && key == "/Next" && next.Elements.Select(Deref).OfType<PdfDictionary>().Any(a => Forbidden(a) is not null))
                {
                    t.Add("Actions", "Next");
                    if (fix) d.Elements.Remove(key);
                }
            var subtype = NameOf(d, "/Subtype");
            if (subtype == "/Image")
            {
                if (d.Elements.GetBoolean("/Interpolate")) { t.Add("Images", "Interpolate"); if (fix) d.Elements.Remove("/Interpolate"); }
                foreach (var key in new[] { "/Alternates", "/OPI" })
                    if (d.Elements.ContainsKey(key)) { t.Add("Images", key.TrimStart('/')); if (fix) d.Elements.Remove(key); }
            }
            if (subtype == "/Form")
                foreach (var key in new[] { "/Ref", "/OPI", "/PS" })
                    if (d.Elements.ContainsKey(key)) { t.Add("External", key.TrimStart('/')); if (fix) d.Elements.Remove(key); }
            if (subtype == "/PS") { t.Add("External", "PostScript"); problems.Add("PostScript"); }
            if (NameOf(d, "/Type") == "/ExtGState" || d.Elements.ContainsKey("/TR") || d.Elements.ContainsKey("/TR2") && NameOf(d, "/Type") == "/ExtGState")
            {
                if (d.Elements.ContainsKey("/TR")) { t.Add("Graphics", "TR"); if (fix) d.Elements.Remove("/TR"); }
                if (d.Elements["/TR2"] is { } tr2 && Deref(tr2) is not PdfName { Value: "/Default" }) { t.Add("Graphics", "TR2"); if (fix) d.Elements.SetName("/TR2", "/Default"); }
                if (d.Elements.ContainsKey("/HTP")) { t.Add("Graphics", "HTP"); if (fix) d.Elements.Remove("/HTP"); }
            }
            if (d.Stream is not null)
            {
                if (d.Elements.ContainsKey("/F") && NameOf(d, "/Type") != "/Filespec" && subtype != "/Image" || d.Elements.ContainsKey("/FFilter"))
                {
                    t.Add("External", "F");
                    problems.Add("external stream");
                }
                if (Filters(d).Contains("/LZWDecode"))
                {
                    t.Add("Lzw");
                    if (fix)
                    {
                        try { ContentRedactor.SetStream(d, d.Stream.UnfilteredValue); }
                        catch (Exception) { problems.Add("LZW stream"); }
                    }
                }
            }
        }
        if (Deref(catalog.Elements["/OCProperties"]) is PdfDictionary ocp)
        {
            var configs = new List<PdfDictionary>();
            if (Deref(ocp.Elements["/D"]) is PdfDictionary dc) configs.Add(dc);
            if (Deref(ocp.Elements["/Configs"]) is PdfArray more) configs.AddRange(more.Elements.Select(Deref).OfType<PdfDictionary>());
            int i = 0;
            foreach (var c in configs)
            {
                i++;
                if (!c.Elements.ContainsKey("/Name")) { t.Add("Layers", "Name"); if (fix) c.Elements.SetString("/Name", i == 1 ? "Default" : $"Configuration {i}"); }
                if (c.Elements.ContainsKey("/AS")) { t.Add("Layers", "AS"); if (fix) c.Elements.Remove("/AS"); }
            }
        }

        // form fields: their own appearance, no viewer-drawn values
        if (form is not null)
        {
            bool need = form.Elements.GetBoolean("/NeedAppearances");
            var terminals = AcroFormFields.Terminals(doc).ToList();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, name, widgets) in terminals)
            {
                if (field.Elements.ContainsKey("/A")) { t.Add("Actions", "field"); if (fix) field.Elements.Remove("/A"); }
                var ft = AcroFormFields.Inherited(field, "/FT") is PdfName n ? n.Value : "";
                bool missing = widgets.Any(w => Deref(w.Elements["/AP"]) is not PdfDictionary);
                if (!need && !missing) continue;
                var value = AcroFormFields.Inherited(field, "/V") switch { PdfString s => s.Value, PdfStringObject so => so.Value, _ => null };
                if (ft is "/Tx" or "/Ch" && !string.IsNullOrEmpty(value)) { t.Add("Fields", name); values[name] = value; }
                else if (missing)
                {
                    // an empty field, or a box in a state with no look: a viewer shows nothing there, and so does an empty appearance
                    t.Add("Fields", name);
                    if (fix)
                        foreach (var w in widgets.Where(w => Deref(w.Elements["/AP"]) is not PdfDictionary))
                            EmptyAppearance(doc, w, ft == "/Btn");
                }
            }
            if (need && values.Count == 0) t.Add("Fields", "NeedAppearances");
            if (fix)
            {
                if (values.Count > 0)
                {
                    foreach (var (field, name, _) in terminals.Where(x => values.ContainsKey(x.Name)))
                        if (AcroFormFields.Inherited(field, "/Ff") is PdfInteger { Value: var ff } && (ff & 1) != 0)
                            field.Elements.SetInteger("/Ff", ff & ~1);   // filled so it gets drawn; set back below
                    var readOnly = terminals.Where(x => values.ContainsKey(x.Name) && AcroFormFields.Inherited(x.Field, "/Ff") is PdfInteger { Value: var f } && (f & 1) != 0).ToList();
                    AcroFormFields.Fill(doc, values);
                    foreach (var (field, name, widgets) in terminals.Where(x => values.ContainsKey(x.Name)))
                        if (widgets.Any(w => Deref(w.Elements["/AP"]) is not PdfDictionary)) problems.Add($"field '{name}' cannot be drawn by Washi");
                }
                form.Elements.Remove("/NeedAppearances");
            }
        }

        // annotations: shown (and printed) as they look, with their own appearance; none that play or carry files
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            if (Deref(page.Elements["/Annots"]) is not PdfArray annots) continue;
            var keep = new List<PdfItem>();
            bool changed = false;
            foreach (var item in annots.Elements)
            {
                if (Deref(item) is not PdfDictionary a) { changed = true; continue; }
                var subtype = NameOf(a, "/Subtype");
                int flags = a.Elements.GetInteger("/F");
                if (ForbiddenAnnotations.Contains(subtype))
                {
                    t.Add(subtype == "/FileAttachment" ? "Attachments" : "Annotations", $"{subtype.TrimStart('/')} p{p + 1}");
                    changed = true;
                    continue;
                }
                if (subtype != "/Popup" && (flags & (FlagHidden | FlagNoView | FlagInvisible)) != 0)
                {
                    t.Add("Annotations", $"hidden p{p + 1}");   // shows nothing: taken away rather than made visible
                    changed = true;
                    continue;
                }
                if (subtype != "/Popup")
                {
                    int want = (flags | FlagPrint) & ~(FlagHidden | FlagInvisible | FlagNoView | FlagToggleNoView);
                    if (want != flags || !a.Elements.ContainsKey("/F")) { t.Add("AnnotationFlags"); if (fix) a.Elements.SetInteger("/F", want); }
                }
                var ap = Deref(a.Elements["/AP"]) as PdfDictionary;
                if (ap is not null)
                    foreach (var key in new[] { "/R", "/D" })
                        if (ap.Elements.ContainsKey(key)) { t.Add("Appearances", key.TrimStart('/')); if (fix) ap.Elements.Remove(key); }
                bool zero = PdfFlattener.RectOf(a) is not { } r || r.X1 - r.X0 < 0.001 || r.Y1 - r.Y0 < 0.001;
                if (subtype is not ("/Popup" or "/Link") && !zero)
                {
                    var n = ap is null ? null : Deref(ap.Elements["/N"]);
                    bool button = subtype == "/Widget" && AcroFormFields.Inherited(a, "/FT") is PdfName { Value: "/Btn" };
                    if (n is null)
                    {
                        if (subtype != "/Widget") { t.Add("Appearances", $"{subtype.TrimStart('/')} p{p + 1}"); if (fix) problems.Add($"a {subtype.TrimStart('/')} comment on page {p + 1} has no appearance"); }
                    }
                    else if (!button && n is PdfDictionary { Stream: null } states)
                    {
                        t.Add("Appearances", "states");
                        if (fix)
                        {
                            var st = NameOf(a, "/AS");
                            if (st.Length > 0 && states.Elements[st] is { } chosen) ap!.Elements["/N"] = chosen;
                            else problems.Add($"a {subtype.TrimStart('/')} on page {p + 1} has no appearance for its state");
                        }
                    }
                }
                keep.Add(item);
            }
            if (changed && fix)
            {
                if (keep.Count == 0) page.Elements.Remove("/Annots");
                else
                {
                    var arr = new PdfArray(doc);
                    foreach (var k in keep) arr.Elements.Add(k);
                    page.Elements["/Annots"] = arr;
                }
            }
        }

        // colour: device colour needs an output intent of its kind: sRGB, or this computer's CMYK profile for a file that
        // paints in CMYK (its RGB then goes through sRGB, set as each resource's DefaultRGB)
        var intent = OutputIntent(catalog);
        var usesCmyk = UsesDevice(doc, cmyk: true);
        if (intent is null)
        {
            t.Add("OutputIntent");
            if (usesCmyk.Count > 0) t.Add("Cmyk", string.Join(", ", usesCmyk));
            if (fix)
            {
                if (usesCmyk.Count == 0) AddOutputIntent(doc, Srgb(), 3, "sRGB IEC61966-2.1");
                else if (SystemCmykProfile() is { } cmykProfile)
                {
                    AddOutputIntent(doc, cmykProfile.Data, 4, cmykProfile.Name);
                    intent = 4;
                }
                else problems.Add("CMYK");
            }
        }
        else if (intent == 3 && usesCmyk.Count > 0) { t.Add("Cmyk", string.Join(", ", usesCmyk)); if (fix) problems.Add("CMYK"); }
        if (intent == 4)
        {
            int missing = RgbWithoutDefault(doc, fix);
            if (missing > 0) t.Add("Rgb", missing.ToString(CultureInfo.InvariantCulture));
        }

        // fonts: every one embedded
        var missingFonts = FontReplacer.Missing(doc);
        foreach (var f in missingFonts) t.Add("Fonts", f);
        var replaced = new List<string>();
        if (fix && missingFonts.Count > 0 && problems.Count == 0)
            foreach (var r in FontReplacer.EmbedMissing(doc)) replaced.Add($"{r.Name} → {r.With}");

        if (fix && problems.Count > 0)
        {
            if (problems.Contains("CMYK"))
                throw new PageAssemblyException($"the file uses CMYK colour ({string.Join(", ", usesCmyk)}) without a CMYK output intent", "Err_PdfaCmyk", string.Join(", ", usesCmyk));
        {
            var distinct = problems.Distinct().ToList();
            var list = string.Join("; ", distinct.Take(5)) + (distinct.Count > 5 ? $"; and {distinct.Count - 5} more" : "");
            throw new PageAssemblyException($"Washi cannot make this file PDF/A: {list}", "Err_PdfaCannot", list);
        }
        }
        var items = t.Items;
        if (fix && replaced.Count > 0)
        {
            replaced = [.. replaced.Distinct()];
            items = [.. items.Where(i => i.Kind != "Fonts"), new ArchiveItem("Fonts", replaced.Count, replaced)];
        }
        return (items, replaced);
    }

    /// <summary>An appearance that draws nothing (for a button: its current state, or Off).</summary>
    private static void EmptyAppearance(PdfDocument doc, PdfDictionary widget, bool button)
    {
        var r = PdfFlattener.RectOf(widget) ?? (0, 0, 0, 0);
        var x = new PdfDictionary(doc);
        doc.Internals.AddObject(x);
        x.Elements.SetName("/Type", "/XObject");
        x.Elements.SetName("/Subtype", "/Form");
        x.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(r.X1 - r.X0), new PdfReal(r.Y1 - r.Y0));
        x.Elements["/Resources"] = new PdfDictionary(doc);
        x.CreateStream([]);
        var ap = new PdfDictionary(doc);
        if (button)
        {
            var state = NameOf(widget, "/AS");
            if (state.Length == 0) { state = "/Off"; widget.Elements.SetName("/AS", state); }
            var states = new PdfDictionary(doc);
            states.Elements.SetReference(state, x);
            ap.Elements["/N"] = states;
        }
        else ap.Elements.SetReference("/N", x);
        widget.Elements["/AP"] = ap;
    }

    private static string? Forbidden(PdfDictionary action)
    {
        var s = NameOf(action, "/S");
        if (ForbiddenActions.Contains(s)) return s;
        if (s == "/Named" && !AllowedNamedActions.Contains(NameOf(action, "/N"))) return "/Named";
        return null;
    }

    private static IEnumerable<string> EmbeddedNames(PdfDictionary tree)
    {
        var stack = new Stack<PdfDictionary>([tree]);
        int guard = 0;
        while (stack.Count > 0 && guard++ < 10000)
        {
            var node = stack.Pop();
            if (Deref(node.Elements["/Names"]) is PdfArray pairs)
                for (int i = 0; i + 1 < pairs.Elements.Count; i += 2)
                    yield return Deref(pairs.Elements[i]) switch { PdfString s => s.Value, _ => "file" };
            if (Deref(node.Elements["/Kids"]) is PdfArray kids)
                foreach (var k in kids.Elements.Select(Deref).OfType<PdfDictionary>()) stack.Push(k);
        }
    }

    private static List<string> Filters(PdfDictionary d) => Deref(d.Elements["/Filter"]) switch
    {
        PdfName n => [n.Value],
        PdfArray a => [.. a.Elements.Select(Deref).OfType<PdfName>().Select(x => x.Value)],
        _ => [],
    };

    /// <summary>Every dictionary in the file: the objects and the dictionaries written inside them.</summary>
    private static IEnumerable<PdfDictionary> AllDictionaries(PdfDocument doc)
    {
        var seen = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem>();
        foreach (var o in doc.Internals.GetAllObjects()) stack.Push(o);
        stack.Push(doc.Internals.Catalog);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (!seen.Add(item)) continue;
            switch (item)
            {
                case PdfDictionary d:
                    yield return d;
                    foreach (var v in d.Elements.Values) if (v is PdfDictionary or PdfArray) stack.Push(v);
                    break;
                case PdfArray a:
                    foreach (var v in a.Elements) if (v is PdfDictionary or PdfArray) stack.Push(v);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ colour

    /// <summary>The number of colour components of the PDF/A output intent (3 for sRGB), or null when there is none.</summary>
    private static int? OutputIntent(PdfDictionary catalog)
    {
        if (Deref(catalog.Elements["/OutputIntents"]) is not PdfArray arr) return null;
        foreach (var i in arr.Elements.Select(Deref).OfType<PdfDictionary>())
            if (NameOf(i, "/S") == "/GTS_PDFA1" && Deref(i.Elements["/DestOutputProfile"]) is PdfDictionary icc)
                return icc.Elements.GetInteger("/N");
        return null;
    }

    private static byte[] Srgb()
    {
        using var s = typeof(PdfDocument).Assembly.GetManifestResourceStream("PdfSharp.Resources.sRGB2014.icc")
                      ?? throw new InvalidOperationException("the sRGB profile is missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>A CMYK printer profile of this computer (ICC, colour space CMYK), for files that paint in CMYK.</summary>
    private static (byte[] Data, string Name)? SystemCmykProfile()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsMacOS()) candidates.Add("/System/Library/ColorSync/Profiles/Generic CMYK Profile.icc");
        else if (OperatingSystem.IsWindows())
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
            candidates.Add(Path.Combine(dir, "RSWOP.icm"));
            if (Directory.Exists(dir)) candidates.AddRange(Directory.GetFiles(dir, "*.ic?").Order());
        }
        else
            foreach (var dir in new[] { "/usr/share/color/icc", "/usr/local/share/color/icc", "/usr/share/ghostscript" })
                if (Directory.Exists(dir))
                    try { candidates.AddRange(Directory.EnumerateFiles(dir, "*.ic?", SearchOption.AllDirectories).Order()); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var path in candidates.Where(File.Exists))
        {
            try
            {
                var data = File.ReadAllBytes(path);
                // header: colour space at 16 ("CMYK"), version at 8 (2 or 4), profile class at 12 (printer or output)
                if (data.Length > 128 && Encoding.ASCII.GetString(data, 16, 4) == "CMYK" && data[8] is 2 or 4
                    && Encoding.ASCII.GetString(data, 12, 4) is "prtr")
                    return (data, Path.GetFileNameWithoutExtension(path));
            }
            catch (IOException) { }
        }
        return null;
    }

    private static PdfDictionary Profile(PdfDocument doc, byte[] data, int n)
    {
        var profile = new PdfDictionary(doc);
        doc.Internals.AddObject(profile);
        profile.Elements.SetInteger("/N", n);
        ContentRedactor.SetStream(profile, data);
        return profile;
    }

    private static void AddOutputIntent(PdfDocument doc, byte[] icc, int n, string name)
    {
        var intent = new PdfDictionary(doc);
        intent.Elements.SetName("/Type", "/OutputIntent");
        intent.Elements.SetName("/S", "/GTS_PDFA1");
        intent.Elements.SetString("/OutputConditionIdentifier", name);
        intent.Elements.SetString("/Info", name);
        if (n == 3) intent.Elements.SetString("/RegistryName", "http://www.color.org");
        intent.Elements.SetReference("/DestOutputProfile", Profile(doc, icc, n));
        var list = Deref(doc.Internals.Catalog.Elements["/OutputIntents"]) as PdfArray;
        if (list is null) { list = new PdfArray(doc); doc.Internals.Catalog.Elements["/OutputIntents"] = list; }
        list.Elements.Add(intent);
    }

    /// <summary>
    /// With a CMYK output intent, device RGB must go through a profile of its own: each resource dictionary gets sRGB as
    /// /DefaultRGB, and transparency groups in device RGB get sRGB. The number of places that lacked it.
    /// </summary>
    private static int RgbWithoutDefault(PdfDocument doc, bool fix)
    {
        if (UsesDevice(doc, cmyk: false).Count == 0) return 0;
        int missing = 0;
        PdfArray? srgb = null;
        PdfArray Srgb1()
        {
            if (srgb is not null) return srgb;
            srgb = new PdfArray(doc, new PdfName("/ICCBased"), Profile(doc, Srgb(), 3).Reference!);
            return srgb;
        }
        foreach (var d in AllDictionaries(doc).ToList())
        {
            // a form (an appearance, say) with no resources of its own still paints in RGB: it gets some
            if (d.Stream is not null && NameOf(d, "/Subtype") == "/Form" && d.Elements["/Resources"] is null)
            {
                if (fix) d.Elements["/Resources"] = new PdfDictionary(doc);
                else missing++;
            }
            if (Deref(d.Elements["/Resources"]) is PdfDictionary res)
            {
                var spaces = Deref(res.Elements["/ColorSpace"]) as PdfDictionary;
                if (spaces?.Elements.ContainsKey("/DefaultRGB") != true)
                {
                    missing++;
                    if (fix)
                    {
                        if (spaces is null) { spaces = new PdfDictionary(doc); res.Elements["/ColorSpace"] = spaces; }
                        spaces.Elements["/DefaultRGB"] = Srgb1();
                    }
                }
            }
            if (NameOf(d, "/S") == "/Transparency" && Deref(d.Elements["/CS"]) is PdfName { Value: "/DeviceRGB" })
            {
                missing++;
                if (fix) d.Elements["/CS"] = Srgb1();
            }
        }
        return missing;
    }

    /// <summary>Where the file paints in device CMYK (or RGB): page numbers, or "images".</summary>
    private static List<string> UsesDevice(PdfDocument doc, bool cmyk)
    {
        string device = cmyk ? "/DeviceCMYK" : "/DeviceRGB";
        var where = new List<string>();
        foreach (var d in AllDictionaries(doc))
        {
            var cs = Deref(d.Elements["/ColorSpace"]);
            if (NameOf(d, "/Subtype") == "/Image" && IsDevice(cs, device) || d.Elements.ContainsKey("/ShadingType") && IsDevice(cs, device)
                || NameOf(d, "/S") == "/Transparency" && IsDevice(Deref(d.Elements["/CS"]), device))
            {
                if (!where.Contains("images")) where.Add("images");
            }
        }
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            bool found = false;
            var res = Deref(page.Elements["/Resources"]) as PdfDictionary;
            foreach (var bytes in ContentOf(page))
            {
                var cs = new ContentStream(bytes);
                foreach (var op in cs.Ops)
                {
                    if (cmyk ? op.Name is "k" or "K" : op.Name is "rg" or "RG") { found = true; break; }
                    if (op.Name is "cs" or "CS" && op.Names.Count > 0 && ("/" + op.Names[^1] == device || Named(res, op.Names[^1], device))) { found = true; break; }
                    if (op.Name == "BI" && Encoding.Latin1.GetString(bytes, op.Start, Math.Min(op.End - op.Start, 300)) is var head
                        && (head.Contains(cmyk ? "/CMYK" : "/RGB", StringComparison.Ordinal) || head.Contains(device, StringComparison.Ordinal))) { found = true; break; }
                }
                if (found) break;
            }
            if (found) where.Add($"p{p + 1}");
        }
        return where;
    }

    private static bool Named(PdfDictionary? res, string name, string device) =>
        res is not null && Deref(res.Elements["/ColorSpace"]) is PdfDictionary spaces && IsDevice(Deref(spaces.Elements["/" + name]), device);

    private static bool IsDevice(PdfItem? cs, string device) => cs switch
    {
        PdfName n => n.Value == device,
        PdfArray a when a.Elements.Count > 0 && Deref(a.Elements[0]) is PdfName first =>
            first.Value switch
            {
                "/Indexed" or "/Pattern" => a.Elements.Count > 1 && IsDevice(Deref(a.Elements[1]), device),
                "/Separation" or "/DeviceN" => a.Elements.Count > 2 && IsDevice(Deref(a.Elements[2]), device),
                _ => false,
            },
        _ => false,
    };

    /// <summary>The page's own content and the forms it draws (one level is enough to see the colour it uses).</summary>
    private static IEnumerable<byte[]> ContentOf(PdfPage page)
    {
        var streams = Deref(page.Elements["/Contents"]) switch
        {
            PdfArray arr => arr.Elements.Select(Deref).OfType<PdfDictionary>().ToList(),
            PdfDictionary one => [one],
            _ => [],
        };
        if (Deref(page.Elements["/Resources"]) is PdfDictionary res && Deref(res.Elements["/XObject"]) is PdfDictionary xs)
            streams.AddRange(xs.Elements.Values.Select(Deref).OfType<PdfDictionary>().Where(x => NameOf(x, "/Subtype") == "/Form"));
        foreach (var s in streams.Where(s => s.Stream is not null))
        {
            byte[]? b = null;
            try { b = s.Stream.UnfilteredValue; } catch (Exception) { }
            if (b is not null) yield return b;
        }
    }

    // ------------------------------------------------------------------ metadata

    /// <summary>
    /// The saved file again, with XMP metadata that says PDF/A-2b and repeats the document properties exactly (added as an
    /// incremental update, since PDFsharp writes its own XMP on every save).
    /// </summary>
    private static byte[] WithMetadata(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var d = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var info = d.Info.Elements;
        string? S(string key) => Deref(info[key]) switch { PdfString s => s.Value, PdfStringObject so => so.Value, _ => null };
        var update = new IncrementalUpdate(bytes);
        int highest = d.Internals.GetAllObjects().Select(o => o.Reference?.ObjectNumber ?? 0).DefaultIfEmpty(0).Max();
        int number = update.NewNumber(highest);
        var xmp = Encoding.UTF8.GetBytes(Xmp(S("/Title"), S("/Author"), S("/Subject"), S("/Keywords"), S("/Creator"), S("/Producer"),
                                             XmpDate(S("/CreationDate") ?? RawDate(bytes, "CreationDate")), XmpDate(S("/ModDate") ?? RawDate(bytes, "ModDate"))));
        var body = new MemoryStream();
        body.Write(Encoding.Latin1.GetBytes($"<</Type/Metadata/Subtype/XML/Length {xmp.Length}>>\nstream\n"));
        body.Write(xmp);
        body.Write(Encoding.Latin1.GetBytes("\nendstream"));
        update.Put(number, 0, body.ToArray());
        // PDFsharp writes a page's transparency group in device RGB again: with a CMYK output intent it goes through sRGB
        if (OutputIntent(d.Internals.Catalog) == 4)
        {
            int? profile = null;
            for (int p = 0; p < d.PageCount; p++)
            {
                var page = d.Pages[p];
                if (Deref(page.Elements["/Group"]) is not PdfDictionary g || Deref(g.Elements["/CS"]) is not PdfName { Value: "/DeviceRGB" } || page.Reference is null) continue;
                if (profile is null)
                {
                    profile = update.NewNumber(number);
                    var icc = Srgb();
                    var pb = new MemoryStream();
                    pb.Write(Encoding.Latin1.GetBytes($"<</N 3/Length {icc.Length}>>\nstream\n"));
                    pb.Write(icc);
                    pb.Write(Encoding.Latin1.GetBytes("\nendstream"));
                    update.Put(profile.Value, 0, pb.ToArray());
                }
                g.Elements["/CS"] = new PdfLiteral($"[/ICCBased {profile} 0 R]");
                update.Put(page.Reference.ObjectNumber, page.Reference.GenerationNumber, PdfSyntax.Write(page));
            }
        }
        var catalog = d.Internals.Catalog;
        var text = PdfSyntax.Write(catalog);
        text = MetadataRefRx().Replace(text, "");
        text = text[..text.LastIndexOf(">>", StringComparison.Ordinal)] + $"/Metadata {number} 0 R>>";
        var root = catalog.Reference!;
        update.Put(root.ObjectNumber, root.GenerationNumber, text);
        return update.Build();
    }

    /// <summary>PDFsharp reads dates as dates; the file's own text of one, when the dictionary gives no string.</summary>
    private static string? RawDate(byte[] bytes, string key)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var m = Regex.Match(text, "/" + key + @"\s*\((D:[^)]*)\)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Xmp(string? title, string? author, string? subject, string? keywords, string? creator, string? producer,
                              string? created, string? modified)
    {
        static string E(string s) => SecurityElement.Escape(s);
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n<pdfaid:part>2</pdfaid:part>\n<pdfaid:conformance>B</pdfaid:conformance>\n</rdf:Description>\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n<dc:format>application/pdf</dc:format>\n");
        if (title is not null) sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{E(title)}</rdf:li></rdf:Alt></dc:title>\n");
        if (author is not null) sb.Append($"<dc:creator><rdf:Seq><rdf:li>{E(author)}</rdf:li></rdf:Seq></dc:creator>\n");
        if (subject is not null) sb.Append($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{E(subject)}</rdf:li></rdf:Alt></dc:description>\n");
        sb.Append("</rdf:Description>\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        if (producer is not null) sb.Append($"<pdf:Producer>{E(producer)}</pdf:Producer>\n");
        if (keywords is not null) sb.Append($"<pdf:Keywords>{E(keywords)}</pdf:Keywords>\n");
        sb.Append("</rdf:Description>\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        if (creator is not null) sb.Append($"<xmp:CreatorTool>{E(creator)}</xmp:CreatorTool>\n");
        if (created is not null) sb.Append($"<xmp:CreateDate>{created}</xmp:CreateDate>\n");
        if (modified is not null) sb.Append($"<xmp:ModifyDate>{modified}</xmp:ModifyDate>\n<xmp:MetadataDate>{modified}</xmp:MetadataDate>\n");
        sb.Append("</rdf:Description>\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\">\n");
        sb.Append($"<xmpMM:DocumentID>uuid:{Guid.NewGuid()}</xmpMM:DocumentID>\n<xmpMM:InstanceID>uuid:{Guid.NewGuid()}</xmpMM:InstanceID>\n</rdf:Description>\n");
        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n");
        sb.Append(new string(' ', 2000)).Append('\n');
        sb.Append("<?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    /// <summary>A PDF date (D:YYYYMMDDHHmmSS+HH'mm') as an XMP date (YYYY-MM-DDTHH:mm:SS+HH:mm), at the same precision.</summary>
    internal static string? XmpDate(string? pdf)
    {
        if (pdf is null) return null;
        var m = PdfDateRx().Match(pdf.Trim());
        if (!m.Success) return null;
        var sb = new StringBuilder(m.Groups["y"].Value);
        if (m.Groups["mo"].Success) sb.Append('-').Append(m.Groups["mo"].Value);
        if (m.Groups["d"].Success) sb.Append('-').Append(m.Groups["d"].Value);
        if (m.Groups["h"].Success)
        {
            sb.Append('T').Append(m.Groups["h"].Value).Append(':').Append(m.Groups["mi"].Success ? m.Groups["mi"].Value : "00");
            if (m.Groups["s"].Success) sb.Append(':').Append(m.Groups["s"].Value);
            var z = m.Groups["z"].Value;
            if (z == "Z") sb.Append('Z');
            else if (z is "+" or "-") sb.Append(z).Append(m.Groups["zh"].Success ? m.Groups["zh"].Value : "00").Append(':').Append(m.Groups["zm"].Success ? m.Groups["zm"].Value : "00");
        }
        return sb.ToString();
    }

    /// <summary>What is wrong with the file's PDF/A identification and its agreement with the document properties.</summary>
    private static List<string> MetadataProblems(PdfDocument doc)
    {
        var problems = new List<string>();
        if (Deref(doc.Internals.Catalog.Elements["/Metadata"]) is not PdfDictionary { Stream: not null } m) return ["no XMP metadata"];
        if (m.Elements.ContainsKey("/Filter")) problems.Add("the XMP is compressed");
        var xmp = Encoding.UTF8.GetString(m.Stream.UnfilteredValue);
        string? Prop(string name)
        {
            var e = Regex.Match(xmp, "<" + Regex.Escape(name) + @">\s*(?:<rdf:(?:Alt|Seq|Bag)>\s*<rdf:li[^>]*>)?(.*?)(?:</rdf:li>)?", RegexOptions.Singleline);
            if (e.Success)
            {
                var full = Regex.Match(xmp, "<" + Regex.Escape(name) + @">(.*?)</" + Regex.Escape(name) + ">", RegexOptions.Singleline);
                var inner = full.Success ? full.Groups[1].Value : e.Groups[1].Value;
                var li = Regex.Match(inner, @"<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Singleline);
                return System.Net.WebUtility.HtmlDecode((li.Success ? li.Groups[1].Value : inner).Trim());
            }
            var a = Regex.Match(xmp, Regex.Escape(name) + "=\"([^\"]*)\"");
            return a.Success ? System.Net.WebUtility.HtmlDecode(a.Groups[1].Value) : null;
        }
        var part = Prop("pdfaid:part");
        var conformance = Prop("pdfaid:conformance");
        if (part is not ("2" or "3") || conformance is not ("B" or "U" or "A")) problems.Add("the XMP does not say PDF/A-2");
        var info = doc.Info.Elements;
        string? S(string key) => Deref(info[key]) switch { PdfString s => s.Value, PdfStringObject so => so.Value, _ => null };
        foreach (var (key, prop) in new[] { ("/Title", "dc:title"), ("/Author", "dc:creator"), ("/Subject", "dc:description"), ("/Keywords", "pdf:Keywords"),
                                            ("/Creator", "xmp:CreatorTool"), ("/Producer", "pdf:Producer") })
            if (S(key) is { } v && Prop(prop) != v) problems.Add($"{key.TrimStart('/')} differs from the XMP");
        return problems;
    }

    // ------------------------------------------------------------------ checking the copy

    private static void Verify(string before, string after, List<string> replacedFonts)
    {
        var left = Check(after);
        if (left.Count > 0)
        {
            var what = string.Join("; ", left.Select(i => i.Details.Count == 0 ? i.Kind : $"{i.Kind} ({string.Join(", ", i.Details.Take(5))})"));
            throw new PageAssemblyException($"the copy still has: {what}; it was not kept", "Err_PdfaCheck", what);
        }
        // the caller holds PDFium's lock (as for every Washi operation that renders)
        {
            using var a = PdfiumDocument.Open(before, PdfPasswords.For(before));
            using var b = PdfiumDocument.Open(after);
            if (a.PageCount != b.PageCount) throw new PageAssemblyException("the copy has another number of pages; it was not kept", "Err_PdfaChanged", 1);
            var pages = a.PageCount <= 12 ? Enumerable.Range(0, a.PageCount).ToList()
                : [.. Enumerable.Range(0, 4).Concat(Enumerable.Range(a.PageCount / 2 - 2, 4)).Concat(Enumerable.Range(a.PageCount - 4, 4)).Distinct()];
            foreach (var page in pages)
            {
                // the text: the same characters in the same places (a replaced font changes the glyphs, not where they are)
                var ca = a.ReadChars(page);
                var cb = b.ReadChars(page);
                var ta = Norm(string.Concat(ca.Select(c => c.Text)));
                var tb = Norm(string.Concat(cb.Select(c => c.Text)));
                if (ta != tb)
                    throw new PageAssemblyException($"the text of page {page + 1} reads differently in the copy; it was not kept", "Err_PdfaChanged", page + 1);
                var placed = new List<PdfRect>();
                if (ca.Count == cb.Count)
                    for (int i = 0; i < ca.Count; i++)
                    {
                        if (Math.Abs(ca[i].Rect.X0 - cb[i].Rect.X0) > 1.0 && Math.Abs((ca[i].Rect.X0 + ca[i].Rect.X1) / 2 - (cb[i].Rect.X0 + cb[i].Rect.X1) / 2) > 1.0)
                            throw new PageAssemblyException($"text moved on page {page + 1} (“{ca[i].Text}”); the copy was not kept", "Err_PdfaChanged", page + 1);
                        if (replacedFonts.Count > 0) { placed.Add(ca[i].Rect); placed.Add(cb[i].Rect); }
                    }
                const double scale = 1.5;
                var ia = a.RenderPage(page, scale);
                var ib = b.RenderPage(page, scale);
                if (ia.Width != ib.Width || ia.Height != ib.Height)
                    throw new PageAssemblyException($"page {page + 1} changed size; the copy was not kept", "Err_PdfaChanged", page + 1);
                var ok = new bool[ia.Width * ia.Height];
                foreach (var r in placed)
                {
                    int x0 = Math.Max(0, (int)Math.Floor(r.X0 * scale) - 2), x1 = Math.Min(ia.Width - 1, (int)Math.Ceiling(r.X1 * scale) + 2);
                    int y0 = Math.Max(0, (int)Math.Floor(r.Y0 * scale) - 2), y1 = Math.Min(ia.Height - 1, (int)Math.Ceiling(r.Y1 * scale) + 2);
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) ok[y * ia.Width + x] = true;
                }
                int changed = 0;
                for (int i = 0, q = 0; i < ok.Length; i++, q += 3)
                    if (!ok[i] && Math.Abs(ia.Pixels[q] - ib.Pixels[q]) + Math.Abs(ia.Pixels[q + 1] - ib.Pixels[q + 1]) + Math.Abs(ia.Pixels[q + 2] - ib.Pixels[q + 2]) > 48)
                        changed++;
                if (changed > ia.Width * ia.Height / 2000)
                    throw new PageAssemblyException($"page {page + 1} looks different in the copy ({changed} pixels); it was not kept", "Err_PdfaChanged", page + 1);
            }
        }
    }

    private static string Norm(string s) => WhiteRx().Replace(s.Normalize(NormalizationForm.FormKC), "");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteRx();
    [GeneratedRegex(@"^%PDF-1\.[0-7]")]
    private static partial Regex HeaderRx();
    [GeneratedRegex(@"/Metadata\s+\d+\s+\d+\s+R\s*")]
    private static partial Regex MetadataRefRx();
    [GeneratedRegex(@"^(?:D:)?(?<y>\d{4})(?<mo>\d{2})?(?<d>\d{2})?(?<h>\d{2})?(?<mi>\d{2})?(?<s>\d{2})?(?<z>[Zz+\-])?(?<zh>\d{2})?'?(?<zm>\d{2})?'?")]
    private static partial Regex PdfDateRx();

    /// <summary>A name entry's value ("" when it is missing or not a name, as in damaged files).</summary>
    private static string NameOf(PdfDictionary d, string key) => Deref(d.Elements[key]) is PdfName n ? n.Value : "";

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
