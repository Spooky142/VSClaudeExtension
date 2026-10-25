using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeForVS.Agent;
using ClaudeForVS.ToolWindows;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace ClaudeForVS.Editor;

/// <summary>The editor layer the inline edit draws on: the lines being edited, and its status and buttons beside them.</summary>
internal sealed class InlineEditLayer
{
    public const string Name = "ClaudeInlineEdit";

#pragma warning disable CS0649, IDE0044 // Set by MEF.
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(Name)]
    [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
    public AdornmentLayerDefinition? Layer;
#pragma warning restore CS0649, IDE0044
}

/// <summary>
/// Editing code in place (Ctrl+Alt+/): asks what to change, sends the lines to Claude, and shows its reply as a diff
/// beside them. Apply replaces the lines as one edit (Ctrl+Z takes it back); Discard leaves them. The selection is
/// edited, or else the member the caret is in, or else the caret's line. One at a time in each editor.
/// </summary>
internal sealed class InlineEditSession
{
    // A type or member longer than this is too much to rewrite for a small request; the caret's line is used instead.
    private const int MaxMemberLines = 200;

    // The most diff lines shown beside the code; the rest can be seen with Open diff.
    private const int MaxPreviewLines = 40;

    private static readonly Brush Highlight = HighContrast.Frozen(new SolidColorBrush(Color.FromArgb(0x1C, 0xD9, 0x77, 0x57)));
    private static readonly Brush AddedTint = HighContrast.Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0x2E, 0xA0, 0x43)));
    private static readonly Brush RemovedTint = HighContrast.Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xF8, 0x51, 0x49)));

    private readonly IWpfTextView view;
    private readonly string filePath;
    private readonly IAdornmentLayer layer;
    private readonly ITrackingSpan span;
    private readonly string original;
    private readonly CancellationTokenSource cancel = new();
    private readonly Border marker;
    private readonly TextBlock status;
    private readonly StackPanel buttons;
    private readonly Border preview;

    // Claude's replacement for the lines, while it's shown for the user to apply or discard.
    private string? proposed;

    // True while Claude is working on the edit; afterwards the marker only shows the result or an error.
    private bool working = true;

    private InlineEditSession(IWpfTextView view, string filePath, ITrackingSpan span)
    {
        this.view = view;
        this.filePath = filePath;
        this.layer = view.GetAdornmentLayer(InlineEditLayer.Name);
        this.span = span;
        this.original = span.GetText(view.TextSnapshot);
        this.status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        this.status.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        this.buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = "✻ ", Foreground = HighContrast.EditorAccent, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(this.status);
        header.Children.Add(this.buttons);
        this.preview = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 4, 1), BorderThickness = new Thickness(0, 1, 0, 0) };
        this.preview.SetResourceReference(Border.BorderBrushProperty, VsBrushes.GrayTextKey);
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(this.preview);
        this.marker = new Border
        {
            Child = content,
            BorderBrush = HighContrast.EditorAccent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 4, 3),
            Focusable = false,
        };
        this.marker.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        TextElement.SetFontSize(this.marker, 11.5);
    }

    /// <summary>The session running in an editor, if any.</summary>
    private static InlineEditSession? Current(ITextView view) => view.Properties.TryGetProperty(typeof(InlineEditSession), out InlineEditSession session) ? session : null;

    /// <summary>Asks what to change and, if the user says, edits the code. Call on the UI thread.</summary>
    public static async Task StartAsync(IWpfTextView view, string filePath, string repoRoot, AgentSettings settings)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Current(view) is InlineEditSession running)
        {
            if (running.working)
            {
                return; // still waiting for Claude; one at a time
            }

            running.Finish(); // a new edit discards the last one's diff, or closes its message
        }

        (int startLine, int endLine) = await ChooseLinesAsync(view);
        ITextSnapshot snapshot = view.TextSnapshot;
        SnapshotSpan lines = new(snapshot.GetLineFromLineNumber(startLine).Start, snapshot.GetLineFromLineNumber(endLine).End);
        string heading = $"Edit {InlineEdit.Describe(startLine + 1, endLine + 1)} with Claude";

        var dialog = new InlineEditDialog(heading, CaretScreenPosition(view));
        if (dialog.ShowModal() != true || dialog.Instruction.Length == 0)
        {
            return;
        }

        var session = new InlineEditSession(view, filePath, snapshot.CreateTrackingSpan(lines, SpanTrackingMode.EdgeInclusive));
        view.Properties[typeof(InlineEditSession)] = session;
        session.Show("Claude is editing…", ("Cancel", session.cancel.Cancel));
        view.LayoutChanged += session.OnLayoutChanged;
        view.Closed += session.OnClosed;

        string displayPath = DisplayPath(filePath, repoRoot);
        string fileText = snapshot.GetText();
        string instruction = dialog.Instruction;
        string code;
        try
        {
            code = await Task.Run(() => InlineEdit.RequestAsync(settings, repoRoot, displayPath, fileText, startLine + 1, endLine + 1, instruction, session.cancel.Token));
        }
        catch (OperationCanceledException) when (session.cancel.IsCancellationRequested)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            session.working = false;
            session.Finish();
            return;
        }
        catch (OperationCanceledException)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            session.working = false;
            session.Show("Claude took too long, so nothing was changed.", ("Close", session.Finish));
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Anything else (Claude Code missing, a network or API error) is shown; otherwise the session would stay
            // "editing" and block Ctrl+Alt+/ in this editor.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            session.working = false;
            session.Show("Claude couldn't edit this: " + OneLine(ex.Message), ("Close", session.Finish));
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        session.working = false;
        session.Propose(code);
    }

    /// <summary>Shows Claude's change to the lines as a diff, for the user to apply or discard.</summary>
    private void Propose(string code)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ITextSnapshot now = this.view.TextBuffer.CurrentSnapshot;
        if (this.view.IsClosed || this.span.GetText(now) != this.original)
        {
            this.Show("The code changed while Claude was working, so it was left as it is.", ("Close", this.Finish));
            return;
        }

        string newline = NewlineOf(now, this.span.GetSpan(now));
        string replacement = InlineEdit.ToFileNewlines(code, newline);
        if (replacement == this.original)
        {
            this.Show("Claude had no changes to make.", ("Close", this.Finish));
            return;
        }

        this.proposed = replacement;
        this.preview.Child = this.DiffView(this.original, replacement);
        this.preview.Visibility = Visibility.Visible;
        this.Show("Claude's edit:", ("Apply", this.Accept), ("Open diff", this.OpenDiff), ("Discard", this.Finish));
    }

    /// <summary>Replaces the lines with Claude's, as one edit that Ctrl+Z takes back, unless they've changed since.</summary>
    private void Accept()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ITextSnapshot now = this.view.TextBuffer.CurrentSnapshot;
        if (this.proposed is not string replacement || this.view.IsClosed || this.span.GetText(now) != this.original)
        {
            this.preview.Visibility = Visibility.Collapsed;
            this.Show("The code changed since Claude's edit, so it was left as it is.", ("Close", this.Finish));
            return;
        }

        using (ITextEdit edit = this.view.TextBuffer.CreateEdit())
        {
            edit.Replace(this.span.GetSpan(now), replacement);
            edit.Apply();
        }

        this.Finish();
    }

    /// <summary>Opens the whole file, as it is and with Claude's edit, in Visual Studio's diff viewer.</summary>
    private void OpenDiff()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.proposed is not string replacement || this.view.IsClosed || ClaudePackage.Instance is not ClaudePackage package)
        {
            return;
        }

        ITextSnapshot now = this.view.TextBuffer.CurrentSnapshot;
        SnapshotSpan lines = this.span.GetSpan(now);
        string text = now.GetText();
        string after = text.Substring(0, lines.Start.Position) + replacement + text.Substring(lines.End.Position);
        package.Vs.ShowDiff(new FileChange(this.filePath, text, after), $"{Path.GetFileName(this.filePath)}: Claude's edit");
    }

    /// <summary>The changed lines with a line of context around them, in the editor's font, removed lines tinted red and added green.</summary>
    private FrameworkElement DiffView(string before, string after)
    {
        var lines = new StackPanel();
        System.Windows.Media.TextFormatting.TextRunProperties text = this.view.FormattedLineSource.DefaultTextProperties;
        IReadOnlyList<ReviewLine> review = ChangeBlocks.Compute(before, after, context: 1)?.Lines ?? [];
        int shown = 0;
        foreach (ReviewLine line in review)
        {
            if (line.Op == '@')
            {
                if (lines.Children.Count > 0)
                {
                    lines.Children.Add(Line("⋯", null, text)); // lines left out between two changes
                }

                continue;
            }

            if (shown++ == MaxPreviewLines)
            {
                lines.Children.Add(Line($"… {review.Count(l => l.Op != '@') - MaxPreviewLines} more lines: Open diff shows them all", null, text));
                break;
            }

            // In a high-contrast theme the tints would vanish, so the "+" and "-" alone say which is which.
            Brush? tint = HighContrast.IsOn ? null : line.Op switch { '+' => AddedTint, '-' => RemovedTint, _ => null };
            lines.Children.Add(Line(line.Display, tint, text));
        }

        if (review.Count == 0)
        {
            lines.Children.Add(Line("The change is too large to show here: Open diff shows it.", null, text));
        }

        return new ScrollViewer
        {
            Content = lines,
            MaxWidth = Math.Max(240, this.view.ViewportWidth * 0.6),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }

    private static TextBlock Line(string content, Brush? tint, System.Windows.Media.TextFormatting.TextRunProperties text)
    {
        var line = new TextBlock
        {
            Text = content.Replace("\t", "    "),
            Background = tint,
            FontFamily = text.Typeface.FontFamily,
            FontSize = text.FontRenderingEmSize,
            Padding = new Thickness(4, 0, 8, 0),
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        return line;
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => this.Place();

    private void OnClosed(object sender, EventArgs e)
    {
        this.cancel.Cancel();
        this.Finish();
    }

    /// <summary>Removes the marker and stops listening; the edit (if any) stays.</summary>
    private void Finish()
    {
        this.view.LayoutChanged -= this.OnLayoutChanged;
        this.view.Closed -= this.OnClosed;
        this.layer.RemoveAllAdornments();
        if (Current(this.view) == this)
        {
            this.view.Properties.RemoveProperty(typeof(InlineEditSession));
        }
    }

    private void Show(string message, params (string Label, Action Click)[] actions)
    {
        this.status.Text = message;
        this.buttons.Children.Clear();
        foreach ((string label, Action click) in actions)
        {
            var button = new Button { Content = label, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1), Focusable = false, Cursor = System.Windows.Input.Cursors.Hand };
            HighContrast.UseVsStyle(button); // the editor has none of the chat's styles, so it would otherwise be drawn light in a dark theme
            button.Click += (_, _) => click();
            this.buttons.Children.Add(button);
        }

        this.Place();
    }

    /// <summary>Tints the lines and puts the marker at the right of the first of them, when they're in view.</summary>
    private void Place()
    {
        if (this.view.IsClosed || this.view.InLayout)
        {
            return;
        }

        this.layer.RemoveAllAdornments();
        SnapshotSpan current = this.span.GetSpan(this.view.TextSnapshot);
        if (this.view.TextViewLines.GetMarkerGeometry(current.Length > 0 ? current : new SnapshotSpan(current.Start, 0)) is Geometry geometry)
        {
            Rect bounds = geometry.Bounds;
            var tint = new System.Windows.Shapes.Rectangle { Width = Math.Max(this.view.ViewportWidth, bounds.Width), Height = Math.Max(bounds.Height, this.view.LineHeight), IsHitTestVisible = false };
            if (HighContrast.IsOn)
            {
                // A faint tint vanishes in a high-contrast theme; an outline in the theme's highlight colour doesn't.
                tint.Stroke = HighContrast.EditorAccent;
                tint.StrokeThickness = 1;
            }
            else
            {
                tint.Fill = Highlight;
            }

            Canvas.SetLeft(tint, this.view.ViewportLeft);
            Canvas.SetTop(tint, bounds.Top);
            this.layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, current, null, tint, null);
        }

        ITextViewLine? first = this.view.TextViewLines.GetTextViewLineContainingBufferPosition(current.Start);
        if (first is null || first.VisibilityState == VisibilityState.Unattached)
        {
            return;
        }

        this.marker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(this.marker, Math.Max(this.view.ViewportLeft, this.view.ViewportRight - this.marker.DesiredSize.Width - 24));
        double above = first.Top - this.marker.DesiredSize.Height - 2;
        if (this.preview.Visibility == Visibility.Visible
            && this.view.TextViewLines.GetTextViewLineContainingBufferPosition(current.End) is ITextViewLine last
            && last.VisibilityState != VisibilityState.Unattached
            && (last.Bottom + this.marker.DesiredSize.Height <= this.view.ViewportBottom || above < this.view.ViewportTop))
        {
            Canvas.SetTop(this.marker, last.Bottom + 2); // the diff goes below the lines, so they stay in sight beside it
        }
        else
        {
            Canvas.SetTop(this.marker, above >= this.view.ViewportTop ? above : first.Bottom + 2);
        }
        this.layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, this.marker, null);
    }

    /// <summary>The lines to edit (0-based): the selection's, else the member the caret is in, else the caret's.</summary>
    private static async Task<(int Start, int End)> ChooseLinesAsync(IWpfTextView view)
    {
        ITextSnapshot snapshot = view.TextSnapshot;
        if (!view.Selection.IsEmpty)
        {
            SnapshotSpan selected = new(view.Selection.Start.Position, view.Selection.End.Position);
            int start = snapshot.GetLineNumberFromPosition(selected.Start);
            int end = snapshot.GetLineNumberFromPosition(selected.End);

            // A selection ending at the start of a line doesn't include that line.
            if (end > start && snapshot.GetLineFromLineNumber(end).Start == selected.End)
            {
                end--;
            }

            return (start, end);
        }

        int caret = view.Caret.Position.BufferPosition.Position;
        int caretLine = snapshot.GetLineNumberFromPosition(caret);
        try
        {
            if (snapshot.GetOpenDocumentInCurrentContextWithChanges() is Microsoft.CodeAnalysis.Document document
                && await RoslynCodeIntelligence.FindEnclosingMemberAsync(document, caret, CancellationToken.None) is EnclosingMember member
                && member.EndLine - member.StartLine < MaxMemberLines
                && member.EndLine <= snapshot.LineCount)
            {
                return (member.StartLine - 1, member.EndLine - 1);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or MissingMemberException or TypeLoadException)
        {
            // No code model on this version of Visual Studio: the caret's line, as for other languages.
        }

        return (caretLine, caretLine);
    }

    /// <summary>Just below the caret, in the screen units WPF windows are placed in.</summary>
    private static Point CaretScreenPosition(IWpfTextView view)
    {
        var inView = new Point(Math.Max(0, view.Caret.Left - view.ViewportLeft), view.Caret.Bottom - view.ViewportTop + 4);
        Point screen = view.VisualElement.PointToScreen(inView);
        return PresentationSource.FromVisual(view.VisualElement)?.CompositionTarget?.TransformFromDevice.Transform(screen) ?? screen;
    }

    private static string NewlineOf(ITextSnapshot snapshot, SnapshotSpan around)
    {
        ITextSnapshotLine line = snapshot.GetLineFromPosition(around.Start);
        for (int i = line.LineNumber; i < snapshot.LineCount; i++)
        {
            string ending = snapshot.GetLineFromLineNumber(i).GetLineBreakText();
            if (ending.Length > 0)
            {
                return ending;
            }
        }

        return Environment.NewLine;
    }

    private static string DisplayPath(string fullPath, string repoRoot)
    {
        string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : Path.GetFileName(fullPath)).Replace('\\', '/');
    }

    private static string OneLine(string text)
    {
        string line = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return line.Length > 160 ? line.Substring(0, 160) + "…" : line;
    }
}
