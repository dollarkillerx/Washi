// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Washi.Core.Localization;

namespace Washi.App.Controls;

/// <summary>
/// Renders <see cref="Markdown"/> (see <see cref="MarkdownParser"/>) with Washi's palette, after MySSH's AI panel. Code
/// blocks get a Copy button. Updates are coalesced and incremental: the controls of leading blocks that did not change are
/// kept, so a streaming answer only rebuilds its last, growing block, and a selection in earlier paragraphs survives.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly FontFamily Mono = new("Menlo, Consolas, monospace");
    private bool _pending;
    private string? _rendered;
    private IReadOnlyList<MdBlock> _blocks = [];

    public MarkdownView() => Spacing = 8;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Schedule();
    }

    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            if (_rendered == Markdown && Children.Count > 0) return;
            _rendered = Markdown;
            Render(full: false);
        }, DispatcherPriority.Background);
    }

    /// <summary>Renders everything now (tests, first layout).</summary>
    public void RenderNow()
    {
        _pending = false;
        _rendered = Markdown;
        Render(full: true);
    }

    private void Render(bool full)
    {
        var blocks = MarkdownParser.Parse(Markdown);
        var keep = 0;
        if (!full)
            while (keep < blocks.Count && keep < _blocks.Count && keep < Children.Count && SameBlock(blocks[keep], _blocks[keep]))
                keep++;
        if (keep < Children.Count) Children.RemoveRange(keep, Children.Count - keep);
        for (var i = keep; i < blocks.Count; i++) Children.Add(Build(blocks[i]));
        _blocks = blocks;
    }

    private static bool SameBlock(MdBlock a, MdBlock b) =>
        a.Kind == b.Kind && a.Text == b.Text && a.Level == b.Level && a.Marker == b.Marker && a.Language == b.Language
        && (ReferenceEquals(a.Rows, b.Rows) || (a.Rows is not null && b.Rows is not null && a.Rows.Count == b.Rows.Count
            && a.Rows.Zip(b.Rows).All(r => r.First.SequenceEqual(r.Second))));

    private Control Build(MdBlock block) => block.Kind switch
    {
        MdBlockKind.Code => CodeBlock(block),
        MdBlockKind.Table => Table(block),
        MdBlockKind.Heading => Heading(block),
        MdBlockKind.ListItem => ListItem(block),
        MdBlockKind.Quote => Quote(block),
        MdBlockKind.Rule => new Border { Height = 1, Background = Brush("RuleBrush"), Margin = new Thickness(0, 4) },
        _ => Text(block.Text),
    };

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    private SelectableTextBlock Text(string text, double size = 14)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = Math.Round(size * 1.45) };
        if (HasCjk(text)) tb.FontFamily = CjkFamily();
        AddInlines(tb.Inlines!, text);
        return tb;
    }

    /// <summary>
    /// CJK text gets a CJK family first in its stack. Through Inter's fallback, Avalonia reuses one face per character
    /// whatever the weight, so **太字** came out regular (and regular text sometimes bold).
    /// </summary>
    internal static FontFamily CjkFamily() => FontFamily.Parse(L.Culture.Name switch
    {
        "zh-Hans" or "zh-CN" or "zh-SG" => "PingFang SC, Microsoft YaHei UI, Noto Sans CJK SC, $Default",
        "zh-Hant" or "zh-TW" or "zh-HK" => "PingFang TC, Microsoft JhengHei UI, Noto Sans CJK TC, $Default",
        var c when c.StartsWith("ko", StringComparison.Ordinal) => "Apple SD Gothic Neo, Malgun Gothic, Noto Sans CJK KR, $Default",
        _ => "Hiragino Sans, Yu Gothic UI, Noto Sans CJK JP, $Default", // Japanese forms are Washi's home ground
    });

    internal static bool HasCjk(string text)
    {
        foreach (var c in text)
            if (c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯')
                return true;
        return false;
    }

    private SelectableTextBlock Heading(MdBlock block)
    {
        var tb = Text(block.Text, block.Level <= 2 ? 16 : 14);
        tb.FontWeight = FontWeight.SemiBold;
        tb.Margin = new Thickness(0, 4, 0, 0);
        return tb;
    }

    private void AddInlines(InlineCollection inlines, string text)
    {
        foreach (var span in MarkdownParser.ParseInline(text))
        {
            var lines = span.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) inlines.Add(new LineBreak());
                if (lines[i].Length == 0) continue;
                var run = new Run(lines[i]);
                switch (span.Kind)
                {
                    case MdSpanKind.Bold:
                        run.FontWeight = FontWeight.SemiBold;
                        break;
                    case MdSpanKind.Italic:
                        run.FontStyle = FontStyle.Italic;
                        break;
                    case MdSpanKind.Code:
                        run.FontFamily = Mono;
                        run.FontSize = 12.5;
                        run.Background = Brush("MistBrush");
                        break;
                    case MdSpanKind.Link:
                        run.Foreground = Brush("BlueBrush");
                        run.TextDecorations = TextDecorations.Underline;
                        break;
                }
                inlines.Add(run);
            }
        }
    }

    private Grid ListItem(MdBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(block.Level * 16, 0, 0, 0) };
        grid.Children.Add(new TextBlock { Text = block.Marker ?? "•", Margin = new Thickness(0, 0, 8, 0), MinWidth = 12, FontSize = 14, LineHeight = 20 });
        var text = Text(block.Text);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private Border Quote(MdBlock block)
    {
        var text = Text(block.Text);
        text.Foreground = Brush("MutedBrush");
        return new Border
        {
            BorderBrush = Brush("RuleBrush"), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0), Child = text,
        };
    }

    /// <summary>
    /// Header on mist, subtle row rules, cells wrap. With up to three columns the last ones share the width (the panel is
    /// narrow); wider tables keep their natural width and scroll sideways.
    /// </summary>
    private Control Table(MdBlock block)
    {
        var rows = block.Rows ?? [];
        var columns = rows.Count == 0 ? 0 : rows[0].Count;
        var fit = columns <= 3;
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(fit && c > 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto)
            {
                MaxWidth = fit && c == 0 && columns > 1 ? 120 : double.PositiveInfinity,
            });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var background = new Border
            {
                Background = r == 0 ? Brush("MistBrush") : null,
                BorderBrush = Brush("RuleBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            Grid.SetRow(background, r);
            Grid.SetColumnSpan(background, Math.Max(1, columns));
            grid.Children.Add(background);
            for (var c = 0; c < columns; c++)
            {
                var cell = Text(rows[r][c], 13);
                if (r == 0) cell.FontWeight = FontWeight.SemiBold;
                cell.Margin = new Thickness(8, 6);
                cell.TextWrapping = fit ? TextWrapping.Wrap : TextWrapping.NoWrap;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        if (fit) return grid;
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private Border CodeBlock(MdBlock block)
    {
        var code = new SelectableTextBlock
        {
            Text = block.Text, Margin = new Thickness(12, 2, 12, 10), TextWrapping = TextWrapping.NoWrap, FontFamily = Mono, FontSize = 12,
        };

        var header = new DockPanel { Margin = new Thickness(12, 2, 2, 0) };
        var copy = new Button
        {
            Name = "CopyCode", Content = L.Get("Ui_Copy"), Background = Brushes.Transparent, Padding = new Thickness(8, 2), FontSize = 12,
        };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(block.Text);
            copy.Content = "✓";
            DispatcherTimer.RunOnce(() => copy.Content = L.Get("Ui_Copy"), TimeSpan.FromSeconds(1.5));
        };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock
        {
            Text = block.Language ?? "", VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Brush("MutedBrush"),
        });

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = code,
        });
        return new Border { Background = Brush("MistBrush"), CornerRadius = new CornerRadius(4), Child = panel };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RenderNow();
    }
}
