using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    private readonly DispatcherTimer timer;
    private DateTime lastRender = DateTime.MinValue;

    public MarkdownView()
    {
        this.IsReadOnly = true;
        this.IsDocumentEnabled = true; // hyperlinks clickable
        this.IsReadOnlyCaretVisible = false;
        this.BorderThickness = new Thickness(0);
        this.Padding = new Thickness(0);
        this.Background = System.Windows.Media.Brushes.Transparent;
        this.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        this.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        this.Document = new System.Windows.Documents.FlowDocument();

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
        this.ResetNaturalWidth();
    }

    // A RichTextBox always wants the full width offered to it, so bubbles around it would span the whole
    // window. Instead: lay out at the offered width, measure the widest rendered line, then report that
    // (when narrower) as the desired width so the surrounding bubble hugs the text.
    private double? naturalWidth;
    private bool measuringNaturalWidth;
    private double lastConstraintWidth = -1;

    private void ResetNaturalWidth()
    {
        this.naturalWidth = null;
        this.measuringNaturalWidth = true;
        this.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (constraint.Width != this.lastConstraintWidth)
        {
            this.lastConstraintWidth = constraint.Width;
            this.naturalWidth = null;
            this.measuringNaturalWidth = true;
        }

        Size size = base.MeasureOverride(constraint);
        if (this.naturalWidth is double natural && natural < size.Width)
        {
            return new Size(natural, size.Height);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        Size result = base.ArrangeOverride(arrangeBounds);
        if (this.measuringNaturalWidth)
        {
#pragma warning disable VSTHRD001, VSTHRD110 // pure UI-thread layout work, no JTF needed
            // Normal (above Render) so the natural width is captured and re-measured before the frame is drawn;
            // at a lower priority the full-width intermediate layout gets painted, which shows as flicker.
            this.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(this.CaptureNaturalWidth));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        return result;
    }

    private void CaptureNaturalWidth()
    {
        if (!this.measuringNaturalWidth)
        {
            return;
        }

        double? widest = this.MeasureWidestLine();
        if (widest is null)
        {
            return; // layout not ready yet; the next arrange retries
        }

        this.measuringNaturalWidth = false;
        double width = Math.Ceiling(widest.Value + this.Document.PagePadding.Right + this.Padding.Right + 4);
        if (this.naturalWidth != width)
        {
            this.naturalWidth = width;
            this.InvalidateMeasure();
        }
    }

    private double? MeasureWidestLine()
    {
        FlowDocument doc = this.Document;
        TextPointer? pos = doc.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        double max = 0;
        while (pos is not null)
        {
            TextPointer? next = pos.GetLineStartPosition(1, out int moved);
            TextPointer end = moved == 1 && next is not null
                ? next.GetInsertionPosition(LogicalDirection.Backward)
                : doc.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);

            Rect rect = end.GetCharacterRect(LogicalDirection.Backward);
            if (rect.IsEmpty)
            {
                return null;
            }

            max = Math.Max(max, rect.Right);
            if (moved != 1)
            {
                break;
            }

            pos = next;
        }

        return max;
    }
}
