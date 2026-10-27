using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AXamlIDE.Views;

/// <summary>
/// Turns Markdown into Avalonia controls for the Markdown preview (MarkdownPreviewView), reading Markdig's syntax tree:
/// headings, paragraphs, emphasis, code (inline and blocks), links, local images, lists (with task check boxes),
/// quotes, tables and rules. GitHub-flavoured extensions are on (tables, task lists, strikethrough, autolinks).
///
/// The look is in Styles/MarkdownStyles.axaml, by class (md-h1, md-code, md-link...); this only builds the tree.
/// Text is selectable. A link is a Run, found under the pointer by hit-testing the text, so it wraps like text.
/// </summary>
public sealed class MarkdownRenderer(string baseFolder, Action<string> openLink)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>Headings by their id (for "#section" links), from the last render.</summary>
    public Dictionary<string, Control> Anchors { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Control Render(string markdown)
    {
        Anchors.Clear();
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var body = new StackPanel { Classes = { "md-body" } };
        AddBlocks(body, document);
        return body;
    }

    // ------------------------------------------------------------- blocks

    private void AddBlocks(Panel panel, ContainerBlock container)
    {
        foreach (var block in container)
            if (RenderBlock(block) is { } control)
                panel.Children.Add(control);
    }

    private Control? RenderBlock(Block block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Paragraph(paragraph),
        FencedCodeBlock or CodeBlock => CodeBlockView((LeafBlock)block),
        QuoteBlock quote => Quote(quote),
        ListBlock list => List(list),
        Table table => TableView(table),
        ThematicBreakBlock => new Border { Classes = { "md-rule" } },
        HtmlBlock html => CodeBlockView(html),   // shown as its source: the preview doesn't run HTML
        ContainerBlock other => Container(other),
        _ => null
    };

    private Control Heading(HeadingBlock heading)
    {
        var text = TextFor(heading.Inline, "md-h" + Math.Clamp(heading.Level, 1, 6));
        if (heading.GetAttributes().Id is { Length: > 0 } id)
            Anchors.TryAdd(id, text);
        return text;
    }

    private Control Paragraph(ParagraphBlock paragraph)
    {
        // An image on its own line is shown as a block, at its size (up to the preview's width).
        if (paragraph.Inline?.FirstChild is LinkInline { IsImage: true } image && image.NextSibling is null && LocalImage(image) is { } picture)
            return picture;
        return TextFor(paragraph.Inline, "md-p");
    }

    private static Control CodeBlockView(LeafBlock block)
    {
        var lines = block.Lines.Lines.Take(block.Lines.Count).Select(line => line.Slice.ToString());
        var code = new SelectableTextBlock { Classes = { "md-code-block" }, Text = string.Join("\n", lines) };
        return new Border
        {
            Classes = { "md-code-block" },
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = code
            }
        };
    }

    private Control Quote(QuoteBlock quote)
    {
        var inner = new StackPanel();
        AddBlocks(inner, quote);
        return new Border { Classes = { "md-quote" }, Child = inner };
    }

    private Control Container(ContainerBlock block)
    {
        var inner = new StackPanel();
        AddBlocks(inner, block);
        return inner;
    }

    private Control List(ListBlock list)
    {
        var panel = new StackPanel { Classes = { "md-list" } };
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var content = new StackPanel();
            AddBlocks(content, item);

            // A task list item ("- [x] done") shows a check box instead of the bullet.
            var task = (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild as TaskList;
            var marker = new TextBlock
            {
                Classes = { "md-marker" },
                Text = task is not null ? (task.Checked ? "☑" : "☐") : list.IsOrdered ? $"{number}{list.OrderedDelimiter}" : "•"
            };
            number++;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(marker);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Control TableView(Table table)
    {
        var columns = table.ColumnDefinitions.Count > 0 ? table.ColumnDefinitions.Count : table.OfType<TableRow>().Select(row => row.Count).DefaultIfEmpty(1).Max();
        var grid = new Grid { Classes = { "md-table" }, HorizontalAlignment = HorizontalAlignment.Left };
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var columnIndex = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                if (columnIndex >= columns)
                    break;
                var content = new StackPanel();
                AddBlocks(content, cell);
                var alignment = columnIndex < table.ColumnDefinitions.Count ? table.ColumnDefinitions[columnIndex].Alignment : null;
                content.HorizontalAlignment = alignment switch
                {
                    TableColumnAlign.Center => HorizontalAlignment.Center,
                    TableColumnAlign.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left
                };
                var border = new Border { Classes = { "md-cell" }, Child = content };
                if (row.IsHeader)
                    border.Classes.Add("md-header");
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, columnIndex);
                grid.Children.Add(border);
                columnIndex += Math.Max(1, cell.ColumnSpan);
            }
            rowIndex++;
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid
        };
    }

    // ------------------------------------------------------------- inlines

    /// <summary>A block of text: its inlines, with the links found by where they are in the text.</summary>
    private SelectableTextBlock TextFor(ContainerInline? inlines, string className)
    {
        var text = new SelectableTextBlock { Classes = { className }, Inlines = [] };
        var links = new List<(int Start, int End, string Url)>();
        if (inlines is not null)
            AddInlines(text.Inlines!, inlines, links, []);

        if (links.Count > 0)
        {
            string? LinkAt(PointerEventArgs e)
            {
                var position = text.TextLayout.HitTestPoint(e.GetPosition(text) - new Point(text.Padding.Left, text.Padding.Top)).TextPosition;
                return links.FirstOrDefault(link => position >= link.Start && position < link.End).Url;
            }
            text.PointerMoved += (_, e) => text.Cursor = LinkAt(e) is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            text.PointerReleased += (_, e) =>
            {
                // A click, not the end of a selection drag.
                if (e.InitialPressMouseButton == MouseButton.Left && text.SelectionStart == text.SelectionEnd && LinkAt(e) is { } url)
                    openLink(url);
            };
        }
        return text;
    }

    private void AddInlines(InlineCollection target, ContainerInline container, List<(int, int, string)> links, string[] classes)
    {
        foreach (var inline in container)
            AddInline(target, inline, links, classes);
    }

    private void AddInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline, List<(int, int, string)> links, string[] classes)
    {
        switch (inline)
        {
            case LiteralInline literal:
                Add(target, new Run(literal.Content.ToString()), classes);
                break;
            case HtmlEntityInline entity:
                Add(target, new Run(entity.Transcoded.ToString()), classes);
                break;
            case CodeInline code:
                Add(target, new Run(code.Content), [.. classes, "md-code"]);
                break;
            case LineBreakInline lineBreak:
                if (lineBreak.IsHard)
                    target.Add(new LineBreak());
                else
                    Add(target, new Run(" "), classes);
                break;
            case TaskList:
                break;   // shown as the list item's check box
            case EmphasisInline emphasis:
            {
                var style = emphasis.DelimiterChar == '~' ? "md-strike" : emphasis.DelimiterCount >= 2 ? "md-strong" : "md-em";
                AddInlines(target, emphasis, links, [.. classes, style]);
                break;
            }
            case LinkInline { IsImage: true } image:
                if (LocalImage(image) is { } picture)
                    target.Add(new InlineUIContainer(picture));
                else
                    AddLink(target, image.Url ?? "", $"[{PlainText(image)}]", links, classes);
                break;
            case LinkInline link:
            {
                var start = TextLength(target);
                AddInlines(target, link, links, [.. classes, "md-link"]);
                if (!string.IsNullOrEmpty(link.Url))
                    links.Add((start, TextLength(target), link.Url));
                break;
            }
            case AutolinkInline auto:
                AddLink(target, auto.IsEmail ? "mailto:" + auto.Url : auto.Url, auto.Url, links, classes);
                break;
            case HtmlInline html:
                Add(target, new Run(html.Tag), [.. classes, "md-code"]);
                break;
            case ContainerInline nested:
                AddInlines(target, nested, links, classes);
                break;
        }
    }

    private static void AddLink(InlineCollection target, string url, string text, List<(int, int, string)> links, string[] classes)
    {
        var start = TextLength(target);
        Add(target, new Run(text), [.. classes, "md-link"]);
        if (url.Length > 0)
            links.Add((start, TextLength(target), url));
    }

    private static void Add(InlineCollection target, Run run, string[] classes)
    {
        run.Classes.AddRange(classes);
        target.Add(run);
    }

    /// <summary>Characters so far, as the text's layout counts them (a line break is a newline).</summary>
    private static int TextLength(InlineCollection inlines) => inlines.Text?.Length ?? 0;

    private static string PlainText(ContainerInline container)
        => string.Concat(container.Select(inline => inline switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            ContainerInline nested => PlainText(nested),
            _ => ""
        }));

    /// <summary>An image from a file beside the Markdown (or under it); null for web images and formats it can't show (SVG).</summary>
    private Image? LocalImage(LinkInline image)
    {
        if (string.IsNullOrEmpty(image.Url) || image.Url.Contains("://", StringComparison.Ordinal) || image.Url.StartsWith("data:", StringComparison.Ordinal))
            return null;
        var path = Path.GetFullPath(Path.Combine(baseFolder, Uri.UnescapeDataString(image.Url.Split('#', '?')[0])));
        if (!File.Exists(path) || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var bitmap = new Bitmap(path);
            var picture = new Image { Classes = { "md-image" }, Source = bitmap, MaxWidth = bitmap.Size.Width, Stretch = Stretch.Uniform };
            ToolTip.SetTip(picture, PlainText(image) is { Length: > 0 } alt ? alt : null);
            return picture;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
