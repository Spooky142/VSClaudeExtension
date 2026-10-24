using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace ClaudeForVS.Editor;

[Export(typeof(IWpfTextViewMarginProvider))]
[Name(ClaudeChangesMargin.MarginName)]
[MarginContainer(PredefinedMarginNames.Left)]
[Order(After = PredefinedMarginNames.LineNumber)]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class ClaudeChangesMarginProvider : IWpfTextViewMarginProvider
{
    [Import]
    internal ITextDocumentFactoryService TextDocuments { get; set; } = null!;

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
    {
        IWpfTextView view = wpfTextViewHost.TextView;

        // Only an editor that shows the file's own text, so its lines are the file's.
        return view.TextBuffer == view.TextDataModel.DocumentBuffer && this.TextDocuments.TryGetTextDocument(view.TextBuffer, out ITextDocument document)
            ? new ClaudeChangesMargin(view, document)
            : null;
    }
}

/// <summary>
/// Marks the lines Claude changed in a chat, beside the line numbers. Clicking a mark offers to undo that change (one
/// editor edit, so Ctrl+Z brings it back) or to compare the file with how it was before the chat. The marks go when
/// the chat is cleared. Lines the user changed elsewhere in the file aren't marked.
/// </summary>
internal sealed class ClaudeChangesMargin : Canvas, IWpfTextViewMargin
{
    public const string MarginName = "ClaudeChangesMargin";

