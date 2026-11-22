// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Washi.Core.Pages;

namespace Washi.App.Controls;

/// <summary>
/// A pad to sign on with a mouse, trackpad, pen or finger. Shows <see cref="Ink"/> (fitted, centred) and raises
/// <see cref="InkCompleted"/> with the whole drawing after each stroke.
/// </summary>
public sealed class InkPad : Control
{
    public static readonly StyledProperty<Ink?> InkProperty = AvaloniaProperty.Register<InkPad, Ink?>(nameof(Ink));

    public Ink? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }

    /// <summary>The drawing after a stroke ended.</summary>
    public event Action<Ink>? InkCompleted;

    private readonly List<List<(double X, double Y)>> _strokes = [];
    private List<(double X, double Y)>? _current;

    static InkPad() => AffectsRender<InkPad>(InkProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // cleared from outside (or replaced by the remembered one): start a fresh drawing
        if (change.Property == InkProperty && _current is null && change.NewValue is null) _strokes.Clear();
    }

    private static readonly IPen Rule = new Pen(new SolidColorBrush(Color.FromRgb(198, 198, 198)), 1);
    private static readonly IBrush InkBrush = new SolidColorBrush(Color.FromRgb(22, 22, 22));

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(Brushes.White, b);
        ctx.DrawLine(Rule, new Point(12, b.Height * 0.75), new Point(b.Width - 12, b.Height * 0.75));   // the line to sign on
        var pen = new Pen(InkBrush, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (_strokes.Count > 0 || _current is not null)
        {
            foreach (var s in _strokes.Append(_current ?? [])) DrawStroke(ctx, pen, s.Select(p => new Point(p.X, p.Y)).ToList());
            return;
        }
        if (Ink is not { IsEmpty: false } ink) return;
        // a remembered signature: fitted to the pad with a margin
        double pad = 12, h = Math.Min(b.Height - 2 * pad, (b.Width - 2 * pad) / ink.Aspect), w = h * ink.Aspect;
        double ox = (b.Width - w) / 2, oy = (b.Height - h) / 2;
        foreach (var s in ink.Strokes) DrawStroke(ctx, pen, s.Select(p => new Point(ox + p.X * w, oy + p.Y * h)).ToList());
    }

    private static void DrawStroke(DrawingContext ctx, IPen pen, List<Point> pts)
    {
        if (pts.Count == 0) return;
        if (pts.Count == 1) { ctx.DrawEllipse(pen.Brush, null, pts[0], 1.2, 1.2); return; }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false);
            for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i]);
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_strokes.Count == 0 && Ink is not null) SetCurrentValue(InkProperty, null);   // drawing over a remembered signature replaces it
        var p = e.GetPosition(this);
        _current = [(p.X, p.Y)];
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_current is null) return;
        var p = e.GetPosition(this);
        var last = _current[^1];
        if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 1.5) return;   // fewer, smoother samples
        _current.Add((Math.Clamp(p.X, 0, Bounds.Width), Math.Clamp(p.Y, 0, Bounds.Height)));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_current is null) return;
        _strokes.Add(_current);
        _current = null;
        e.Pointer.Capture(null);
        var ink = Washi.Core.Pages.Ink.From(_strokes.Select(s => (IReadOnlyList<(double, double)>)s));
        SetCurrentValue(InkProperty, ink);
        InkCompleted?.Invoke(ink);
        InvalidateVisual();
    }
}
