using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeForVS.Markdown;

namespace ClaudeForVS.ToolWindows;

/// <summary>Find in this chat: searches the rendered messages and replies, and selects each match in turn.</summary>
public partial class ClaudeChatControl
{
    // The current match, as the index of its message among those searched and the offset of its start in that message.
    // Kept as positions rather than TextPointers because a streaming reply re-renders its document.
    private (int View, int Offset)? currentMatch;
    private MarkdownView? selectedView;

    private void InitializeFind()
    {
        // Ctrl+F reaches here when Visual Studio doesn't route Edit.Find to the window itself (see ClaudeToolWindow).
        this.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                this.OpenFind();
                e.Handled = true;
            }
            else if (e.Key == Key.F3 && this.FindBar.Visibility == Visibility.Visible)
            {
                this.FindStep(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
            }
        };
    }

    /// <summary>Shows the find bar with its text selected, ready to type.</summary>
    internal void OpenFind()
    {
        this.viewModel.ShowHistory = false;
        this.FindBar.Visibility = Visibility.Visible;
        this.FindBox.Focus();
        this.FindBox.SelectAll();
        if (this.FindBox.Text.Length > 0)
        {
            this.currentMatch = null;
            this.FindStep(forward: true);
        }
    }

    private void OnOpenFind(object sender, RoutedEventArgs e) => this.OpenFind();

    private void OnCloseFind(object sender, RoutedEventArgs e) => this.CloseFind();

    private void OnFindNext(object sender, RoutedEventArgs e) => this.FindStep(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => this.FindStep(forward: false);

    private void CloseFind()
    {
        this.ClearFindSelection();
        this.currentMatch = null;
        this.FindBar.Visibility = Visibility.Collapsed;
        this.PromptBox.Focus();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        // Start again from the match at (or just after) the current one, so typing narrows it down in place.
        if (this.currentMatch is (int view, int offset))
        {
            this.currentMatch = (view, offset - 1);
        }

        this.FindStep(forward: true);
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                this.FindStep(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.Escape:
                this.CloseFind();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Moves to the next (or previous) match, wrapping around, and shows how many there are.</summary>
    private void FindStep(bool forward)
    {
        string query = this.FindBox.Text;
        this.ClearFindSelection();
        if (query.Length == 0)
        {
            this.FindCount.Text = string.Empty;
            this.currentMatch = null;
            return;
        }

        List<MarkdownView> views = this.MessageViews();
        var matches = new List<(int View, int Offset, TextPointer Start, TextPointer End)>();
        for (int v = 0; v < views.Count; v++)
        {
            foreach ((int offset, TextPointer start, TextPointer end) in FindIn(views[v].Document, query))
            {
                matches.Add((v, offset, start, end));
            }
        }

        if (matches.Count == 0)
        {
            this.FindCount.Text = "No matches";
            this.currentMatch = null;
            return;
        }

        int index;
        if (this.currentMatch is not (int curView, int curOffset))
        {
            index = forward ? 0 : matches.Count - 1;
        }
        else if (forward)
        {
            index = matches.FindIndex(m => m.View > curView || (m.View == curView && m.Offset > curOffset));
            index = index < 0 ? 0 : index;
        }
        else
        {
            index = matches.FindLastIndex(m => m.View < curView || (m.View == curView && m.Offset < curOffset));
            index = index < 0 ? matches.Count - 1 : index;
        }

        var match = matches[index];
        this.currentMatch = (match.View, match.Offset);
        this.FindCount.Text = $"{index + 1} of {matches.Count}";

        MarkdownView target = views[match.View];
        target.IsInactiveSelectionHighlightEnabled = true; // show the match while focus stays in the find box
        target.Selection.Select(match.Start, match.End);
        this.selectedView = target;
        this.ScrollToMatch(target, match.Start);
    }

    private void ClearFindSelection()
    {
        if (this.selectedView is MarkdownView view)
        {
            view.Selection.Select(view.Document.ContentStart, view.Document.ContentStart);
            view.IsInactiveSelectionHighlightEnabled = false;
            this.selectedView = null;
        }
    }

    /// <summary>Every rendered message and reply in the transcript, top to bottom.</summary>
    private List<MarkdownView> MessageViews()
    {
        var views = new List<MarkdownView>();
        void Walk(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is MarkdownView view)
                {
                    views.Add(view);
                }
                else
                {
                    Walk(child);
                }
            }
        }

        Walk(this.Transcript);
        return views;
    }

    /// <summary>Case-insensitive matches within each run of text (a match split by formatting isn't found).</summary>
    private static IEnumerable<(int Offset, TextPointer Start, TextPointer End)> FindIn(FlowDocument document, string query)
    {
        for (TextPointer? pointer = document.ContentStart; pointer != null; pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            {
                continue;
            }

            string run = pointer.GetTextInRun(LogicalDirection.Forward);
            for (int at = run.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); at >= 0; at = run.IndexOf(query, at + query.Length, StringComparison.CurrentCultureIgnoreCase))
            {
                TextPointer start = pointer.GetPositionAtOffset(at);
                yield return (document.ContentStart.GetOffsetToPosition(start), start, pointer.GetPositionAtOffset(at + query.Length));
            }
        }
    }

    /// <summary>Scrolls the transcript so the match sits a third of the way down, and stops following new output.</summary>
    private void ScrollToMatch(MarkdownView view, TextPointer start)
    {
        if (this.Transcript.Content is not UIElement content)
        {
            return;
        }

        Rect rect = start.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
        {
            return;
        }

        Point inContent = view.TranslatePoint(rect.TopLeft, content);
        this.followOutput = false;
        this.Transcript.ScrollToVerticalOffset(Math.Max(0, inContent.Y - (this.Transcript.ViewportHeight / 3)));
    }
}