    private const double MarkWidth = 3;
    private static readonly TimeSpan RecomputeDelay = TimeSpan.FromMilliseconds(300);
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));

    private readonly IWpfTextView view;
    private readonly ITextDocument document;
    private readonly DispatcherTimer recompute;
    private IReadOnlyList<ChangedRegion> regions = [];
    private ITextSnapshot? regionsSnapshot;
    private bool disposed;

    public ClaudeChangesMargin(IWpfTextView view, ITextDocument document)
    {
        this.view = view;
        this.document = document;
        this.Width = MarkWidth + 3;
        this.ClipToBounds = true;
        this.Background = Brushes.Transparent; // so a click beside a thin mark still reaches the margin
        this.Visibility = Visibility.Collapsed;
        this.recompute = new DispatcherTimer(DispatcherPriority.Background) { Interval = RecomputeDelay };
        this.recompute.Tick += (_, _) =>
        {
            this.recompute.Stop();
            this.RecomputeAsync().FileAndForget("ClaudeForVS/ChangesMargin");
        };

        ChatChanges.Changed += this.OnChatChanged;
        view.LayoutChanged += this.OnLayoutChanged;
        view.TextBuffer.Changed += this.OnBufferChanged;
        this.MouseLeftButtonUp += this.OnClick;
        this.Schedule();
    }

    public FrameworkElement VisualElement => this;

    public double MarginSize => this.Visibility == Visibility.Visible ? this.Width : 0;

    public bool Enabled => true;

    public ITextViewMargin? GetTextViewMargin(string marginName) => marginName == MarginName ? this : null;

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            this.recompute.Stop();
            ChatChanges.Changed -= this.OnChatChanged;
            this.view.LayoutChanged -= this.OnLayoutChanged;
            this.view.TextBuffer.Changed -= this.OnBufferChanged;
        }
    }

    private void OnChatChanged(object? sender, string path)
    {
        if (string.Equals(path, this.document.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            this.ScheduleOnUIThreadAsync().FileAndForget("ClaudeForVS/ChangesMargin");
        }
    }

    // ChatChanges.Changed is raised on whichever thread recorded the change.
    private async Task ScheduleOnUIThreadAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        this.Schedule();
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        if (this.regions.Count > 0 || ChatChanges.IsTracked(this.document.FilePath))
        {
            this.Schedule();
        }
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => this.Draw();

    private void Schedule()
    {
        if (!this.disposed)
        {
            this.recompute.Stop();
            this.recompute.Start();
        }
    }

    /// <summary>Works out the marks for the editor's text (off the UI thread: it's a diff of the whole file).</summary>
    private async Task RecomputeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string path = this.document.FilePath;
        ITextSnapshot snapshot = this.view.TextBuffer.CurrentSnapshot;
        IReadOnlyList<ChangedRegion> found = ChatChanges.IsTracked(path)
            ? await Task.Run(() => ChatChanges.RegionsIn(path, snapshot.GetText()))
            : [];

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (this.disposed || snapshot != this.view.TextBuffer.CurrentSnapshot)
        {
            return; // the text moved on; another recompute is on its way
        }

        this.regions = found;
        this.regionsSnapshot = snapshot;
        Visibility visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (this.Visibility != visibility)
        {
            this.Visibility = visibility;
        }

        this.Draw();
    }

    /// <summary>Draws a bar beside each changed line in view, and a notch where lines were removed.</summary>
    private void Draw()
    {
        this.Children.Clear();
        if (this.regions.Count == 0 || this.view.IsClosed || this.view.InLayout || this.regionsSnapshot != this.view.TextSnapshot)
        {
            return;
        }

        foreach (ITextViewLine line in this.view.TextViewLines)
        {
            ITextSnapshotLine fileLine = line.Start.GetContainingLine();
            int number = fileLine.LineNumber;
            bool firstPart = line.Start == fileLine.Start; // a wrapped line is shown as several
            bool lastInFile = line.End.Position == line.Snapshot.Length && line.LineBreakLength == 0;
            double top = line.Top - this.view.ViewportTop;
            foreach (ChangedRegion region in this.regions)
            {
                if (!region.IsRemoval && number >= region.NewStart && number < region.NewStart + region.NewCount)
                {
                    this.Add(new Rectangle { Width = MarkWidth, Height = line.Height, Fill = Accent }, 1, top);
                }
                else if (region.IsRemoval && number == region.NewStart && firstPart)
                {
                    this.Add(Notch(), 0, top - 3);
                }
                else if (region.IsRemoval && number == region.NewStart - 1 && lastInFile)
                {
                    this.Add(Notch(), 0, line.Bottom - this.view.ViewportTop - 3);
                }
            }
        }
    }

    private void Add(FrameworkElement mark, double left, double top)
    {
        mark.Cursor = Cursors.Hand;
        mark.ToolTip = "Changed by Claude in this chat. Click to undo it or compare.";
        SetLeft(mark, left);
        SetTop(mark, top);
        this.Children.Add(mark);
    }

    private static Polygon Notch() => new()
    {
        Points = [new Point(0, 0), new Point(MarkWidth + 3, 3), new Point(0, 6)],
        Fill = Accent,
    };

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        double y = e.GetPosition(this).Y + this.view.ViewportTop;
        if (this.view.TextViewLines.GetTextViewLineContainingYCoordinate(y) is not ITextViewLine line || this.regionsSnapshot != this.view.TextSnapshot)
        {
            return;
        }

        int number = line.Start.GetContainingLine().LineNumber;
        ChangedRegion? region = this.regions.FirstOrDefault(r => !r.IsRemoval && number >= r.NewStart && number < r.NewStart + r.NewCount)
            ?? this.regions.FirstOrDefault(r => r.IsRemoval && (r.NewStart == number || r.NewStart == number + 1));
        if (region is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        string what = region.IsRemoval
            ? (region.OldCount == 1 ? "Put back the line Claude removed" : $"Put back the {region.OldCount} lines Claude removed")
            : region.OldCount == 0
                ? (region.NewCount == 1 ? "Remove the line Claude added" : $"Remove the {region.NewCount} lines Claude added")
                : $"Undo Claude's change to {InlineEdit.Describe(region.NewStart + 1, region.NewStart + region.NewCount)}";
        menu.Items.Add(Item(what, () => this.Revert(region)));
        menu.Items.Add(Item("Compare with before this chat", this.Compare));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Puts one region back as it was before the chat, as an edit in the editor (so Ctrl+Z undoes it).</summary>
    private void Revert(ChangedRegion region)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ITextSnapshot snapshot = this.view.TextBuffer.CurrentSnapshot;
        if (snapshot != this.regionsSnapshot)
        {
            return; // the file changed since the menu opened
        }

        (int start, int length, string text) = ChatChanges.RevertEdit(snapshot.GetText(), region);
        using ITextEdit edit = this.view.TextBuffer.CreateEdit();
        edit.Replace(new Span(start, length), text);
        edit.Apply();
    }

    /// <summary>Opens Visual Studio's diff of the file before the chat and now.</summary>
    private void Compare()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string path = this.document.FilePath;
        if (ChatChanges.Before(path) is string before && ClaudePackage.Instance is ClaudePackage package)
        {
            package.Vs.ShowDiff(new FileChange(path, before, this.view.TextBuffer.CurrentSnapshot.GetText()), $"{System.IO.Path.GetFileName(path)}: changes in this chat", "now");
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
