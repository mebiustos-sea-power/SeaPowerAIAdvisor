using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

// Translate Markdown into fixed Noesis elements; only resolved local unit references become actions.
internal static class AdvisorMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UsePipeTables().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseCjkFriendlyEmphasis().Build();

    internal static XElement Render(string markdown, Func<string, XElement, XElement>? unitLink = null)
    {
        var root = Element("StackPanel",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute("TextElement.FontSize", "14"), Blocks(Markdown.Parse(markdown, Pipeline), unitLink));
        return root;
    }

    private static IEnumerable<XElement> Blocks(ContainerBlock container, Func<string, XElement, XElement>? unitLink, bool compact = false)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var size = heading.Level switch { 1 => 22, 2 => 20, 3 => 18, 4 => 16, _ => 14 };
                    yield return Paragraph(heading, false, unitLink, new XAttribute("FontSize", size),
                        new XAttribute("FontWeight", "Bold"));
                    break;
                case ParagraphBlock paragraph:
                    yield return Paragraph(paragraph, compact, unitLink);
                    break;
                case CodeBlock code:
                    yield return Element("Border", new XAttribute("Background", "#18212C"),
                        new XAttribute("Padding", "8"), new XAttribute("Margin", "0,0,0,10"),
                        Element("ScrollViewer", new XAttribute("HorizontalScrollBarVisibility", "Auto"),
                            new XAttribute("VerticalScrollBarVisibility", "Disabled"),
                            Element("TextBlock", new XAttribute("FontSize", "13"),
                                new XAttribute("TextWrapping", "NoWrap"), Text("Text", code.Lines.ToString()))));
                    break;
                case QuoteBlock quote:
                    yield return Element("Border", new XAttribute("BorderBrush", "#607080"),
                        new XAttribute("BorderThickness", "3,0,0,0"), new XAttribute("Padding", "10,0,0,0"),
                        new XAttribute("Margin", "0,2,0,10"), Element("StackPanel", Blocks(quote, unitLink)));
                    break;
                case ListBlock list:
                    var number = list.IsOrdered ? int.Parse(list.OrderedStart ?? "1", CultureInfo.InvariantCulture) : 0;
                    foreach (var item in list.Cast<ListItemBlock>())
                    {
                        var marker = list.IsOrdered ? (number++).ToString(CultureInfo.InvariantCulture) + list.OrderedDelimiter : "•";
                        yield return Element("Grid", new XAttribute("Margin", "0,0,0,4"),
                            Element("Grid.ColumnDefinitions", Element("ColumnDefinition", new XAttribute("Width", "Auto")),
                                Element("ColumnDefinition", new XAttribute("Width", "*"))),
                            Element("TextBlock", Text("Text", marker), new XAttribute("MinWidth", "18"),
                                new XAttribute("Margin", "0,0,6,0")),
                            Element("StackPanel", new XAttribute("Grid.Column", "1"), Blocks(item, unitLink, !list.IsLoose)));
                    }
                    break;
                case ThematicBreakBlock:
                    yield return Element("Border", new XAttribute("Height", "1"),
                        new XAttribute("Background", "#607080"), new XAttribute("Margin", "0,6,0,12"));
                    break;
                case Table table:
                    yield return RenderTable(table, unitLink);
                    break;
                case LinkReferenceDefinitionGroup:
                    break;
                default:
                    throw new NotSupportedException("Unsupported Markdown block: " + block.GetType().Name);
            }
        }
    }

    private static XElement Paragraph(LeafBlock block, bool compact, Func<string, XElement, XElement>? unitLink, params object[] attributes) =>
        Element("TextBlock", new XAttribute("TextWrapping", "Wrap"),
            new XAttribute("Margin", compact ? "0,0,0,3" : "0,0,0,10"), attributes, Inlines(block.Inline, unitLink));

    private static IEnumerable<XElement> Inlines(ContainerInline? container, Func<string, XElement, XElement>? unitLink)
    {
        for (var inline = container?.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    yield return Run(literal.Content.ToString());
                    break;
                case CodeInline code:
                    yield return Element("Span", new XAttribute("Foreground", "#E0C99F"), Run(code.Content));
                    break;
                case EmphasisInline emphasis:
                    yield return Element("Span", emphasis.DelimiterChar == '~'
                        ? new XAttribute("TextDecorations", "Strikethrough")
                        : emphasis.DelimiterCount >= 2 ? new XAttribute("FontWeight", "Bold")
                        : new XAttribute("FontStyle", "Italic"), Inlines(emphasis, unitLink));
                    break;
                case LineBreakInline line:
                    yield return line.IsHard ? Element("LineBreak") : Run(" ");
                    break;
                case LinkInline link:
                    var label = Element("Span", Inlines(link, unitLink));
                    if (!link.IsImage && link.Url?.StartsWith("unit:", StringComparison.OrdinalIgnoreCase) == true)
                        yield return unitLink == null ? label : unitLink(link.Url.Substring(5), label);
                    else
                    {
                        // External links and image references remain text; no URL is opened or image loaded.
                        yield return label;
                        if (!string.IsNullOrEmpty(link.Url)) yield return Run(" (" + link.Url + ")");
                    }
                    break;
                case AutolinkInline link:
                    yield return Run(link.Url);
                    break;
                case HtmlEntityInline entity:
                    yield return Run(entity.Transcoded.ToString());
                    break;
                default:
                    throw new NotSupportedException("Unsupported Markdown inline: " + inline.GetType().Name);
            }
        }
    }

    private static XElement RenderTable(Table table, Func<string, XElement, XElement>? unitLink)
    {
        var rows = table.Cast<TableRow>().ToArray();
        var columns = table.ColumnDefinitions.Count;
        var grid = Element("Grid", new XAttribute("MinWidth", columns * 110),
            new XAttribute("Width", "{Binding ViewportWidth, RelativeSource={RelativeSource AncestorType={x:Type ScrollViewer}}}"),
            Element("Grid.ColumnDefinitions", Enumerable.Range(0, columns).Select(_ =>
                Element("ColumnDefinition", new XAttribute("Width", "*")))),
            Element("Grid.RowDefinitions", rows.Select(_ => Element("RowDefinition", new XAttribute("Height", "Auto")))));
        for (var row = 0; row < rows.Length; row++)
        {
            var column = 0;
            foreach (var cell in rows[row].Cast<TableCell>())
            {
                var index = cell.ColumnIndex < 0 ? column : cell.ColumnIndex;
                var alignment = table.ColumnDefinitions[index].Alignment switch
                {
                    TableColumnAlign.Center => "Center", TableColumnAlign.Right => "Right", _ => "Left"
                };
                var blocks = Blocks(cell, unitLink, true).ToArray();
                foreach (var paragraph in blocks.Where(b => b.Name.LocalName == "TextBlock"))
                {
                    paragraph.SetAttributeValue("TextAlignment", alignment);
                    if (rows[row].IsHeader) paragraph.SetAttributeValue("FontWeight", "Bold");
                }
                grid.Add(Element("Border", new XAttribute("Grid.Row", row), new XAttribute("Grid.Column", index),
                    new XAttribute("Grid.ColumnSpan", cell.ColumnSpan), new XAttribute("BorderBrush", "#607080"),
                    new XAttribute("BorderThickness", "1"), new XAttribute("Padding", "6"),
                    new XAttribute("Background", rows[row].IsHeader ? "#293B50" : "Transparent"),
                    Element("StackPanel", blocks)));
                column = index + cell.ColumnSpan;
            }
        }
        return Element("ScrollViewer", new XAttribute("HorizontalScrollBarVisibility", "Auto"),
            new XAttribute("VerticalScrollBarVisibility", "Disabled"), new XAttribute("Margin", "0,0,0,10"), grid);
    }

    private static XElement Run(string value) => Element("Run", Text("Text", value));

}
