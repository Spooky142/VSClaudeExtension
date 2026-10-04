using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;
using ClaudeForVS.Markdown;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.ToolWindows;

public partial class ClaudeChatControl : UserControl
{
    // WPF scrolls 3 lines of 16px per wheel notch.
    private const double PixelsPerWheelNotch = 48;

    private readonly ClaudePackage package;
    private readonly ClaudeChatViewModel viewModel = new();

    /// <summary>True while the transcript is scrolled to the bottom; new output then keeps it there.</summary>
    private bool followOutput = true;

    /// <summary>Set after the first Loaded event, when the open solution's repository is picked up.</summary>
    private bool loadedOnce;

    private readonly DispatcherTimer usageTimer;

    public ClaudeChatControl(ClaudePackage package)
    {
        this.package = package;
        this.InitializeComponent();
        this.DataContext = this.viewModel;
        this.viewModel.PromptSent += (_, _) =>
        {
            this.followOutput = true;
            this.Transcript.ScrollToEnd();
        };
        this.viewModel.EditRequested += (_, text) => this.SetPromptText(text);
        this.viewModel.AttentionNeeded += (_, message) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.package.Vs.RequestAttention(message);
        };
        this.viewModel.Confirm = message =>
            MessageBox.Show(message, "Claude", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        this.viewModel.ChooseExportPath = ChooseExportPath;
        this.viewModel.CopyText = text =>
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        };

