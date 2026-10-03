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
    private const double WidthSlack = 8;

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
        this.Document = new FlowDocument();

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

    private void Render()
    {
        this.timer.Stop();
        this.lastRender = DateTime.UtcNow;
        this.Document = MarkdownRenderer.Render(this.Markdown, this.FontSize);
        this.naturalWidth = null;
        this.InvalidateMeasure();
    }

    // A RichTextBox always wants the full width offered to it, so bubbles around it would span the whole window.
    // Instead the width of the longest line is worked out from the document's text, and the text is laid out at
    // that width (when narrower) so the surrounding bubble hugs it. This happens within the measure pass itself:
    // laying out at full width first and shrinking afterwards gets drawn in between, which shows as flicker.
    private double? naturalWidth;

    protected override Size MeasureOverride(Size constraint)
    {
        this.naturalWidth ??= this.MeasureNaturalWidth();
        double width = Math.Min(constraint.Width, this.naturalWidth.Value);
        Size size = base.MeasureOverride(new Size(width, constraint.Height));
        return new Size(Math.Min(size.Width, width), size.Height);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == FontWeightProperty
            || e.Property == FontStyleProperty || e.Property == PaddingProperty)
        {
            this.naturalWidth = null;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        this.naturalWidth = null;
        this.InvalidateMeasure();
    }

    private double MeasureNaturalWidth()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double content = MeasureBlocks(this.Document.Blocks, pixelsPerDip);
        if (double.IsPositiveInfinity(content))
        {
            return double.PositiveInfinity;
        }

        Thickness page = this.Document.PagePadding;
        return Math.Ceiling(content + page.Left + page.Right + this.Padding.Left + this.Padding.Right
            + this.BorderThickness.Left + this.BorderThickness.Right + WidthSlack);
    }

    /// <summary>Widest unwrapped line among the blocks, including their indents; infinite for tables, which fill whatever width they get.</summary>
    private static double MeasureBlocks(BlockCollection blocks, double pixelsPerDip)
    {
        double max = 0;
        foreach (Block block in blocks)
        {
            double inner = block switch
            {
                Paragraph p => MeasureParagraph(p, pixelsPerDip),
                List list => MeasureList(list, pixelsPerDip),
                Section section => MeasureBlocks(section.Blocks, pixelsPerDip),
                _ => double.PositiveInfinity,
            };

            max = Math.Max(max, inner + block.Padding.Left + block.Padding.Right + block.BorderThickness.Left
                + block.BorderThickness.Right + block.Margin.Left + block.Margin.Right);
        }

        return max;
    }

    private static double MeasureList(List list, double pixelsPerDip)
    {
        double max = 0;
        foreach (ListItem item in list.ListItems)
        {
            max = Math.Max(max, MeasureBlocks(item.Blocks, pixelsPerDip));
        }

        return max;
    }

    private static double MeasureParagraph(Paragraph paragraph, double pixelsPerDip)
    {
        var line = new LineMeasurer(paragraph, pixelsPerDip);
        line.AddInlines(paragraph.Inlines);
        return line.Finish();
    }

    /// <summary>Measures the hard lines of a paragraph, keeping each run's font.</summary>
    private sealed class LineMeasurer
    {
        private readonly Paragraph paragraph;
        private readonly double pixelsPerDip;
        private readonly StringBuilder text = new();
        private readonly System.Collections.Generic.List<(int Start, int Length, Run Run)> runs = [];
        private double max;

        public LineMeasurer(Paragraph paragraph, double pixelsPerDip)
        {
            this.paragraph = paragraph;
            this.pixelsPerDip = pixelsPerDip;
        }

        public void AddInlines(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        string runText = run.Text.Replace("\t", "    ");
                        this.runs.Add((this.text.Length, runText.Length, run));
                        this.text.Append(runText);
                        break;
                    case LineBreak:
                        this.EndLine();
                        break;
                    case Span span:
                        this.AddInlines(span.Inlines);
                        break;
                }
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

                this.max = Math.Max(this.max, formatted.WidthIncludingTrailingWhitespace);
            }

            this.text.Clear();
            this.runs.Clear();
        }
    }
}
