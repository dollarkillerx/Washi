// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Washi.App.ViewModels;
using Washi.Core;
using Washi.Masume;

namespace Washi.App.Controls;

/// <summary>
/// Draws the fields of one page over the page image and lets the user edit them:
/// click to select, drag to move, drag a corner to resize, drag on empty paper (draw mode)
/// to create, Delete to remove, arrows to nudge (Shift = 5 pt). Coordinates are page points
/// times <see cref="Zoom"/>. One control draws every field: a page can hold hundreds.
/// </summary>
public sealed class FieldCanvas : Control
{
    public static readonly StyledProperty<MainViewModel?> EditorProperty =
        AvaloniaProperty.Register<FieldCanvas, MainViewModel?>(nameof(Editor));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<FieldCanvas, double>(nameof(Zoom), 1.0);

    public MainViewModel? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    private const double HandlePx = 7;     // size of the corner handles, in screen pixels
    private const double MinSizePt = 4;

    private enum Drag { None, Move, Resize, Create }
    private Drag _drag;
    private Point _start;                  // page points
    private PdfRect _startRect;
    private (bool Left, bool Top) _corner; // which corner is being dragged
    private FieldViewModel? _dragged;
    private PdfRect? _eraseBox;           // the erase box being drawn, page points

    static FieldCanvas()
    {
        AffectsRender<FieldCanvas>(ZoomProperty);
        FocusableProperty.OverrideDefaultValue<FieldCanvas>(true);
    }

