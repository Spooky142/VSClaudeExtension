using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace ClaudeForVS.Markdown;

/// <summary>Converts Markdown into a WPF FlowDocument. Colors come from the hosting control, so it follows the VS theme.</summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");

    // Translucent gray reads as a subtle tint on both light and dark themes.
    private static readonly Brush CodeBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)));
    private static readonly Brush RuleBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)));

    public static FlowDocument Render(string markdown, double baseFontSize)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = baseFontSize,
            TextAlignment = TextAlignment.Left,
        };

        MarkdownDocument parsed = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
        foreach (MdBlock block in parsed)
        {
            if (RenderBlock(block, baseFontSize) is WpfBlock rendered)
            {
                doc.Blocks.Add(rendered);
            }
        }

        // Trim the spacing after the last block so messages don't end with a gap.
        if (doc.Blocks.LastBlock is WpfBlock last)
        {
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        return doc;
    }

    private static WpfBlock? RenderBlock(MdBlock block, double baseFontSize)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                double scale = heading.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.0 };
                var p = new Paragraph { FontSize = baseFontSize * scale, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) };
                AddInlines(p.Inlines, heading.Inline);
                return p;
            }

            case ParagraphBlock paragraph:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(p.Inlines, paragraph.Inline);
                return p;
            }

            case FencedCodeBlock or CodeBlock:
            {
                var code = (LeafBlock)block;
                string text = string.Join("\n", code.Lines.Lines.Take(code.Lines.Count).Select(l => l.Slice.ToString()));
                var p = new Paragraph
                {
                    FontFamily = CodeFont,
                    Background = CodeBackground,
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 8),
                };
                AddPlainLines(p.Inlines, text);
                return p;
            }

            case ListBlock list:
            {
                var l = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(20, 0, 0, 0),
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start))
                {
                    l.StartIndex = start;
                }

                foreach (ListItemBlock item in list.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (MdBlock child in item)
                    {
                        if (RenderBlock(child, baseFontSize) is WpfBlock rendered)
                        {
                            rendered.Margin = new Thickness(0, 0, 0, 2);
                            li.Blocks.Add(rendered);
                        }
                    }

                    l.ListItems.Add(li);
                }

                return l;
            }

            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 8),
                    FontStyle = FontStyles.Italic,
                };
                foreach (MdBlock child in quote)
                {
                    if (RenderBlock(child, baseFontSize) is WpfBlock rendered)
                    {
                        section.Blocks.Add(rendered);
                    }
                }

                return section;
            }

            case ThematicBreakBlock:
                return new Paragraph { BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 8), FontSize = 1 };

            case MdTable table:
                return RenderTable(table, baseFontSize);

            case HtmlBlock html:
            {
                var p = new Paragraph { FontFamily = CodeFont, Margin = new Thickness(0, 0, 0, 8) };
                AddPlainLines(p.Inlines, html.Lines.ToString());
                return p;
            }

            case LeafBlock leaf when leaf.Inline != null:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(p.Inlines, leaf.Inline);
                return p;
            }

            default:
                return null;
        }
    }

    private static WpfTable RenderTable(MdTable table, double baseFontSize)
    {
        var result = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8), BorderBrush = RuleBrush, BorderThickness = new Thickness(1, 1, 0, 0) };
        int columns = table.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (int i = 0; i < columns; i++)
        {
            result.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        foreach (MdTableRow row in table.OfType<MdTableRow>())
        {
            var wpfRow = new WpfTableRow();
            if (row.IsHeader)
            {
                wpfRow.FontWeight = FontWeights.SemiBold;
                wpfRow.Background = CodeBackground;
            }

            foreach (MdTableCell cell in row.OfType<MdTableCell>())
            {
                var wpfCell = new WpfTableCell { BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 2, 6, 2) };
                foreach (MdBlock child in cell)
                {
                    if (RenderBlock(child, baseFontSize) is WpfBlock rendered)
                    {
                        rendered.Margin = new Thickness(0);
                        wpfCell.Blocks.Add(rendered);
                    }
                }

                wpfRow.Cells.Add(wpfCell);
            }

            group.Rows.Add(wpfRow);
        }

        result.RowGroups.Add(group);
        return result;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container == null)
        {
            return;
        }

        foreach (MdInline inline in container)
        {
            if (RenderInline(inline) is WpfInline rendered)
            {
                target.Add(rendered);
            }
        }
    }

    private static WpfInline? RenderInline(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());

            case CodeInline code:
                return new Run(code.Content) { FontFamily = CodeFont, Background = CodeBackground };

            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");

            case EmphasisInline emphasis:
            {
                Span span = emphasis.DelimiterChar == '~'
                    ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(span.Inlines, emphasis);
                return span;
            }

            case LinkInline { IsImage: false } link:
            {
                var hyperlink = new Hyperlink();
                AddInlines(hyperlink.Inlines, link);
                if (hyperlink.Inlines.Count == 0)
                {
                    hyperlink.Inlines.Add(new Run(link.Url));
                }

                AttachNavigation(hyperlink, link.Url);
                return hyperlink;
            }

            case LinkInline image:
                return new Run($"[image: {image.Title ?? image.Url}]");

            case AutolinkInline autolink:
            {
                var hyperlink = new Hyperlink(new Run(autolink.Url));
                AttachNavigation(hyperlink, autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url);
                return hyperlink;
            }

            case HtmlInline html:
                return new Run(html.Tag);

            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());

            case ContainerInline container:
            {
                var span = new Span();
                AddInlines(span.Inlines, container);
                return span;
            }

            default:
                return new Run(inline.ToString());
        }
    }

    private static void AttachNavigation(Hyperlink hyperlink, string? url)
    {
        // Only open web links; never launch arbitrary file or protocol handlers from model output.
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.ToolTip = uri.AbsoluteUri;
            hyperlink.RequestNavigate += (_, e) =>
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            };
        }
    }

    private static void AddPlainLines(InlineCollection target, string text)
    {
        string[] lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                target.Add(new LineBreak());
            }

            target.Add(new Run(lines[i]));
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
