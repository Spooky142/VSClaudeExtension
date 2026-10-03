using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeForVS.Agent;
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
        this.Loaded += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (this.viewModel.RepoPath.Length == 0)
            {
                this.UseSolutionRepo();
            }

            this.PromptBox.Focus();
        };
    }

    private void UseSolutionRepo()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.viewModel.SetRepoFromDirectory(this.package.GetSolutionDirectory());
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

    private void OnFromSolution(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.UseSolutionRepo();
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        this.viewModel.SaveApiKey(this.ApiKeyBox.Password);
        this.ApiKeyBox.Clear();
    }

    private void OnChangeKey(object sender, RoutedEventArgs e) => this.viewModel.NeedsApiKey = true;

    private void OnAllow(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.Allow);

    private void OnAllowAll(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.AllowAll);

    private void OnDeny(object sender, RoutedEventArgs e) => this.viewModel.Resolve(Approval.Deny);
}
