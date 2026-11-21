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
        vm.Fields.CollectionChanged += OnFieldsChanged;
        foreach (var f in vm.Fields) f.PropertyChanged += OnFieldChanged;
    }

    private void Unhook(MainViewModel vm)
    {
        vm.PropertyChanged -= OnEditorChanged;
        vm.Fields.CollectionChanged -= OnFieldsChanged;
        foreach (var f in vm.Fields) f.PropertyChanged -= OnFieldChanged;
    }

    private void OnEditorChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedField) or nameof(MainViewModel.PageIndex) or nameof(MainViewModel.DrawMode)
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
    };

    private IEnumerable<FieldViewModel> PageFields =>
        Editor is { } vm ? vm.Fields.Where(f => f.Page == vm.PageIndex) : [];

    private Rect Screen(FieldViewModel f) => new(f.X0 * Zoom, f.Y0 * Zoom, (f.X1 - f.X0) * Zoom, (f.Y1 - f.Y0) * Zoom);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.White, new Rect(Bounds.Size));   // paper, and hit-testable everywhere
        // the page is drawn here too, in the same coordinates as the fields, so they always line up
        if (Editor is { PageImage: { } page } ed)
            ctx.DrawImage(page, new Rect(0, 0, page.PixelSize.Width, page.PixelSize.Height), new Rect(0, 0, ed.PageWidth * Zoom, ed.PageHeight * Zoom));
        var selected = Editor?.SelectedField;
        var typeface = new Typeface("Inter");
        foreach (var f in PageFields)
        {
            var c = KindColors[f.Kind];
            var r = Screen(f);
            bool sel = ReferenceEquals(f, selected);
            ctx.DrawRectangle(new SolidColorBrush(c, sel ? 0.28 : 0.14), new Pen(new SolidColorBrush(c), sel ? 2.2 : 1), r);

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
                    ctx.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(c), 1.5), h);
        }
    }

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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is not { } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var screen = e.GetPosition(this);
        _start = ToPage(screen);

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
        else if (vm.DrawMode) type = StandardCursorType.Cross;
        else if (HitField(screen) is not null) type = StandardCursorType.SizeAll;
        Cursor = new Cursor(type);
    }

    // ------------------------------------------------------------------ keyboard
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Editor is not { } vm || vm.SelectedField is not { } f) return;
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
