using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));

    private static readonly Brush DarkKeyword = Rgb(0x56, 0x9C, 0xD6);
    private static readonly Brush DarkType = Rgb(0x4E, 0xC9, 0xB0);
    private static readonly Brush DarkString = Rgb(0xD6, 0x9D, 0x85);
    private static readonly Brush DarkComment = Rgb(0x57, 0xA6, 0x4A);
    private static readonly Brush DarkNumber = Rgb(0xB5, 0xCE, 0xA8);
    private static readonly Brush DarkAttribute = Rgb(0x9C, 0xDC, 0xFE);
    private static readonly Brush LightKeyword = Rgb(0x00, 0x00, 0xFF);
    private static readonly Brush LightType = Rgb(0x2B, 0x91, 0xAF);
    private static readonly Brush LightString = Rgb(0xA3, 0x15, 0x15);
    private static readonly Brush LightComment = Rgb(0x00, 0x80, 0x00);
    private static readonly Brush LightNumber = Rgb(0x09, 0x86, 0x58);
    private static readonly Brush LightTag = Rgb(0x80, 0x00, 0x00);
    private static readonly Brush LightAttribute = Rgb(0xE5, 0x00, 0x00);

    private static readonly ControlTemplate FlatButtonTemplate = CreateFlatButtonTemplate();

    /// <summary>Whether Visual Studio is using a dark theme, for picking code colours. Set by the chat window.</summary>
    public static Func<bool> IsDarkTheme { get; set; } = () => true;

    public static FlowDocument Render(string markdown, double baseFontSize)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        Update(doc, markdown, baseFontSize, new RenderedBlocks());
        return doc;
    }

    /// <summary>
    /// Renders Markdown into an existing document. While a reply streams in, only its end changes, so top-level
    /// blocks whose Markdown is the same as last time are kept rather than rebuilt; only the rest are rendered again.
    /// </summary>
    /// <param name="previous">What the document holds from the last call; updated to match the new text.</param>
    public static void Update(FlowDocument doc, string markdown, double baseFontSize, RenderedBlocks previous)
    {
        markdown ??= string.Empty;
        if (previous.FontSize != baseFontSize)
        {
            previous.Clear();
            doc.Blocks.Clear();
            previous.FontSize = baseFontSize;
        }

        doc.FontSize = baseFontSize;
        if (previous.Trimmed is { } trimmed)
        {
            trimmed.Block.Margin = trimmed.Margin;
            previous.Trimmed = null;
        }

        previous.ChangedInPlace = null;
        List<MdBlock> blocks = Markdig.Markdown.Parse(markdown, Pipeline).ToList();
        List<string?> sources = blocks.Select(b => SourceOf(markdown, b)).ToList();

        // A code block still streaming in only grows at its end, so new lines are added to it rather than the whole
        // block being highlighted and built again on every update. Once its fence closes it's rendered afresh.
        int last = blocks.Count - 1;
        if (last >= 0 && last == previous.Items.Count - 1 && previous.Streaming is { } streaming
            && ReferenceEquals(previous.Items[last].Block, streaming.Section)
            && blocks[last] is FencedCodeBlock { ClosingFencedCharCount: 0 } open && (open.Info ?? string.Empty) == streaming.Info
            && Enumerable.Range(0, last).All(i => sources[i] != null && string.Equals(previous.Items[i].Source, sources[i], StringComparison.Ordinal))
            && streaming.TryAppend(CodeText(open)))
        {
            previous.Items[last] = (sources[last], streaming.Section);
            previous.ChangedInPlace = streaming.Section;
            TrimLastMargin(doc, previous);
            return;
        }

        previous.Streaming = null;

        // The last block rendered before may have been cut off mid-stream, so it's never kept.
        int keep = 0;
        while (keep < blocks.Count && keep < previous.Items.Count - 1
            && sources[keep] != null && string.Equals(previous.Items[keep].Source, sources[keep], StringComparison.Ordinal))
        {
            keep++;
        }

        for (int i = previous.Items.Count - 1; i >= keep; i--)
        {
            if (previous.Items[i].Block is WpfBlock stale)
            {
                doc.Blocks.Remove(stale);
            }
        }

        previous.Items.RemoveRange(keep, previous.Items.Count - keep);
        for (int i = keep; i < blocks.Count; i++)
        {
            WpfBlock? rendered;
            if (i == last && blocks[i] is FencedCodeBlock { ClosingFencedCharCount: 0 } unclosed)
            {
                previous.Streaming = new StreamingCode(unclosed.Info ?? string.Empty, baseFontSize);
                previous.Streaming.TryAppend(CodeText(unclosed));
                rendered = previous.Streaming.Section;
            }
            else
            {
                rendered = RenderBlock(blocks[i], baseFontSize);
            }

            previous.Items.Add((sources[i], rendered));
            if (rendered != null)
            {
                doc.Blocks.Add(rendered);
            }
        }

        TrimLastMargin(doc, previous);
    }

    // Trim the spacing after the last block so messages don't end with a gap (put back if it stops being last).
    private static void TrimLastMargin(FlowDocument doc, RenderedBlocks previous)
    {
        if (doc.Blocks.LastBlock is WpfBlock last)
        {
            previous.Trimmed = (last, last.Margin);
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }
    }

    private static string CodeText(LeafBlock code)
        => string.Join("\n", code.Lines.Lines.Take(code.Lines.Count).Select(l => l.Slice.ToString())).TrimEnd('\n');

    /// <summary>
    /// A code block whose closing fence hasn't arrived yet. Its complete lines are highlighted once and kept; only the
    /// line still being written is replaced on each update. Lines are highlighted a batch at a time, so a comment or
    /// string spanning batches may be coloured wrongly until the fence closes and the block is rendered whole.
    /// </summary>
    internal sealed class StreamingCode
    {
        private readonly string? language;
        private string text = string.Empty;
        private int committed; // length of the text up to and including its last line break, which is never redone
        private WpfInline? committedEnd;

        public StreamingCode(string info, double baseFontSize)
        {
            this.Info = info;
            this.language = SyntaxHighlighter.NormalizeLanguage(info);
            (this.Section, this.Body) = CodeSection(info, baseFontSize, () => this.text);
        }

        public string Info { get; }

        public Section Section { get; }

        private Paragraph Body { get; }

        /// <summary>Shows <paramref name="code"/>, which must start with the lines already shown; false if it doesn't.</summary>
        public bool TryAppend(string code)
        {
            if (code.Length < this.committed || string.CompareOrdinal(code, 0, this.text, 0, this.committed) != 0)
            {
                return false;
            }

            InlineCollection inlines = this.Body.Inlines;
            while (inlines.LastInline is WpfInline tail && !ReferenceEquals(tail, this.committedEnd))
            {
                inlines.Remove(tail);
            }

            bool dark = IsDarkTheme();
            int lineEnd = code.LastIndexOf('\n') + 1;
            if (lineEnd > this.committed)
            {
                AddCodeTokens(inlines, SyntaxHighlighter.Tokenize(code.Substring(this.committed, lineEnd - this.committed), this.language), dark);
                this.committed = lineEnd;
                this.committedEnd = inlines.LastInline;
            }

            if (code.Length > this.committed)
            {
                AddCodeTokens(inlines, SyntaxHighlighter.Tokenize(code.Substring(this.committed), this.language), dark);
            }

            this.text = code;
            return true;
        }
    }

    /// <summary>The Markdown a top-level block came from, or null if its position isn't known.</summary>
    private static string? SourceOf(string markdown, MdBlock block)
        => block.Span.Start >= 0 && block.Span.Length > 0 && block.Span.End < markdown.Length
            ? markdown.Substring(block.Span.Start, block.Span.Length)
            : null;

    /// <summary>The top-level blocks in a document rendered by <see cref="Update"/>, with the Markdown each came from.</summary>
    internal sealed class RenderedBlocks
    {
        public List<(string? Source, WpfBlock? Block)> Items { get; } = [];

        public double FontSize { get; set; } = double.NaN;

        /// <summary>The block whose bottom margin was trimmed, and the margin it had.</summary>
        public (WpfBlock Block, Thickness Margin)? Trimmed { get; set; }

        /// <summary>The last block, if it's a code block still streaming in.</summary>
        public StreamingCode? Streaming { get; set; }

        /// <summary>A block the last update changed without replacing it, so anything cached about it is stale.</summary>
        public WpfBlock? ChangedInPlace { get; set; }

        /// <summary>Forgets everything, so the next update renders every block again (e.g. for new code colours).</summary>
        public void Clear()
        {
            this.Items.Clear();
            this.Trimmed = null;
            this.Streaming = null;
            this.ChangedInPlace = null;
        }
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
                return RenderCodeBlock((LeafBlock)block, baseFontSize);

            case ListBlock list:
            {
                var l = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(20, 0, 0, 0),
                };
                    // Markdown allows a list to start at 0, but WPF throws for anything below 1.
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start) && start > 0)
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

    /// <summary>A code block: a header with the language and a Copy button, then the code, coloured by language.</summary>
    private static Section RenderCodeBlock(LeafBlock code, double baseFontSize)
    {
        string text = CodeText(code);
        string info = (code as FencedCodeBlock)?.Info ?? string.Empty;
        (Section section, Paragraph body) = CodeSection(info, baseFontSize, () => text);
        AddCodeTokens(body.Inlines, SyntaxHighlighter.Tokenize(text, SyntaxHighlighter.NormalizeLanguage(info)), IsDarkTheme());
        return section;
    }

    /// <summary>An empty code block: the header, and the paragraph for the code.</summary>
    private static (Section Section, Paragraph Body) CodeSection(string info, double baseFontSize, Func<string> copyText)
    {
        var header = new Paragraph
        {
            Background = CodeBackground,
            Padding = new Thickness(8, 2, 2, 0),
            Margin = new Thickness(0),
            FontSize = baseFontSize * 0.85,
            TextAlignment = TextAlignment.Right,
        };
        string label = info.Trim().Split(' ').FirstOrDefault() ?? string.Empty;
        if (label.Length > 0)
        {
            header.Inlines.Add(new Run(label + "  ") { Foreground = MutedBrush });
        }

        header.Inlines.Add(new InlineUIContainer(CopyButton(copyText)) { BaselineAlignment = BaselineAlignment.Center });

        var body = new Paragraph
        {
            FontFamily = CodeFont,
            Background = CodeBackground,
            Padding = new Thickness(8, 0, 8, 6),
            Margin = new Thickness(0),
        };

        var section = new Section { Margin = new Thickness(0, 0, 0, 8) };
        section.Blocks.Add(header);
        section.Blocks.Add(body);
        return (section, body);
    }

    /// <summary>A small flat "Copy" button that puts <paramref name="text"/> on the clipboard.</summary>
    internal static Button CopyButton(string text, string label = "Copy") => CopyButton(() => text, label);

    private static Button CopyButton(Func<string> text, string label = "Copy")
    {
        var button = new Button
        {
            Content = label,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = MutedBrush,
            Padding = new Thickness(6, 1, 6, 1),
            FontSize = 11,
            Focusable = false,
            ToolTip = "Copy to the clipboard",
        };
        button.Template = FlatButtonTemplate;
        button.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text());
                button.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                button.Content = "Clipboard busy";
            }

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                button.Content = label;
            };
            timer.Start();
        };
        return button;
    }

    private static ControlTemplate CreateFlatButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "Bd"));
        template.Triggers.Add(hover);
        template.Seal();
        return template;
    }

    private static void AddCodeTokens(InlineCollection target, IReadOnlyList<CodeToken> tokens, bool dark)
    {
        foreach (CodeToken token in tokens)
        {
            Brush? brush = TokenBrush(token.Kind, dark);
            string[] lines = token.Text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    target.Add(new LineBreak());
                }

                if (lines[i].Length > 0)
                {
                    var run = new Run(lines[i]);
                    if (brush != null)
                    {
                        run.Foreground = brush;
                    }

                    target.Add(run);
                }
            }
        }
    }

    /// <summary>Token colours in the style of Visual Studio's own Dark and Light themes.</summary>
    private static Brush? TokenBrush(TokenKind kind, bool dark) => kind switch
    {
        TokenKind.Keyword => dark ? DarkKeyword : LightKeyword,
        TokenKind.Type => dark ? DarkType : LightType,
        TokenKind.String => dark ? DarkString : LightString,
        TokenKind.Comment => dark ? DarkComment : LightComment,
        TokenKind.Number => dark ? DarkNumber : LightNumber,
        TokenKind.Preprocessor => MutedBrush,
        TokenKind.Tag => dark ? DarkKeyword : LightTag,
        TokenKind.Attribute => dark ? DarkAttribute : LightAttribute,
        _ => null,
    };

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
                return RenderText(literal.Content.ToString());

            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = CodeFont, Background = CodeBackground };
                return FileLinks.ParseWhole(code.Content) is PathMention mention && FileLinks.Target(mention) is FileTarget target
                    ? FileHyperlink(run, target)
                    : run;
            }

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

                // A relative link such as [Foo.cs](src/Foo.cs#L12) points into the repository.
                if (!Uri.TryCreate(link.Url, UriKind.Absolute, out Uri? absolute) || absolute.IsFile)
                {
                    string url = Uri.UnescapeDataString(link.Url ?? string.Empty);
                    if (FileLinks.ParseWhole(url.StartsWith("file:///") ? url.Substring(8) : url) is PathMention mention
                        && FileLinks.Target(mention) is FileTarget target)
                    {
                        AttachFileNavigation(hyperlink, target);
                        return hyperlink;
                    }
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

    /// <summary>Plain text, with any paths to files in the repository turned into links.</summary>
    private static WpfInline RenderText(string text)
    {
        if (FileLinks.Resolve is null)
        {
            return new Run(text);
        }

        Span? span = null;
        int done = 0;
        foreach (PathMention mention in FileLinks.Find(text))
        {
            if (FileLinks.Target(mention) is not FileTarget target)
            {
                continue;
            }

            span ??= new Span();
            if (mention.Start > done)
            {
                span.Inlines.Add(new Run(text.Substring(done, mention.Start - done)));
            }

            span.Inlines.Add(FileHyperlink(new Run(text.Substring(mention.Start, mention.Length)), target));
            done = mention.Start + mention.Length;
        }

        if (span is null)
        {
            return new Run(text);
        }

        if (done < text.Length)
        {
            span.Inlines.Add(new Run(text.Substring(done)));
        }

        return span;
    }

    private static Hyperlink FileHyperlink(WpfInline content, FileTarget target)
    {
        var hyperlink = new Hyperlink(content);
        AttachFileNavigation(hyperlink, target);
        return hyperlink;
    }

    private static void AttachFileNavigation(Hyperlink hyperlink, FileTarget target)
    {
        string lines = target.Line <= 0 ? string.Empty
            : target.EndLine > target.Line ? $" at lines {target.Line}-{target.EndLine}"
            : $" at line {target.Line}";
        hyperlink.ToolTip = $"Open {target.FullPath}{lines}";
        hyperlink.Cursor = Cursors.Hand;
        hyperlink.Click += (_, e) =>
        {
            FileLinks.Open?.Invoke(target);
            e.Handled = true;
        };
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
                e.Handled = true;
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // No default browser, or a policy blocks it: an exception here would reach Visual Studio's dispatcher.
                    hyperlink.ToolTip = $"Couldn't open a browser ({ex.Message}). {uri.AbsoluteUri}";
                }
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

    private static Brush Rgb(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
