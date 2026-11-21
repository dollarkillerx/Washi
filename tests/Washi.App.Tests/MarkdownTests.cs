// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Washi.App.Controls;

namespace Washi.App.Tests;

public sealed class MarkdownTests
{
    const string Answer = """
        我仍然无法访问这台机器：`list_hosts` 返回空，说明它的 **AI 策略还没有设置**。

        ## 下一步
        1. 打开主机设置
        2. 把 AI 策略设为 *ReadOnly*
           之后我就可以直接查询。
        - 也可以手动运行：

        ```bash
        uname -a; free -h
        df -hT | grep -v tmpfs
        ```
        > 注意：不要以 root 运行未知脚本。
        ---
        详见 [文档](https://example.com/docs)。
        """;

    [Fact]
    public void Blocks_are_recognised()
    {
        var blocks = MarkdownParser.Parse(Answer);
        Assert.Equal(
            [MdBlockKind.Paragraph, MdBlockKind.Heading, MdBlockKind.ListItem, MdBlockKind.ListItem, MdBlockKind.ListItem,
             MdBlockKind.Code, MdBlockKind.Quote, MdBlockKind.Rule, MdBlockKind.Paragraph],
            blocks.Select(b => b.Kind));
        Assert.Equal(("1.", "打开主机设置"), (blocks[2].Marker, blocks[2].Text));
        Assert.Equal("把 AI 策略设为 *ReadOnly*\n之后我就可以直接查询。", blocks[3].Text); // continuation line joins the item
        Assert.Equal("•", blocks[4].Marker);
        Assert.Equal(("bash", "uname -a; free -h\ndf -hT | grep -v tmpfs"), (blocks[5].Language, blocks[5].Text));
    }

    [Fact]
    public void An_unterminated_fence_while_streaming_is_code_to_the_end()
    {
        var blocks = MarkdownParser.Parse("Run:\n```sh\nsystemctl status nginx\n");
        Assert.Equal(MdBlockKind.Code, blocks[^1].Kind);
        Assert.Equal("systemctl status nginx\n", blocks[^1].Text);
    }

    [Fact]
    public void Pipe_tables()
    {
        const string md = """
            **203.0.113.10 配置概览**

            | 项目 | 情况 |
            |---|:---|
            | 系统 | Rocky Linux 9.3 |
            | 网络 | eth0：`a|b` IPv6 2a14::3e/64 |
            | 磁盘 | 30 G \| 已用 2.2 G |
            | 缺列 |

            之后的段落
            """;
        var blocks = MarkdownParser.Parse(md);
        Assert.Equal([MdBlockKind.Paragraph, MdBlockKind.Table, MdBlockKind.Paragraph], blocks.Select(b => b.Kind));
        var rows = blocks[1].Rows!;
        Assert.Equal(["项目", "情况"], rows[0]);
        Assert.Equal(["网络", "eth0：`a|b` IPv6 2a14::3e/64"], rows[2]);
        Assert.Equal(["磁盘", "30 G | 已用 2.2 G"], rows[3]);
        Assert.Equal(["缺列", ""], rows[4]);
        // Not a table without the separator line.
        Assert.Equal(MdBlockKind.Paragraph, Assert.Single(MarkdownParser.Parse("| a | b |\n| c | d |")).Kind);
    }

    [Fact]
    public void Inline_spans()
    {
        var spans = MarkdownParser.ParseInline("a `x*y` **b** *c* [d](https://e) f_g_h 2*3*4 \\*lit\\*");
        Assert.Equal(
            [(MdSpanKind.Text, "a "), (MdSpanKind.Code, "x*y"), (MdSpanKind.Text, " "), (MdSpanKind.Bold, "b"), (MdSpanKind.Text, " "),
             (MdSpanKind.Italic, "c"), (MdSpanKind.Text, " "), (MdSpanKind.Link, "d"), (MdSpanKind.Text, " f_g_h 2"), (MdSpanKind.Italic, "3"),
             (MdSpanKind.Text, "4 *lit*")],
            spans.Select(s => (s.Kind, s.Text)));
        Assert.Equal("https://e", spans[7].Url);
    }

    [AvaloniaFact]
    public void Answers_render_bold_tables_and_copyable_code()
    {
        var view = new MarkdownView { Markdown = Answer + "\n\n| 页 | 表格 |\n|---|---|\n| 1 | **令和9年分** |" };
        var w = new Window { Content = view, Width = 420, Height = 900 };
        w.Show();
        view.RenderNow();
        Assert.Single(view.GetLogicalDescendants().OfType<Button>(), b => b.Name == "CopyCode");
        var texts = view.GetLogicalDescendants().OfType<SelectableTextBlock>().ToList();
        var bold = texts[0].Inlines!.OfType<Run>().Single(r => r.Text == "AI 策略还没有设置");
        Assert.Equal(FontWeight.SemiBold, bold.FontWeight);
        // no raw markers reach the screen
        Assert.DoesNotContain(texts.SelectMany(t => t.Inlines!.OfType<Run>()), r => r.Text!.Contains("**") || r.Text.StartsWith('|'));
        Assert.Contains(texts.SelectMany(t => t.Inlines!.OfType<Run>()), r => r.Text == "令和9年分" && r.FontWeight == FontWeight.SemiBold);
        w.Close();
    }

    [AvaloniaFact]
    public void Streaming_keeps_finished_blocks()
    {
        var view = new MarkdownView { Markdown = "First paragraph.\n\nSecond" };
        var w = new Window { Content = view, Width = 420, Height = 400 };
        w.Show();
        view.RenderNow();
        var first = view.Children[0];
        view.Markdown = "First paragraph.\n\nSecond, longer";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(first, view.Children[0]);
        Assert.Equal(2, view.Children.Count);
        w.Close();
    }
}
