// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Washi.App.Controls;

/// <summary>
/// A line icon: a 20 × 20 geometry stroked in the inherited text colour, so it follows the
/// button it sits in (hover, pressed, disabled, selected) without extra styles.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 20);

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, SizeProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
        VerticalAlignmentProperty.OverrideDefaultValue<Icon>(Avalonia.Layout.VerticalAlignment.Center);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is null) return;
        var brush = TextElement.GetForeground(this) ?? Brushes.Black;
        double k = Size / 20;
        using (context.PushTransform(Matrix.CreateScale(k, k)))
            context.DrawGeometry(null, new Pen(brush, 1.5, lineCap: PenLineCap.Square, lineJoin: PenLineJoin.Miter), Data);
    }
}
