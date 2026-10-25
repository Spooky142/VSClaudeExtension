using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeForVS.Markdown;

/// <summary>
/// Read-only, selectable rich text showing rendered Markdown. While text is streaming in,
/// re-rendering is throttled so long answers don't re-layout on every token.
/// </summary>
internal sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(string.Empty, OnMarkdownChanged));

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(120);

    // Covers the caret and small differences between measured and laid-out text, so the last word doesn't wrap.
    private const double WidthSlack = 6;

    private readonly DispatcherTimer timer;
    private DateTime lastRender = DateTime.MinValue;

    public MarkdownView()
    {
        this.IsReadOnly = true;
        this.IsDocumentEnabled = true; // hyperlinks clickable
        this.IsReadOnlyCaretVisible = false;
        this.BorderThickness = new Thickness(0);
        this.Padding = new Thickness(0);
        this.Background = Brushes.Transparent;
        this.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        this.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        this.Document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };

        this.timer = new DispatcherTimer(RenderInterval, DispatcherPriority.Background, (_, _) => this.Render(), this.Dispatcher);
        this.timer.Stop();
    }

    public string Markdown
    {
        get => (string)this.GetValue(MarkdownProperty);
        set => this.SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)d;
        if (DateTime.UtcNow - view.lastRender >= RenderInterval)
        {
            view.Render();
        }
        else
        {
            view.timer.Stop();
            view.timer.Start();
        }
    }

    /// <summary>Renders all of the Markdown again now, e.g. after the theme changes the code colours.</summary>
    public void Rerender()
    {
        this.rendered.Clear();
        this.Document.Blocks.Clear();
        this.blockWidths.Clear();
        this.Render();
    }

    private void Render()
    {
        this.timer.Stop();
        this.lastRender = DateTime.UtcNow;
        using (FileLinks.Using(FileLinks.GetHandler(this)))
        {
            MarkdownRenderer.Update(this.Document, this.Markdown, this.FontSize, this.rendered);
        }

        if (this.rendered.ChangedInPlace is Block changed)
        {
            this.blockWidths.Remove(changed); // a streaming code block that grew
        }

        this.fitWidth = null;
        this.InvalidateMeasure();
    }

    // What the document holds, so a render only rebuilds the blocks whose Markdown changed.
    private readonly MarkdownRenderer.RenderedBlocks rendered = new();

    // A RichTextBox always wants the full width offered to it, so bubbles around it would span the whole window.
    // Instead the width of the longest line is worked out from the document's text, wrapped at the width on offer,
    // and the text is laid out at that width (when narrower) so the surrounding bubble hugs it, wrapped or not.
    // This happens within the measure pass itself: laying out at full width first and shrinking afterwards gets
    // drawn in between, which shows as flicker.
    private double? fitWidth;
    private double fitConstraint;

    protected override Size MeasureOverride(Size constraint)
    {
        if (this.fitWidth is null || this.fitConstraint != constraint.Width)
        {
            this.fitConstraint = constraint.Width;
            this.fitWidth = this.MeasureFitWidth(constraint.Width);
        }

        double width = Math.Min(constraint.Width, this.fitWidth.Value);
        Size size = base.MeasureOverride(new Size(width, constraint.Height));
        return new Size(Math.Min(size.Width, width), size.Height);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == FontWeightProperty
            || e.Property == FontStyleProperty || e.Property == PaddingProperty)
        {
            this.fitWidth = null;
            this.blockWidths.Clear();
        }
        else if (e.Property == FileLinks.HandlerProperty && e.NewValue != null && this.rendered.Items.Count > 0)
        {
            this.Rerender(); // rendered before it was in a chat window, so without links to files
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        this.fitWidth = null;
        this.blockWidths.Clear();
        this.InvalidateMeasure();
    }

    // Widths of the top-level blocks at blockWidthsAvailable, so blocks kept from the last render aren't measured again.
    private readonly System.Collections.Generic.Dictionary<Block, double> blockWidths = [];
    private double blockWidthsAvailable;

    private double MeasureFitWidth(double constraintWidth)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Thickness page = this.Document.PagePadding;
        double chrome = page.Left + page.Right + this.Padding.Left + this.Padding.Right
            + this.BorderThickness.Left + this.BorderThickness.Right + WidthSlack;
        double available = Math.Max(1, constraintWidth - chrome);
        if (available != this.blockWidthsAvailable)
        {
            this.blockWidths.Clear();
            this.blockWidthsAvailable = available;
        }

        double content = 0;
        var current = new System.Collections.Generic.Dictionary<Block, double>();
        foreach (Block block in this.Document.Blocks)
        {
            if (!this.blockWidths.TryGetValue(block, out double width))
            {
                // Once a block fills the width on offer, the rest can't make the text any wider; leave them unmeasured,
                // so resizing a long chat doesn't measure every paragraph of every message again.
                if (content + chrome >= constraintWidth)
                {
                    continue;
                }

                width = ReferenceEquals(block, this.rendered.Streaming?.Section)
                    ? this.MeasureStreamingCode((Section)block, pixelsPerDip, available)
                    : MeasureBlock(block, pixelsPerDip, available);
            }

            current[block] = width;
            content = Math.Max(content, width);
        }

        // Keep only the blocks still in the document.
        this.blockWidths.Clear();
        foreach (var pair in current)
        {
            this.blockWidths[pair.Key] = pair.Value;
        }

        return double.IsPositiveInfinity(content) ? double.PositiveInfinity : Math.Ceiling(content + chrome);
    }

    /// <summary>Widest line among the blocks once wrapped to <paramref name="available"/>, including their indents; infinite for tables, which fill whatever width they get.</summary>
    private static double MeasureBlocks(BlockCollection blocks, double pixelsPerDip, double available)
    {
        double max = 0;
        foreach (Block block in blocks)
        {
            max = Math.Max(max, MeasureBlock(block, pixelsPerDip, available));
        }

        return max;
    }

    private static double Insets(Block block) => block.Padding.Left + block.Padding.Right + block.BorderThickness.Left
        + block.BorderThickness.Right + block.Margin.Left + block.Margin.Right;

    // The code block still streaming in, its last complete line's line break, and the widest line up to there, so each
    // render only measures the lines added since rather than the whole block again.
    private (Paragraph Body, LineBreak LastBreak, double Max, double Available, double PixelsPerDip)? streamingLines;

    /// <summary>Like <see cref="MeasureBlock"/> for a streaming code block's section, measuring only its new lines.</summary>
    private double MeasureStreamingCode(Section section, double pixelsPerDip, double available)
    {
        double insets = Insets(section);
        double inside = Math.Max(1, available - insets);
        double max = 0;
        foreach (Block block in section.Blocks)
        {
            if (block is Paragraph body && ReferenceEquals(block, section.Blocks.LastBlock))
            {
                double bodyInsets = Insets(body);
                max = Math.Max(max, this.MeasureCodeBody(body, pixelsPerDip, Math.Max(1, inside - bodyInsets)) + bodyInsets);
            }
            else
            {
                max = Math.Max(max, MeasureBlock(block, pixelsPerDip, inside));
            }
        }

        return max + insets;
    }

    private double MeasureCodeBody(Paragraph body, double pixelsPerDip, double available)
    {
        var line = new LineMeasurer(body, pixelsPerDip, available);
        if (this.streamingLines is { } cached && ReferenceEquals(cached.Body, body) && ReferenceEquals(cached.LastBreak.Parent, body)
            && cached.Available == available && cached.PixelsPerDip == pixelsPerDip)
        {
            line.AddFrom(cached.LastBreak.NextInline, cached.Max, cached.LastBreak);
        }
        else
        {
            line.AddFrom(body.Inlines.FirstInline, 0, null);
        }

        double width = line.Finish();
        this.streamingLines = line.LastBreak is LineBreak lastBreak ? (body, lastBreak, line.MaxAtLastBreak, available, pixelsPerDip) : null;
        return width;
    }

    private static double MeasureBlock(Block block, double pixelsPerDip, double available)
    {
        double insets = Insets(block);
        double inside = Math.Max(1, available - insets);
        double inner = block switch
        {
            Paragraph p => MeasureParagraph(p, pixelsPerDip, inside),
            List list => MeasureList(list, pixelsPerDip, inside),
            Section section => MeasureBlocks(section.Blocks, pixelsPerDip, inside),
            _ => double.PositiveInfinity,
        };

        return inner + insets;
    }

    private static double MeasureList(List list, double pixelsPerDip, double available)
    {
        double max = 0;
        foreach (ListItem item in list.ListItems)
        {
            max = Math.Max(max, MeasureBlocks(item.Blocks, pixelsPerDip, available));
        }

        return max;
    }

    private static double MeasureParagraph(Paragraph paragraph, double pixelsPerDip, double available)
    {
        var line = new LineMeasurer(paragraph, pixelsPerDip, available);
        line.AddInlines(paragraph.Inlines);
        return line.Finish();
    }

    /// <summary>Measures a paragraph's lines, wrapped to the available width, keeping each run's font.</summary>
    private sealed class LineMeasurer
    {
        private readonly Paragraph paragraph;
        private readonly double pixelsPerDip;
        private readonly double available;
        private readonly StringBuilder text = new();
        private readonly System.Collections.Generic.List<(int Start, int Length, Run Run)> runs = [];
        private double max;

        public LineMeasurer(Paragraph paragraph, double pixelsPerDip, double available)
        {
            this.paragraph = paragraph;
            this.pixelsPerDip = pixelsPerDip;
            this.available = available;
        }

        /// <summary>The last line break measured, and the widest line before it.</summary>
        public LineBreak? LastBreak { get; private set; }

        public double MaxAtLastBreak { get; private set; }

        /// <summary>Measures <paramref name="first"/> and the inlines after it, carrying on from lines already measured.</summary>
        public void AddFrom(Inline? first, double max, LineBreak? lastBreak)
        {
            this.max = this.MaxAtLastBreak = max;
            this.LastBreak = lastBreak;
            for (Inline? inline = first; inline != null; inline = inline.NextInline)
            {
                this.Add(inline);
            }
        }

        public void AddInlines(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                this.Add(inline);
            }
        }

        private void Add(Inline inline)
        {
            switch (inline)
            {
                case Run run:
                    string runText = run.Text.Replace("\t", "    ");
                    this.runs.Add((this.text.Length, runText.Length, run));
                    this.text.Append(runText);
                    break;
                case LineBreak lineBreak:
                    this.EndLine();
                    this.LastBreak = lineBreak;
                    this.MaxAtLastBreak = this.max;
                    break;
                case Span span:
                    this.AddInlines(span.Inlines);
                    break;
            }
        }

        public double Finish()
        {
            this.EndLine();
            return this.max;
        }

        private void EndLine()
        {
            if (this.text.Length > 0)
            {
                var formatted = new FormattedText(
                    this.text.ToString(),
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(this.paragraph.FontFamily, this.paragraph.FontStyle, this.paragraph.FontWeight, FontStretches.Normal),
                    this.paragraph.FontSize,
                    Brushes.Black,
                    this.pixelsPerDip);

                foreach ((int start, int length, Run run) in this.runs)
                {
                    if (length > 0)
                    {
                        formatted.SetFontFamily(run.FontFamily, start, length);
                        formatted.SetFontSize(run.FontSize, start, length);
                        formatted.SetFontWeight(run.FontWeight, start, length);
                        formatted.SetFontStyle(run.FontStyle, start, length);
                    }
                }

                if (!double.IsPositiveInfinity(this.available))
                {
                    formatted.MaxTextWidth = this.available;
                }

                this.max = Math.Max(this.max, formatted.Width);
            }

            this.text.Clear();
            this.runs.Clear();
        }
    }
}
