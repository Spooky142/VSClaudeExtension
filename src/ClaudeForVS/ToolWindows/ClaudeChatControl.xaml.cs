using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;
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

        // The pane lives as long as Visual Studio, so this subscription never needs removing.
        package.SolutionOpened += (_, directory) => this.viewModel.OnSolutionOpened(directory);

        // Keep the usage strip's "resets in" countdowns current.
        this.usageTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => this.viewModel.RefreshUsage(), this.Dispatcher);

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
            }

            this.PromptBox.Focus();
            if (this.viewModel.Settings.Engine == ChatEngineKind.ClaudeCode && this.viewModel.CodeStatus is null)
            {
                this.viewModel.RefreshClaudeCodeStatusAsync().FileAndForget("ClaudeForVS/CodeStatus");
            }
        };
    }

    private void SendPrompt()
    {
        if (this.viewModel.TrySend(this.PromptBox.Text, out Task run))
        {
            this.PromptBox.Clear();
            run.FileAndForget("ClaudeForVS/Send");
        }
    }

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            this.SendPrompt();
        }
    }

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

    private void OnSend(object sender, RoutedEventArgs e) => this.SendPrompt();

    private void OnStop(object sender, RoutedEventArgs e) => this.viewModel.Stop();

    private void OnNewChat(object sender, RoutedEventArgs e) => this.viewModel.NewChat();

    private void OnOpenSession(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SessionItem item)
        {
            this.viewModel.OpenSession(item);
            this.PromptBox.Focus();
        }
    }

    private void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SessionItem item)
        {
            this.viewModel.DeleteSession(item);
        }
    }

    private void OnDeleteAllSessions(object sender, RoutedEventArgs e)
    {
        int count = this.viewModel.Sessions.Count;
        if (count > 0 && MessageBox.Show(
                $"Delete all {count} saved {(count == 1 ? "chat" : "chats")}? This can't be undone.",
                "Claude", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            this.viewModel.DeleteAllSessions();
        }
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

    private void OnSubmitAnswers(object sender, RoutedEventArgs e) => this.viewModel.SubmitAnswers();

    private void OnSkipQuestions(object sender, RoutedEventArgs e) => this.viewModel.SkipQuestions();

    private void OnAllow(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.Allow);

    private void OnAllowAll(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.AllowAll);

    private void OnDeny(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.Deny);
}