        // Replies link file paths to the editor, and colour code to suit the theme.
        FileLinks.Resolve = this.ResolveFile;
        FileLinks.Open = target =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!this.package.Vs.OpenFile(target.FullPath, target.Line, target.EndLine))
            {
                this.viewModel.ShowStatus($"Couldn't open {target.FullPath}.");
            }
        };
        MarkdownRenderer.IsDarkTheme = this.IsDarkTheme;

        // Theme colours follow a theme change by themselves, but code blocks are coloured for a light or dark theme when
        // they're rendered. Redraw them once the new theme's resources are in place. The pane lives as long as Visual
        // Studio, so this subscription never needs removing.
        VSColorTheme.ThemeChanged += _ => package.JoinableTaskFactory.RunAsync(async () =>
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            this.OnThemeChanged();
        }).Task.FileAndForget("ClaudeForVS/ThemeChanged");

        this.InitializeComposer();
        this.InitializeFind();

        // The pane lives as long as Visual Studio, so this subscription never needs removing.
        package.SolutionOpened += (_, directory) => this.viewModel.OnSolutionOpened(directory);
        OptionsPage.Applied += (_, settings) => this.viewModel.ApplySettings(settings, this.viewModel.RepoPath, null);

        // Keep the usage strip's "resets in" countdowns current.
        this.usageTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => this.viewModel.RefreshUsage(), this.Dispatcher);

        // Coming back to the chat picks up whatever is now open in the editor.
        this.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                this.RefreshEditorContext();
            }
        };

        this.Loaded += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!this.loadedOnce)
            {
                // Loaded fires each time the window is shown; only pick up the open solution the first time,
                // so re-showing the window doesn't undo a repository chosen in Settings since.
                this.loadedOnce = true;
                string? solutionDir = this.package.GetSolutionDirectory();
                if (GitRunner.FindRepoRoot(solutionDir) != null)
                {
                    this.viewModel.OnSolutionOpened(solutionDir!);
                }
                else if (this.viewModel.RepoPath.Length == 0)
                {
                    this.viewModel.SetRepoFromDirectory(solutionDir);
                }

                this.viewModel.RestoreLastSession();
            }

            this.RefreshEditorContext();
            this.viewModel.WarmFileIndex();
            this.PromptBox.Focus();
            if (this.viewModel.Settings.Engine == ChatEngineKind.ClaudeCode && this.viewModel.CodeStatus is null)
            {
                this.viewModel.RefreshClaudeCodeStatusAsync().FileAndForget("ClaudeForVS/CodeStatus");
            }

            if (this.viewModel.Settings.Engine == ChatEngineKind.ClaudeCode && !this.viewModel.IsBusy)
            {
                this.viewModel.RefreshPlanUsageAsync().FileAndForget("ClaudeForVS/PlanUsage");
            }
        };
    }

    // ===== Called by the package's commands =====

    /// <summary>Tools > Claude Chat: show the chat with the prompt box focused.</summary>
    internal void FocusPrompt()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.viewModel.ShowHistory = false;
        this.RefreshEditorContext();
        this.PromptBox.Focus();
    }

    /// <summary>
    /// The editor's Claude menu: attaches the selection (if any) and either sends <paramref name="prompt"/> or,
    /// with no prompt, leaves the user to type a question.
    /// </summary>
    internal void AskAbout(EditorContext? editor, string? prompt)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.viewModel.ShowHistory = false;
        this.viewModel.SetEditor(editor);
        if (editor is { HasSelection: true })
        {
            this.viewModel.AddSelection(editor);
        }

        if (prompt != null)
        {
            this.SendOrFill(prompt);
        }

        this.PromptBox.Focus();
    }

    /// <summary>The Error List's "Fix with Claude": attaches the errors and asks Claude to fix them.</summary>
    internal void FixErrors(IReadOnlyList<ErrorListItem> errors)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.viewModel.ShowHistory = false;
        if (errors.Count == 0)
        {
            this.viewModel.ShowStatus("The Error List has no errors to fix.");
            return;
        }

        this.viewModel.AddAttachment(Attachment.ForErrors(errors, this.viewModel.RepoRoot ?? this.viewModel.RepoPath));
        this.SendOrFill(errors.Count == 1 ? "Fix this error." : "Fix these errors.");
        this.PromptBox.Focus();
    }

    /// <summary>Sends a canned prompt straight away, unless Claude is busy or the user has a draft; then it waits in the box.</summary>
    private void SendOrFill(string prompt)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.PromptBox.Text.Trim().Length > 0)
        {
            this.viewModel.ShowStatus("Attached to your message. Send it when you're ready.");
            return;
        }

        this.SetPromptText(prompt);
        if (this.viewModel.IsIdle)
        {
            this.SendPrompt();
        }
    }

    private void RefreshEditorContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.viewModel.SetEditor(this.package.Vs.GetEditorContext());
    }

    /// <summary>Maps a path written in a reply to a file that exists: absolute, repository-relative, or a unique file name.</summary>
    private string? ResolveFile(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }

        if (this.viewModel.RepoRoot is string root)
        {
            string full = Path.GetFullPath(Path.Combine(root, path));
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                return full;
            }
        }

        return this.viewModel.FindRepoFile(path);
    }

    private void OnThemeChanged()
    {
        this.ClearFindSelection(); // rendering replaces the documents the selection was in
        foreach (MarkdownView view in this.MessageViews())
        {
            view.Rerender();
        }

        // Put the find highlight back, in the new theme's colour.
        if (this.FindBar.Visibility == Visibility.Visible && this.currentMatch is (int matchView, int offset))
        {
            this.UpdateLayout();
            this.currentMatch = (matchView, offset - 1);
            this.FindStep(forward: true);
        }
    }

    private bool IsDarkTheme()
    {
        if (this.TryFindResource(VsBrushes.ToolWindowBackgroundKey) is SolidColorBrush brush)
        {
            Color c = brush.Color;
            return (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B) < 128;
        }

        return true;
    }

    private static string? ChooseExportPath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export chat",
            FileName = suggestedName,
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // ===== Transcript =====

    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The user scrolled: follow new output only if they're at (or back at) the bottom.
            this.followOutput = this.Transcript.VerticalOffset >= this.Transcript.ScrollableHeight - 8;
        }
        else if (this.followOutput)
        {
            this.Transcript.ScrollToEnd();
        }
    }

    /// <summary>
    /// Message bodies are text controls with their own scroll handling, which would swallow the wheel.
    /// Scroll the transcript directly instead, wherever the pointer is.
    /// </summary>
    private void OnTranscriptMouseWheel(object sender, MouseWheelEventArgs e)
    {
        this.Transcript.ScrollToVerticalOffset(this.Transcript.VerticalOffset - (e.Delta / 120.0 * PixelsPerWheelNotch));
        e.Handled = true;
    }

    private static T? ContextOf<T>(object sender)
        where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void OnEditPrompt(object sender, RoutedEventArgs e)
    {
        if (ContextOf<ChatEntry>(sender) is ChatEntry entry)
        {
            this.viewModel.Edit(entry);
        }
    }

    private void OnRetryPrompt(object sender, RoutedEventArgs e)
    {
        if (ContextOf<ChatEntry>(sender) is ChatEntry entry)
        {
            this.viewModel.RetryAsync(entry).FileAndForget("ClaudeForVS/Retry");
        }
    }

    private void OnUndoPrompt(object sender, RoutedEventArgs e)
    {
        if (ContextOf<ChatEntry>(sender) is ChatEntry entry)
        {
            this.viewModel.UndoAsync(entry).FileAndForget("ClaudeForVS/Undo");
        }
    }

    private void OnCopyEntry(object sender, RoutedEventArgs e)
    {
        if (ContextOf<ChatEntry>(sender) is ChatEntry entry && sender is Button button)
        {
            try
            {
                Clipboard.SetText(entry.Text);
                button.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                button.Content = "Clipboard busy";
            }

            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                button.Content = "Copy";
            };
            reset.Start();
        }
    }

    private void OnOpenEntryDiff(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ContextOf<ChatEntry>(sender) is { Change: FileChange change } entry)
        {
            this.ShowDiff(change, entry.Header);
        }
    }

    private void OnOpenApprovalDiff(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.viewModel.ApprovalChange is FileChange change)
        {
            this.ShowDiff(change, this.viewModel.ApprovalTitle);
        }
    }

    private void ShowDiff(FileChange change, string caption)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            this.package.Vs.ShowDiff(change, caption);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            this.viewModel.ShowStatus("Couldn't open the diff: " + ex.Message);
        }
    }

    // ===== Header =====

    private void OnToggleHistory(object sender, RoutedEventArgs e) => this.viewModel.ShowHistory = !this.viewModel.ShowHistory;

    private void OnNewChat(object sender, RoutedEventArgs e) => this.viewModel.NewChat();

    /// <summary>The model chip's menu: switch model, effort or mode without opening Settings.</summary>
    private void OnModelMenu(object sender, RoutedEventArgs e)
    {
        AgentSettings current = this.viewModel.Settings;
        var menu = new ContextMenu { PlacementTarget = this.ModelChip, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        void Section(string title)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        }

        void Item(string header, bool isChecked, Action apply, string? toolTip = null)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked, ToolTip = toolTip };
            item.Click += (_, _) => apply();
            menu.Items.Add(item);
        }

        string? restartNote = this.viewModel.HasConversation ? "Starts a new chat" : null;
        Section("Model");
        foreach (ClaudeModel model in new[] { ClaudeModel.Opus, ClaudeModel.Sonnet, ClaudeModel.Haiku })
        {
            Item(AgentSettings.ModelName(model), model == current.Model, () => this.viewModel.QuickSet(model: model), model == current.Model ? null : restartNote);
        }

        Section("Effort");
        foreach (EffortLevel effort in new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh, EffortLevel.Max })
        {
            Item(AgentSettings.EffortName(effort), effort == current.Effort, () => this.viewModel.QuickSet(effort: effort));
        }

        Section("Mode");
        Item("Auto · edits without asking", current.Mode == AgentMode.Auto, () => this.viewModel.QuickSet(mode: AgentMode.Auto));
        Item("Plan · read-only", current.Mode == AgentMode.Plan, () => this.viewModel.QuickSet(mode: AgentMode.Plan));
        Item("Coding · asks before changes", current.Mode == AgentMode.Coding, () => this.viewModel.QuickSet(mode: AgentMode.Coding));

        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.OnOpenSettings(sender, e);
        };
        menu.Items.Add(settings);
        menu.IsOpen = true;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dialog = new SettingsDialog(
            this.viewModel.Settings,
            this.viewModel.RepoPath,
            this.viewModel.HasApiKey,
            this.viewModel.HasConversation,
            this.viewModel.CodeStatus,
            () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return this.package.GetSolutionDirectory();
            },
            (owner, start) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return this.package.BrowseForFolder(owner, "Choose a folder inside a git repository", start);
            });

        if (dialog.ShowModal() == true)
        {
            this.viewModel.ApplySettings(dialog.Result, dialog.ResultRepoPath, dialog.ResultApiKey);
        }

        // Install or sign-in may have happened inside the dialog, even if it was cancelled.
        this.viewModel.RefreshClaudeCodeStatusAsync().FileAndForget("ClaudeForVS/CodeStatus");
    }

    // ===== History tab =====

    private void OnOpenSession(object sender, RoutedEventArgs e)
    {
        if (ContextOf<SessionItem>(sender) is SessionItem item)
        {
            this.viewModel.OpenSession(item);
            this.PromptBox.Focus();
        }
    }

    private void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (ContextOf<SessionItem>(sender) is SessionItem item)
        {
            this.viewModel.DeleteSession(item);
        }
    }

    private void OnRenameSession(object sender, RoutedEventArgs e)
    {
        if (ContextOf<SessionItem>(sender) is SessionItem item)
        {
            this.viewModel.StartRename(item);
        }
    }

    private void OnRenameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            // Focus once the box is laid out.
            this.package.JoinableTaskFactory.RunAsync(async () =>
            {
                await this.package.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                box.Focus();
                box.SelectAll();
            }).Task.FileAndForget("ClaudeForVS/Rename");
        }
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (ContextOf<SessionItem>(sender) is not SessionItem item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            this.viewModel.CommitRename(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            item.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ContextOf<SessionItem>(sender) is { IsRenaming: true } item)
        {
            this.viewModel.CommitRename(item);
        }
    }

    private void OnExportSession(object sender, RoutedEventArgs e)
    {
        if (ContextOf<SessionItem>(sender) is SessionItem item)
        {
            this.viewModel.ExportSession(item);
        }
    }

    private void OnPinSession(object sender, RoutedEventArgs e)
    {
        if (ContextOf<SessionItem>(sender) is SessionItem item)
        {
            this.viewModel.TogglePin(item);
        }
    }

    private void OnDeleteAllSessions(object sender, RoutedEventArgs e)
    {
        int count = this.viewModel.Sessions.Count(s => !s.IsPinned);
        if (count == 0)
        {
            this.viewModel.ShowStatus("Every chat shown is pinned. Unpin a chat to delete it.");
            return;
        }

        string chats = count == 1 ? "chat" : "chats";
        string kept = this.viewModel.Sessions.Any(s => s.IsPinned) ? " Pinned chats are kept." : string.Empty;
        string question = this.viewModel.IsSessionListFiltered
            ? $"Delete the {count} {chats} shown? Chats hidden by the search or filter are kept.{kept} This can't be undone."
            : $"Delete all {count} saved {chats}?{kept} This can't be undone.";
        if (MessageBox.Show(question, "Claude", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            this.viewModel.DeleteShownSessions();
        }
    }

    // ===== Composer chips =====

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (ContextOf<Attachment>(sender) is Attachment attachment)
        {
            this.viewModel.RemoveAttachment(attachment);
        }
    }

    // ===== Cards =====

    private void OnSubmitAnswers(object sender, RoutedEventArgs e) => this.viewModel.SubmitAnswers();

    private void OnSkipQuestions(object sender, RoutedEventArgs e) => this.viewModel.SkipQuestions();

    private void OnAllow(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.Allow);

    private void OnAllowAll(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.AllowAll);

    private void OnDeny(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.Deny);
}
