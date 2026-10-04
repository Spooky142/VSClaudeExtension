using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.ToolWindows;

/// <summary>The prompt box: sending, Up/Down history, completion, and pasted or dropped images and files.</summary>
public partial class ClaudeChatControl
{
    // Where the text replaced by the selected completion starts.
    private int suggestionStart;

    // Set while the prompt text is changed in code, so it isn't mistaken for typing.
    private bool settingPrompt;

    private void InitializeComposer()
    {
        // Paste is intercepted for images and files; text pastes go through as normal.
        CommandManager.AddPreviewExecutedHandler(this.PromptBox, this.OnPromptPreviewExecuted);
    }

    private void SendPrompt()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.CloseSuggestions();
        this.RefreshEditorContext();
        if (this.viewModel.TrySend(this.PromptBox.Text, out Task run))
        {
            this.SetPromptText(string.Empty);
            run.FileAndForget("ClaudeForVS/Send");
        }
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.SendPrompt();
    }

    private void OnStop(object sender, RoutedEventArgs e) => this.viewModel.Stop();

    /// <summary>Replaces the prompt text from code (recall, edit, canned prompts) and puts the caret at the end.</summary>
    private void SetPromptText(string text)
    {
        this.settingPrompt = true;
        this.PromptBox.Text = text;
        this.PromptBox.CaretIndex = text.Length;
        this.settingPrompt = false;
        this.PromptBox.Focus();
    }

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.SuggestionPopup.IsOpen && this.HandleSuggestionKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                e.Handled = true;
                this.SendPrompt();
                break;

            // Up on the first line recalls older prompts when the box is empty or already showing one.
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None
                && this.PromptBox.GetLineIndexFromCharacterIndex(this.PromptBox.CaretIndex) <= 0
                && (this.PromptBox.Text.Length == 0 || this.viewModel.History.IsBrowsing):
                if (this.viewModel.History.Older(this.PromptBox.Text) is string older)
                {
                    this.SetPromptText(older);
                    e.Handled = true;
                }

                break;

            case Key.Down when Keyboard.Modifiers == ModifierKeys.None
                && this.viewModel.History.IsBrowsing
                && this.PromptBox.GetLineIndexFromCharacterIndex(this.PromptBox.CaretIndex) >= this.PromptBox.LineCount - 1:
                if (this.viewModel.History.Newer() is string newer)
                {
                    this.SetPromptText(newer);
                    e.Handled = true;
                }

                break;
        }
    }

    private void OnPromptTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!this.settingPrompt)
        {
            this.viewModel.History.Reset(); // typing over a recalled prompt makes it a new draft
            this.UpdateSuggestions();
        }
    }

    private void OnPromptSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (this.SuggestionPopup.IsOpen)
        {
            this.UpdateSuggestions(); // the caret moved, so the word being completed may have changed
        }
    }

    private void OnPromptLostFocus(object sender, KeyboardFocusChangedEventArgs e) => this.CloseSuggestions();

    // ===== Completion =====

    private void UpdateSuggestions()
    {
        IReadOnlyList<Suggestion> suggestions = this.viewModel.GetSuggestions(this.PromptBox.Text, this.PromptBox.CaretIndex, out int start);
        string typed = this.PromptBox.Text.Substring(start, Math.Max(0, this.PromptBox.CaretIndex - start));
        if (suggestions.Count == 0 || (suggestions.Count == 1 && suggestions[0].Insert.TrimEnd() == typed.TrimEnd()))
        {
            this.CloseSuggestions();
            return;
        }

        this.suggestionStart = start;
        this.SuggestionList.ItemsSource = suggestions;
        this.SuggestionList.SelectedIndex = 0;
        this.SuggestionList.ScrollIntoView(suggestions[0]);
        this.SuggestionPopup.IsOpen = true;
    }

    private void CloseSuggestions() => this.SuggestionPopup.IsOpen = false;

    private bool HandleSuggestionKey(Key key)
    {
        int count = this.SuggestionList.Items.Count;
        switch (key)
        {
            case Key.Down:
                this.SuggestionList.SelectedIndex = (this.SuggestionList.SelectedIndex + 1) % count;
                this.SuggestionList.ScrollIntoView(this.SuggestionList.SelectedItem);
                return true;
            case Key.Up:
                this.SuggestionList.SelectedIndex = (this.SuggestionList.SelectedIndex - 1 + count) % count;
                this.SuggestionList.ScrollIntoView(this.SuggestionList.SelectedItem);
                return true;
            case Key.Tab:
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                if (this.SuggestionList.SelectedItem is Suggestion suggestion)
                {
                    this.AcceptSuggestion(suggestion);
                    return true;
                }

                return false;
            case Key.Escape:
                this.CloseSuggestions();
                return true;
            default:
                return false;
        }
    }

    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Suggestion suggestion)
        {
            this.AcceptSuggestion(suggestion);
            e.Handled = true;
        }
    }

    private void AcceptSuggestion(Suggestion suggestion)
    {
        string text = this.PromptBox.Text;
        int caret = Math.Min(this.PromptBox.CaretIndex, text.Length);
        int start = Math.Min(this.suggestionStart, caret);
        string updated = text.Substring(0, start) + suggestion.Insert + text.Substring(caret);

        this.settingPrompt = true;
        this.PromptBox.Text = updated;
        this.PromptBox.CaretIndex = start + suggestion.Insert.Length;
        this.settingPrompt = false;
        this.PromptBox.Focus();

        // A command that takes an argument goes straight on to suggesting its values.
        this.CloseSuggestions();
        this.UpdateSuggestions();
    }

    // ===== Pasted and dropped images and files =====

    private void OnPromptPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
            {
                this.AttachBitmap(image, "Pasted image");
                e.Handled = true;
            }
            else if (Clipboard.ContainsFileDropList())
            {
                this.AddFiles(Clipboard.GetFileDropList().Cast<string>());
                e.Handled = true;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy; let the normal paste try.
        }
    }

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            this.AddFiles(files);
            e.Handled = true;
        }
        else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            this.AttachBitmap(bitmap, "Dropped image");
            e.Handled = true;
        }
    }

    private void AttachBitmap(BitmapSource bitmap, string name)
    {
        try
        {
            this.viewModel.AddAttachment(ImageAttachments.FromBitmap(bitmap, name));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            this.viewModel.ShowStatus("Couldn't attach that image: " + ex.Message);
        }

        this.PromptBox.Focus();
    }

    /// <summary>Images become attachments; other files and folders are mentioned in the prompt by path.</summary>
    private void AddFiles(IEnumerable<string> paths)
    {
        var mentions = new List<string>();
        foreach (string path in paths)
        {
            if (PromptImageFile(path) is Attachment image)
            {
                this.viewModel.AddAttachment(image);
                continue;
            }

            string display = Attachment.DisplayPath(path, this.viewModel.RepoRoot ?? string.Empty);
            mentions.Add(display.Contains(' ') ? $"\"{display}\"" : "@" + display);
        }

        if (mentions.Count > 0)
        {
            string text = this.PromptBox.Text;
            int caret = this.PromptBox.CaretIndex;
            string before = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : string.Empty;
            string insert = before + string.Join(" ", mentions) + " ";
            this.settingPrompt = true;
            this.PromptBox.Text = text.Insert(caret, insert);
            this.PromptBox.CaretIndex = caret + insert.Length;
            this.settingPrompt = false;
        }

        this.PromptBox.Focus();
    }

    private Attachment? PromptImageFile(string path)
    {
        if (Agent.PromptImage.MediaTypeFor(path) is null || !File.Exists(path))
        {
            return null;
        }

        Attachment? image = ImageAttachments.FromFile(path);
        if (image is null)
        {
            this.viewModel.ShowStatus($"Couldn't read {Path.GetFileName(path)} as an image.");
        }

        return image;
    }
}
