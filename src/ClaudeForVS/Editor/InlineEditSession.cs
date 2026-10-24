using System;
using System.ComponentModel.Composition;
using System.IO;
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
/// Editing code in place (Alt+/): asks what to change, sends the lines to Claude, and replaces them with its reply as
/// one edit, which Undo here (or Ctrl+Z) takes back. The selection is edited, or else the member the caret is in, or
/// else the caret's line. One at a time in each editor.
/// </summary>
internal sealed class InlineEditSession
{
    // A type or member longer than this is too much to rewrite for a small request; the caret's line is used instead.
    private const int MaxMemberLines = 200;

    private static readonly Brush Highlight = Frozen(new SolidColorBrush(Color.FromArgb(0x1C, 0xD9, 0x77, 0x57)));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));

    private readonly IWpfTextView view;
    private readonly IAdornmentLayer layer;
    private readonly ITrackingSpan span;
    private readonly string original;
    private readonly CancellationTokenSource cancel = new();
    private readonly Border marker;
    private readonly TextBlock status;
    private readonly StackPanel buttons;
    private ITextSnapshot? appliedSnapshot;

    private InlineEditSession(IWpfTextView view, ITrackingSpan span)
    {
        this.view = view;
        this.layer = view.GetAdornmentLayer(InlineEditLayer.Name);
        this.span = span;
        this.original = span.GetText(view.TextSnapshot);
        this.status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        this.status.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        this.buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = "✻ ", Foreground = Accent, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(this.status);
        content.Children.Add(this.buttons);
        this.marker = new Border
        {
            Child = content,
            BorderBrush = Accent,
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
            if (running.appliedSnapshot is null)
            {
                return; // still waiting for Claude; one at a time
            }

            running.Finish(); // a new edit keeps the last one
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

        var session = new InlineEditSession(view, snapshot.CreateTrackingSpan(lines, SpanTrackingMode.EdgeInclusive));
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
            session.Finish();
            return;
        }
        catch (OperationCanceledException)
        {
            session.Show("Claude took too long, so nothing was changed.", ("Close", session.Finish));
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Http.HttpRequestException or Anthropic.Exceptions.AnthropicException)
        {
            session.Show("Claude couldn't edit this: " + OneLine(ex.Message), ("Close", session.Finish));
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        session.Apply(code);
    }

    private void Apply(string code)
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

        using (ITextEdit edit = this.view.TextBuffer.CreateEdit())
        {
            edit.Replace(this.span.GetSpan(now), replacement);
            this.appliedSnapshot = edit.Apply();
        }

        this.view.TextBuffer.Changed += this.OnBufferChanged;
        this.Show("Edited. Keep it?", ("Keep", this.Finish), ("Undo", this.Undo));
    }

    /// <summary>Puts the lines back as they were, if they haven't been changed again since.</summary>
    private void Undo()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ITextSnapshot now = this.view.TextBuffer.CurrentSnapshot;
        if (now == this.appliedSnapshot)
        {
            this.view.TextBuffer.Changed -= this.OnBufferChanged;
            using ITextEdit edit = this.view.TextBuffer.CreateEdit();
            edit.Replace(this.span.GetSpan(now), this.original);
            edit.Apply();
        }

        this.Finish();
    }

    // Typing anything after the edit keeps it, and Ctrl+Z still takes it back.
    private void OnBufferChanged(object sender, TextContentChangedEventArgs e) => this.Finish();

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
        this.view.TextBuffer.Changed -= this.OnBufferChanged;
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
            var tint = new System.Windows.Shapes.Rectangle { Width = Math.Max(this.view.ViewportWidth, bounds.Width), Height = Math.Max(bounds.Height, this.view.LineHeight), Fill = Highlight, IsHitTestVisible = false };
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
        double top = first.Top - this.marker.DesiredSize.Height - 2;
        Canvas.SetLeft(this.marker, Math.Max(this.view.ViewportLeft, this.view.ViewportRight - this.marker.DesiredSize.Width - 24));
        Canvas.SetTop(this.marker, top >= this.view.ViewportTop ? top : first.Bottom + 2);
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

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