    public FieldCanvas() => ClipToBounds = true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty)
        {
            if (change.OldValue is MainViewModel old) Unhook(old);
            if (change.NewValue is MainViewModel vm) Hook(vm);
            InvalidateVisual();
        }
    }

    private void Hook(MainViewModel vm)
    {
        vm.PropertyChanged += OnEditorChanged;
        vm.Erase.Changed += InvalidateVisual;
        vm.Sign.Changed += InvalidateVisual;
        vm.Fields.CollectionChanged += OnFieldsChanged;
        foreach (var f in vm.Fields) f.PropertyChanged += OnFieldChanged;
    }

    private void Unhook(MainViewModel vm)
    {
        vm.PropertyChanged -= OnEditorChanged;
        vm.Erase.Changed -= InvalidateVisual;
        vm.Sign.Changed -= InvalidateVisual;
        vm.Fields.CollectionChanged -= OnFieldsChanged;
        foreach (var f in vm.Fields) f.PropertyChanged -= OnFieldChanged;
    }

    private void OnEditorChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedField) or nameof(MainViewModel.PageIndex) or nameof(MainViewModel.DrawMode)
                           or nameof(MainViewModel.ActiveTool) or nameof(MainViewModel.PreviewMode)
                           or nameof(MainViewModel.PageImage) or nameof(MainViewModel.PageWidth) or nameof(MainViewModel.PageHeight))
            InvalidateVisual();
    }

    private void OnFieldsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        foreach (FieldViewModel f in e.OldItems ?? Array.Empty<FieldViewModel>()) f.PropertyChanged -= OnFieldChanged;
        foreach (FieldViewModel f in e.NewItems ?? Array.Empty<FieldViewModel>()) f.PropertyChanged += OnFieldChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && Editor is { } vm)
            foreach (var f in vm.Fields) { f.PropertyChanged -= OnFieldChanged; f.PropertyChanged += OnFieldChanged; }
        InvalidateVisual();
    }

    private void OnFieldChanged(object? s, PropertyChangedEventArgs e) => InvalidateVisual();

    // ------------------------------------------------------------------ drawing
    private static readonly Dictionary<FieldKind, Color> KindColors = new()
    {
        [FieldKind.Text] = Color.FromRgb(31, 73, 224),
        [FieldKind.Amount] = Color.FromRgb(140, 51, 217),
        [FieldKind.Comb] = Color.FromRgb(226, 112, 0),
        [FieldKind.Checkbox] = Color.FromRgb(13, 150, 64),
        [FieldKind.Radio] = Color.FromRgb(0, 125, 121),
        [FieldKind.Choice] = Color.FromRgb(0, 99, 168),
        [FieldKind.Signature] = Color.FromRgb(138, 63, 0),
    };

    private static readonly Color Active = Color.FromRgb(224, 69, 43);   // vermilion

    /// <summary>Fields are shown and edited in the form tool only; the other tools show the plain page.</summary>
    private IEnumerable<FieldViewModel> PageFields =>
        Editor is { IsFormTool: true } vm ? vm.Fields.Where(f => f.Page == vm.PageIndex) : [];

    private Rect Screen(FieldViewModel f) => new(f.X0 * Zoom, f.Y0 * Zoom, (f.X1 - f.X0) * Zoom, (f.Y1 - f.Y0) * Zoom);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.White, new Rect(Bounds.Size));   // paper, and hit-testable everywhere
        // the page is drawn here too, in the same coordinates as the fields, so they always line up
        if (Editor is { PageImage: { } page } ed)
            ctx.DrawImage(page, new Rect(0, 0, page.PixelSize.Width, page.PixelSize.Height), new Rect(0, 0, ed.PageWidth * Zoom, ed.PageHeight * Zoom));
        if (Editor is { IsEraseTool: true } er)
        {
            RenderErase(ctx, er);
            return;
        }
        if (Editor is { IsSignTool: true } sg)
        {
            RenderSign(ctx, sg);
            return;
        }
        var selected = Editor?.SelectedField;
        var typeface = new Typeface("Inter");
        if (Editor is { PreviewMode: true })
        {
            foreach (var f in PageFields) RenderPreview(ctx, f, ReferenceEquals(f, selected), typeface);
            return;
        }
        foreach (var f in PageFields)
        {
            var c = KindColors[f.Kind];
            var r = Screen(f);
            bool sel = ReferenceEquals(f, selected);
            // the selected field is the brand's vermilion: "the field being filled"
            if (f.Kind == FieldKind.Radio)
                ctx.DrawEllipse(new SolidColorBrush(sel ? Active : c, sel ? 0.18 : 0.14), new Pen(new SolidColorBrush(sel ? Active : c), sel ? 2 : 1), r);
            else
                ctx.DrawRectangle(new SolidColorBrush(sel ? Active : c, sel ? 0.18 : 0.14), new Pen(new SolidColorBrush(sel ? Active : c), sel ? 2 : 1), r);
            if (f.Kind == FieldKind.Signature && r.Height >= 10)
            {
                var pen = new FormattedText("✍", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                                            Math.Clamp(r.Height * 0.5, 9, 18), new SolidColorBrush(c, 0.7));
                ctx.DrawText(pen, new Point(r.Right - pen.Width - 3, r.Bottom - pen.Height - 1));
            }
            if (f.Kind == FieldKind.Choice && r.Height >= 8)
                DrawCaret(ctx, r, new SolidColorBrush(c));

            if (f.Kind == FieldKind.Comb && f.MaxLen > 1)
            {
                var tick = new Pen(new SolidColorBrush(c, 0.8), 1);
                double cw = r.Width / f.MaxLen;
                for (int i = 1; i < f.MaxLen; i++)
                    ctx.DrawLine(tick, new Point(r.X + i * cw, r.Bottom - Math.Min(6, r.Height / 3)), new Point(r.X + i * cw, r.Bottom));
            }
            if (f.Prefilled.Length > 0)
                ctx.DrawRectangle(null, new Pen(Brushes.Crimson, 1, new DashStyle([3, 2], 0)), r.Inflate(1.5));

            if (r.Height >= 11 && r.Width >= 30)
            {
                var text = new FormattedText(f.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                                             Math.Clamp(r.Height * 0.32, 7, 11), new SolidColorBrush(c))
                { MaxTextWidth = Math.Max(1, r.Width - 4), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                ctx.DrawText(text, new Point(r.X + 2, r.Y + 1));
            }

            if (sel)
                foreach (var h in Handles(r))
                    ctx.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Active), 1.5), h);
        }
    }

    /// <summary>The ▾ of a drop-down list, at the right end of its box.</summary>
    private static void DrawCaret(DrawingContext ctx, Rect r, IBrush brush)
    {
        double s = Math.Clamp(r.Height * 0.3, 4, 8), x = r.Right - s - 3, y = r.Center.Y - s / 4;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, y), true);
            c.LineTo(new Point(x + s, y));
            c.LineTo(new Point(x + s / 2, y + s / 2));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, g);
    }

    private static readonly Color Blue = Color.FromRgb(31, 73, 224);
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(22, 22, 22));

    /// <summary>A field as a PDF viewer shows it: a light box with its value, no name or handles.</summary>
    private void RenderPreview(DrawingContext ctx, FieldViewModel f, bool selected, Typeface typeface)
    {
        var r = Screen(f);
        ctx.DrawRectangle(new SolidColorBrush(Blue, selected ? 0.12 : 0.06),
                          new Pen(new SolidColorBrush(selected ? Active : Blue, selected ? 1 : 0.35), selected ? 1.5 : 0.75), r);
        if (f.Kind == FieldKind.Choice && r.Height >= 8) DrawCaret(ctx, r, new SolidColorBrush(Blue, 0.6));
        if (f.Value.Length == 0) return;

        if (f.Kind == FieldKind.Radio)
        {
            if (!f.IsChecked) return;
            double d = Math.Min(r.Width, r.Height) * 0.55;
            ctx.DrawEllipse(Ink, null, r.Center, d / 2, d / 2);
            return;
        }

        if (f.Kind == FieldKind.Checkbox)
        {
            if (!f.IsChecked) return;
            var s = Math.Min(r.Width, r.Height);
            var c = r.Center;
            var tick = new StreamGeometry();
            using (var g = tick.Open())
            {
                g.BeginFigure(new Point(c.X - s * 0.32, c.Y), false);
                g.LineTo(new Point(c.X - s * 0.08, c.Y + s * 0.25));
                g.LineTo(new Point(c.X + s * 0.34, c.Y - s * 0.28));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(Ink, Math.Max(1, s * 0.13), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), tick);
            return;
        }

        if (f.Kind == FieldKind.Comb && f.MaxLen > 0)
        {
            double pitch = r.Width / f.MaxLen;
            double size = Math.Min(f.FontSize > 0 ? f.FontSize * Zoom : r.Height * 0.62, Math.Min(r.Height - 2, pitch) * 0.95);
            for (int i = 0; i < Math.Min(f.Value.Length, f.MaxLen); i++)
            {
                var t = Text(f.Value[i].ToString(), new Typeface(TextFamily(f)), size, TextBrush(f));
                ctx.DrawText(t, new Point(r.X + pitch * i + (pitch - t.Width) / 2, r.Y + (r.Height - t.Height) / 2));
            }
            return;
        }

        // as the saved PDF draws it: the chosen size (or one from the field's height) is the most the text gets, and
        // a value too long for the box is shrunk to fit rather than cut off
        double pad = 2 * Zoom;
        double fs = f.FontSize > 0 ? f.FontSize * Zoom : Math.Min(r.Height * 0.62, 12 * Zoom);
        fs = Math.Min(fs, Math.Max(4 * Zoom, r.Height - 2 * Zoom));
        var face = new Typeface(TextFamily(f));
        var brush = TextBrush(f);
        var text = Text(f.Value, face, fs, brush);
        double room = Math.Max(1, r.Width - 2 * pad);
        if (f.Y1 - f.Y0 > 24)
        {
            // tall fields wrap (as the saved PDF does); lines that still do not fit get smaller
            for (text.MaxTextWidth = room; text.Height > r.Height - pad && fs > 4 * Zoom; text.MaxTextWidth = room)
                text = Text(f.Value, face, fs = Math.Max(4 * Zoom, fs - 0.5 * Zoom), brush);
            ctx.DrawText(text, new Point(r.X + pad, r.Y + pad / 2));
            return;
        }
        if (text.Width > room) text = Text(f.Value, face, Math.Max(4 * Zoom, fs * room / text.Width), brush);
        text.MaxTextWidth = Math.Max(1, r.Width - pad);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        double x = f.Align switch
        {
            1 => r.X + (r.Width - text.Width) / 2,
            2 => r.Right - pad - text.Width,
            _ => r.X + pad,
        };
        ctx.DrawText(text, new Point(Math.Max(r.X + 1, x), r.Y + (r.Height - text.Height) / 2));
    }

    private static FormattedText Text(string s, Typeface typeface, double size, IBrush? brush = null) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Math.Max(1, size), brush ?? Ink);

    /// <summary>The colour a field's value is drawn in (black unless one was chosen).</summary>
    public static IBrush TextBrush(FieldViewModel f) =>
        f.Color.Length == 7 && Color.TryParse(f.Color, out var c) ? new SolidColorBrush(c) : Ink;

    /// <summary>Screen fonts close to what the PDF asks its viewer for: Mincho / Song for serif, Gothic / Hei for sans.</summary>
    public static FontFamily TextFamily(FieldViewModel f) => f.Font switch
    {
        "serif" => new FontFamily("Hiragino Mincho ProN, Songti SC, Yu Mincho, MS Mincho, Times New Roman, serif"),
        "sans" => new FontFamily("Hiragino Sans, PingFang SC, Yu Gothic, Meiryo, Helvetica Neue, Arial, sans-serif"),
        _ => new FontFamily("Inter, Hiragino Sans, PingFang SC, Yu Gothic, sans-serif"),
    };

    private static IEnumerable<Rect> Handles(Rect r)
    {
        double s = HandlePx;
        yield return new Rect(r.X - s / 2, r.Y - s / 2, s, s);
        yield return new Rect(r.Right - s / 2, r.Y - s / 2, s, s);
        yield return new Rect(r.X - s / 2, r.Bottom - s / 2, s, s);
        yield return new Rect(r.Right - s / 2, r.Bottom - s / 2, s, s);
    }

    // ------------------------------------------------------------------ pointer
    private Point ToPage(Point p) => new(p.X / Zoom, p.Y / Zoom);

    /// <summary>Topmost field under the point (smallest first, so nested boxes stay reachable).</summary>
    private FieldViewModel? HitField(Point screen) =>
        PageFields.Where(f => Screen(f).Inflate(2).Contains(screen)).OrderBy(f => (f.X1 - f.X0) * (f.Y1 - f.Y0)).FirstOrDefault();

    private (bool Left, bool Top)? HitHandle(FieldViewModel f, Point screen)
    {
        var r = Screen(f);
        var corners = new[] { (r.TopLeft, true, true), (r.TopRight, false, true), (r.BottomLeft, true, false), (r.BottomRight, false, false) };
        foreach (var (pt, left, top) in corners)
            if (Math.Abs(pt.X - screen.X) <= HandlePx && Math.Abs(pt.Y - screen.Y) <= HandlePx) return (left, top);
        return null;
    }

    private Rect Screen(PdfRect r) => new(r.X0 * Zoom, r.Y0 * Zoom, Math.Max(0, r.Width) * Zoom, Math.Max(0, r.Height) * Zoom);

    /// <summary>Erase tool: the boxes (vermilion), what will be removed (red) and what stays inside them (amber).</summary>
    private void RenderErase(DrawingContext ctx, MainViewModel vm)
    {
        var page = vm.PageIndex;
        foreach (var t in vm.Erase.Targets.Where(t => t.Page == page))
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(218, 30, 40), 0.28), null, Screen(t.Rect));
        var amber = new Pen(new SolidColorBrush(Color.FromRgb(241, 194, 27)), 1.5, new DashStyle([2, 2], 0));
        foreach (var k in vm.Erase.Kept.Where(k => k.Page == page))
            ctx.DrawRectangle(null, amber, Screen(k.Rect));
        var edge = new Pen(new SolidColorBrush(Active), 1.5, new DashStyle([5, 3], 0));
        foreach (var b in vm.Erase.Boxes.Where(b => b.Page == page))
            ctx.DrawRectangle(new SolidColorBrush(Active, 0.06), edge, Screen(b.Rect));
        if (_eraseBox is { } drawing)
            ctx.DrawRectangle(new SolidColorBrush(Active, 0.10), edge, Screen(drawing));
    }

    /// <summary>Sign tool: the signature fields (dashed), the placed signatures as they will print, the box being dragged.</summary>
    private void RenderSign(DrawingContext ctx, MainViewModel vm)
    {
        int page = vm.PageIndex;
        var spot = new Pen(new SolidColorBrush(Color.FromRgb(138, 63, 0)), 1, new DashStyle([4, 3], 0));
        foreach (var s in vm.Sign.Spots.Where(s => s.Page == page))
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(138, 63, 0), 0.06), spot, Screen(s.Rect));
        var edge = new Pen(new SolidColorBrush(Active), 1, new DashStyle([5, 3], 0));
        foreach (var p in vm.Sign.Placed.Where(p => p.Page == page))
        {
            var r = Screen(p.Rect);
            ctx.DrawRectangle(null, edge, r);
            if (vm.Sign.Ink is { } ink) DrawInk(ctx, ink, r);
        }
        if (_eraseBox is { } drawing)
            ctx.DrawRectangle(new SolidColorBrush(Active, 0.10), edge, Screen(drawing));
    }

    /// <summary>The ink fitted and centred in <paramref name="r"/>, as <see cref="Washi.Core.Pages.PdfSigner"/> draws it.</summary>
    private static void DrawInk(DrawingContext ctx, Washi.Core.Pages.Ink ink, Rect r)
    {
        double h = Math.Min(r.Height, r.Width / Math.Max(ink.Aspect, 0.01)), w = h * ink.Aspect;
        double ox = r.X + (r.Width - w) / 2, oy = r.Y + (r.Height - h) / 2;
        var pen = new Pen(Ink, Math.Clamp(h * 0.035, 0.6, 3), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var s in ink.Strokes)
        {
            if (s.Count == 0) continue;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(ox + s[0].X * w, oy + s[0].Y * h), false);
                foreach (var p in s.Skip(1)) c.LineTo(new Point(ox + p.X * w, oy + p.Y * h));
                if (s.Count == 1) c.LineTo(new Point(ox + s[0].X * w + 0.5, oy + s[0].Y * h));
                c.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, g);
        }
    }

    private static PdfRect Normal(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is { HasDocument: true } ev && (ev.IsEraseTool || ev.IsSignTool) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _start = ToPage(e.GetPosition(this));
            _eraseBox = Normal(_start, _start);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (Editor is not { IsFormTool: true } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var screen = e.GetPosition(this);
        _start = ToPage(screen);

        if (vm.PreviewMode)
        {
            if (HitField(screen) is { } target) vm.ActivateField(target);
            else vm.SelectedField = null;
            e.Handled = true;
            return;
        }

        if (vm.SelectedField is { } sel && HitHandle(sel, screen) is { } corner)
        {
            Begin(Drag.Resize, sel, corner);
        }
        else if (!vm.DrawMode && HitField(screen) is { } hit)
        {
            vm.SelectedField = hit;
            Begin(Drag.Move, hit, default);
        }
        else if (vm.DrawMode)
        {
            var created = vm.AddField(new PdfRect(_start.X, _start.Y, _start.X, _start.Y));
            Begin(Drag.Create, created, (false, false));
        }
        else
        {
            vm.SelectedField = null;
            return;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void Begin(Drag kind, FieldViewModel f, (bool, bool) corner)
    {
        _drag = kind;
        _dragged = f;
        _startRect = f.Rect;
        _corner = corner;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);
        if (_eraseBox is not null)
        {
            _eraseBox = Normal(_start, ToPage(screen));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_dragged is not { } f || _drag == Drag.None)
        {
            UpdateCursor(screen);
            return;
        }
        var p = ToPage(screen);
        double dx = p.X - _start.X, dy = p.Y - _start.Y;
        var r = _startRect;
        if (_drag == Drag.Move)
        {
            f.SetRect(new PdfRect(r.X0 + dx, r.Y0 + dy, r.X1 + dx, r.Y1 + dy));
        }
        else
        {
            double x0 = r.X0, y0 = r.Y0, x1 = r.X1, y1 = r.Y1;
            if (_corner.Left) x0 += dx; else x1 += dx;
            if (_corner.Top) y0 += dy; else y1 += dy;
            f.SetRect(new PdfRect(x0, y0, x1, y1));
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_eraseBox is { } box)
        {
            _eraseBox = null;
            e.Pointer.Capture(null);
            if (box.Width >= MinSizePt && box.Height >= MinSizePt && Editor is { } ed)
            {
                if (ed.IsSignTool) ed.Sign.Place(box);
                else _ = ed.Erase.AddBoxAsync(box);
            }
            InvalidateVisual();
            return;
        }
        if (_dragged is { } f && Editor is { } vm)
        {
            bool tooSmall = f.X1 - f.X0 < MinSizePt || f.Y1 - f.Y0 < MinSizePt;
            if (_drag == Drag.Create && tooSmall)
            {
                vm.Fields.Remove(f);
                vm.SelectedField = null;
            }
            else if (tooSmall)
            {
                f.SetRect(_startRect);   // a resize that collapsed the box is undone
            }
        }
        _drag = Drag.None;
        _dragged = null;
        e.Pointer.Capture(null);
    }

    private void UpdateCursor(Point screen)
    {
        if (Editor is not { } vm) return;
        StandardCursorType type = StandardCursorType.Arrow;
        if (vm.SelectedField is { } sel && HitHandle(sel, screen) is { } c)
            type = c.Left == c.Top ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner;
        else if (vm.PreviewMode && HitField(screen) is { } over)
            type = over.Kind is FieldKind.Checkbox or FieldKind.Radio or FieldKind.Choice ? StandardCursorType.Hand
                 : over.Kind == FieldKind.Signature ? StandardCursorType.Arrow : StandardCursorType.Ibeam;
        else if (vm.PreviewMode) type = StandardCursorType.Arrow;
        else if (vm.DrawMode || vm.IsEraseTool || vm.IsSignTool) type = StandardCursorType.Cross;
        else if (HitField(screen) is not null) type = StandardCursorType.SizeAll;
        Cursor = new Cursor(type);
    }

    // ------------------------------------------------------------------ keyboard
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Editor is not { } vm || vm.SelectedField is not { } f) return;
        if (vm.PreviewMode)
        {
            switch (e.Key)
            {
                case Key.Delete or Key.Back: f.Value = ""; break;              // clears the value, keeps the field
                case Key.Space or Key.Enter: vm.ActivateField(f); break;
                case Key.Escape: vm.SelectedField = null; break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 0.5;
        switch (e.Key)
        {
            case Key.Delete or Key.Back: vm.DeleteSelectedCommand.Execute(null); break;
            case Key.Left: f.Offset(-step, 0); break;
            case Key.Right: f.Offset(step, 0); break;
            case Key.Up: f.Offset(0, -step); break;
            case Key.Down: f.Offset(0, step); break;
            case Key.Escape: vm.SelectedField = null; break;
            default: return;
        }
        e.Handled = true;
    }
}
