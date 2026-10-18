using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Markdig;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;

namespace AXamlIDE.Editor.Hover;

public static class MarkdownContentRenderer
{
    private static readonly Regex HtmlTag = new("</?[A-Za-z][^>]*>", RegexOptions.Compiled);
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static Control Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var content = new StackPanel { Spacing = 8, MaxWidth = 520 };
        AddBlocks(content, document);
        return content;
    }

    public static string ToPlainText(string markdown)
        => Markdown.ToPlainText(markdown, Pipeline);

    private static void AddBlocks(Panel panel, ContainerBlock blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    panel.Children.Add(CreateInlineText(heading.Inline, heading.Level));
                    break;
                case ParagraphBlock paragraph:
                    panel.Children.Add(CreateInlineText(paragraph.Inline));
                    break;
                case FencedCodeBlock fencedCode:
                    panel.Children.Add(CreateCodeBlock(fencedCode.Lines.ToString()));
                    break;
                case CodeBlock code:
                    panel.Children.Add(CreateCodeBlock(code.Lines.ToString()));
                    break;
                case HtmlBlock html:
                    var htmlText = PlainTextFromHtml(html.Lines.ToString());
                    if (!string.IsNullOrWhiteSpace(htmlText))
                        panel.Children.Add(new TextBlock
                        {
                            Text = htmlText.Trim(),
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 13,
                            Foreground = Brushes.Gainsboro
                        });
                    break;
                case ListBlock list:
                    panel.Children.Add(CreateList(list));
                    break;
                case QuoteBlock quote:
                    panel.Children.Add(CreateQuote(quote));
                    break;
                case ThematicBreakBlock:
                    panel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.FromArgb(100, 150, 150, 150)),
                        Margin = new Thickness(0, 4)
                    });
                    break;
                case ContainerBlock nested:
                    var nestedPanel = new StackPanel { Spacing = 6 };
                    AddBlocks(nestedPanel, nested);
                    if (nestedPanel.Children.Count > 0)
                        panel.Children.Add(nestedPanel);
                    break;
            }
        }
    }

    private static TextBlock CreateInlineText(MarkdownInline? inline, int headingLevel = 0)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = headingLevel > 0 ? HeadingSize(headingLevel) : 13,
            FontWeight = headingLevel > 0 ? FontWeight.Bold : FontWeight.Normal,
            Foreground = Brushes.Gainsboro
        };
        if (text.Inlines is { } inlines)
            AddInlines(inlines, inline);
        return text;
    }

    private static double HeadingSize(int level) => level switch
    {
        1 => 20,
        2 => 18,
        3 => 16,
        4 => 15,
        _ => 14
    };

    private static Border CreateCodeBlock(string code)
        => new()
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
            Child = new TextBlock
            {
                Text = code.TrimEnd('\r', '\n'),
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,Monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 504
            }
        };

    private static Control CreateList(ListBlock list)
    {
        var items = new StackPanel { Spacing = 4 };
        var itemNumber = int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            ? start
            : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 6
            };
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{itemNumber++}." : "\u2022",
                MinWidth = 18
            };
            row.Children.Add(marker);
            var itemContent = new StackPanel { Spacing = 4, MaxWidth = 490 };
            AddBlocks(itemContent, item);
            Grid.SetColumn(itemContent, 1);
            row.Children.Add(itemContent);
            items.Children.Add(row);
        }

        return items;
    }

    private static Control CreateQuote(QuoteBlock quote)
    {
        var content = new StackPanel { Spacing = 4 };
        AddBlocks(content, quote);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 120, 150, 190)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Child = content
        };
    }

    private static void AddInlines(InlineCollection target, MarkdownInline? first)
    {
        for (var inline = first; inline is not null; inline = inline.NextSibling)
            AddInline(target, inline, InlineStyle.Normal);
    }

    private static void AddInline(InlineCollection target, MarkdownInline inline, InlineStyle style)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(CreateRun(SliceToString(literal.Content), style));
                break;
            case CodeInline code:
                target.Add(CreateRun(code.Content, style with { IsCode = true }));
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case HtmlEntityInline entity:
                target.Add(CreateRun(SliceToString(entity.Transcoded), style));
                break;
            case LinkInline link:
                var linkStyle = style with { IsLink = !link.IsImage };
                foreach (var child in Enumerate(link))
                    AddInline(target, child, linkStyle);
                break;
            case EmphasisInline emphasis:
                var emphasisStyle = style with
                {
                    IsBold = style.IsBold || emphasis.DelimiterCount > 1,
                    IsItalic = style.IsItalic || emphasis.DelimiterCount == 1
                };
                foreach (var child in Enumerate(emphasis))
                    AddInline(target, child, emphasisStyle);
                break;
            case HtmlInline:
                break;
            case ContainerInline container:
                foreach (var child in Enumerate(container))
                    AddInline(target, child, style);
                break;
        }
    }

    private static IEnumerable<MarkdownInline> Enumerate(ContainerInline container)
    {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
            yield return child;
    }

    private static string SliceToString(StringSlice slice)
        => slice.Length == 0 ? "" : slice.Text.Substring(slice.Start, slice.Length);

    private static string PlainTextFromHtml(string markup)
        => HtmlTag.Replace(WebUtility.HtmlDecode(markup), "");

    private static Run CreateRun(string text, InlineStyle style)
    {
        var run = new Run(text)
        {
            FontWeight = style.IsBold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.IsItalic ? FontStyle.Italic : FontStyle.Normal,
            FontFamily = style.IsCode
                ? new FontFamily("Cascadia Code,Consolas,Menlo,Monospace")
                : FontFamily.Default,
            Foreground = style.IsLink ? Brushes.DeepSkyBlue : null,
            TextDecorations = style.IsLink ? TextDecorations.Underline : null
        };
        return run;
    }

    private readonly record struct InlineStyle(bool IsBold, bool IsItalic, bool IsCode, bool IsLink)
    {
        public static InlineStyle Normal => new(false, false, false, false);
    }
}
