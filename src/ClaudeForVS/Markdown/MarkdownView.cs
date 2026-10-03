using System;
using System.Windows;
using System.Windows.Controls;
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
    }
}
