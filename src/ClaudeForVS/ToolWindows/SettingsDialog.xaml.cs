using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.ToolWindows;

/// <summary>One entry in the always-allowed list: the saved rule, and how it's shown (tools and trusted hooks are kept in the same list as commands).</summary>
internal sealed record AllowedRule(string Rule, string Display)
{
    public static string Label(string rule) =>
        rule.StartsWith("hooks:", StringComparison.Ordinal) ? "Run this repository's hooks (trusted until they change)"
        : rule == "build_solution" ? "Build the solution in Visual Studio"
        : rule == "run_tests" ? "Run tests"
        : rule == PackageTools.RuleKey ? "Add, change and remove NuGet packages"
        : rule.StartsWith("mcp__", StringComparison.Ordinal) ? "MCP tool: " + rule.Substring(5).Replace("__", " · ")
        : rule;
}

/// <summary>Engine, model, effort, repository and API key. Read the result properties after ShowModal() returns true.</summary>
public partial class SettingsDialog : DialogWindow
{
    private readonly AgentSettings current;
    private readonly bool hasConversation;
    private readonly Func<string?> getSolutionDirectory;
    private readonly Func<IntPtr, string?, string?> browseForFolder;
    private ClaudeCodeStatus? codeStatus;
    private string? lastError;
    private bool busy;

    /// <summary>The install/sign-in/status step in progress, if any.</summary>
    private Microsoft.VisualStudio.Threading.JoinableTask? setupTask;

    /// <param name="getSolutionDirectory">Returns the open solution's or folder's directory, or null.</param>
    /// <param name="browseForFolder">Shows a folder picker owned by the given window, starting in the given folder.</param>
    internal SettingsDialog(
        AgentSettings settings,
        string repoPath,
        bool hasApiKey,
        bool hasConversation,
        ClaudeCodeStatus? codeStatus,
        Func<string?> getSolutionDirectory,
        Func<IntPtr, string?, string?> browseForFolder)
    {
        this.current = settings;
        this.hasConversation = hasConversation;
        this.codeStatus = codeStatus;
        this.getSolutionDirectory = getSolutionDirectory;
        this.browseForFolder = browseForFolder;
        this.InitializeComponent();

        (settings.Engine == ChatEngineKind.ClaudeCode ? this.CodeEngineRadio : this.ApiEngineRadio).IsChecked = true;
        this.RepoBox.Text = repoPath;
        this.RunHooksBox.IsChecked = settings.RunHooks;
        this.FormatChangedBox.IsChecked = settings.FormatChangedFiles;
        this.NotificationsBox.IsChecked = settings.ShowNotifications;
        this.ExtensionUpdatesBox.IsChecked = settings.CheckForExtensionUpdates;
        this.ExtensionUpdatesHint.Text =
            $"Whenever a chat window is shown (at most every 15 minutes, and daily while Visual Studio stays open), looks for a newer release on GitHub ({ExtensionUpdates.Repository}) and offers to install it. You have {ExtensionUpdates.InstalledVersion}.";
        this.NewestModelsBox.IsChecked = settings.UseNewestApiModels;
        this.SpendLimitBox.Text = settings.SpendLimitUsd > 0 ? settings.SpendLimitUsd.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
        this.CompactAtBox.Text = settings.CompactAtTokens > 0 ? (settings.CompactAtTokens / 1000).ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
        this.UpdateRepoHint();
        this.UpdateAllowedCommands();
        this.ApiKeyHint.Text = hasApiKey
            ? "✓ A key is saved. Leave this empty to keep it, or enter a new one to replace it."
            : "Get a key at console.anthropic.com. It's stored encrypted for your Windows account.";
        this.UpdateChangeNote();
        this.UpdateEngineUi();

        if (codeStatus is null)
        {
            this.RefreshStatusInBackground();
        }
    }

    internal AgentSettings Result { get; private set; } = new();

    internal string ResultRepoPath { get; private set; } = string.Empty;

    /// <summary>A newly entered API key, or null to keep the saved one.</summary>
    internal string? ResultApiKey { get; private set; }

    private ChatEngineKind SelectedEngine => this.CodeEngineRadio.IsChecked == true ? ChatEngineKind.ClaudeCode : ChatEngineKind.Api;

    /// <summary>Warns that saving starts a new chat: another engine, or another version of the model on the Claude API.</summary>
    private void UpdateChangeNote()
    {
        // Radio buttons raise Checked during InitializeComponent, before every named element exists.
        if (this.ModelChangeNote is null)
        {
            return;
        }

        bool restarts = this.SelectedEngine != this.current.Engine
            || (this.SelectedEngine == ChatEngineKind.Api && (this.NewestModelsBox?.IsChecked == true) != this.current.UseNewestApiModels
                && ModelCatalog.NewerApiModel(this.current.Model) != null);
        this.ModelChangeNote.Visibility = this.hasConversation && restarts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEngineUi()
    {
        if (this.CodePanel is null || this.ApiKeySection is null || this.ApiOptionsSection is null)
        {
            return;
        }

        bool code = this.SelectedEngine == ChatEngineKind.ClaudeCode;
        this.CodePanel.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        this.ApiKeySection.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
        this.ApiOptionsSection.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
        this.UpdateNewestModelsHint();

        ClaudeCodeStatus? status = this.codeStatus;
        bool ready = status is { Installed: true, SignedIn: true };
        if (!this.busy)
        {
            this.CodeStatusText.Text = this.lastError ?? status?.Summary ?? "Checking Claude Code…";
        }

        this.CodeStatusIcon.Text = ready ? "" : ""; // checkmark / warning
        this.CodeStatusIcon.Foreground = (Brush)this.FindResource(ready ? "Accent" : "Amber");

        this.InstallButton.Visibility = status is { Installed: false } ? Visibility.Visible : Visibility.Collapsed;
        this.SignInButton.Visibility = status is { Installed: true } ? Visibility.Visible : Visibility.Collapsed;
        this.SignInButton.Content = status is { SignedIn: true } ? "Sign in again" : "Sign in";
        this.SignInButton.Style = (Style)this.FindResource(status is { SignedIn: true } ? "OutlineButton" : "PrimaryButton");
        this.SignInButton.Margin = new Thickness(0, 0, 6, 0);
        this.InstallButton.IsEnabled = this.SignInButton.IsEnabled = this.RefreshButton.IsEnabled = !this.busy;
    }

    private void UpdateNewestModelsHint()
    {
        if (this.NewestModelsHint is null)
        {
            return;
        }

        IEnumerable<string> newer = new[] { ClaudeModel.Opus, ClaudeModel.Sonnet, ClaudeModel.Haiku }
            .Select(ModelCatalog.NewerApiModel).OfType<ModelVersion>().Select(m => m.Name);
        string built = $"This extension was built for {AgentSettings.ModelName(ClaudeModel.Opus, ChatEngineKind.Api)}, {AgentSettings.ModelName(ClaudeModel.Sonnet, ChatEngineKind.Api)} and {AgentSettings.ModelName(ClaudeModel.Haiku, ChatEngineKind.Api)}. ";
        this.NewestModelsHint.Text = built + (newer.Any()
            ? $"Newer on the Claude API: {string.Join(", ", newer)}. A new version can work differently; if requests start failing, turn this off. Cost estimates use the built-in versions' prices."
            : "When the Claude API lists a newer version, chats use it. Claude Code always uses its newest versions.");
    }

    private void OnNewestModelsChanged(object sender, RoutedEventArgs e) => this.UpdateChangeNote();

    private void OnEngineChanged(object sender, RoutedEventArgs e)
    {
        this.UpdateChangeNote();
        this.UpdateEngineUi();
    }

    private void OnInstallClaudeCode(object sender, RoutedEventArgs e) =>
        this.RunBusy("Installing… follow the PowerShell window that opened.", ClaudeCodeCli.InstallAsync);

    private void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (this.codeStatus?.ExecutablePath is string exe)
        {
            this.RunBusy("Finish signing in in your browser and the console window that opened.", () => ClaudeCodeCli.SignInAsync(exe));
        }
    }

    private void OnRefreshStatus(object sender, RoutedEventArgs e) => this.RunBusy("Checking Claude Code…", () => Task.CompletedTask);

    private void RefreshStatusInBackground() => this.RunBusy("Checking Claude Code…", () => Task.CompletedTask);

    /// <summary>Runs an install/sign-in step, then re-checks Claude Code's status.</summary>
    private void RunBusy(string message, Func<Task> step)
    {
        this.setupTask = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            this.busy = true;
            this.lastError = null;
            this.CodeStatusText.Text = message;
            this.UpdateEngineUi();
            try
            {
                await step();
                ClaudeCodeStatus status = await ClaudeCodeCli.GetStatusAsync(CancellationToken.None);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                this.codeStatus = status;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                this.lastError = "Couldn't run Claude Code: " + ex.Message;
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                this.busy = false;
                this.UpdateEngineUi();
            }
        });
        this.setupTask.FileAndForget("ClaudeForVS/ClaudeCodeSetup");
    }

    private void OnUseSolution(object sender, RoutedEventArgs e)
    {
        if (this.getSolutionDirectory() is string dir)
        {
            this.RepoBox.Text = Git.GitRunner.FindRepoRoot(dir) ?? dir;
        }
        else
        {
            this.ShowRepoHint(false, "No solution or folder is open in Visual Studio.");
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        string current = this.RepoBox.Text.Trim();
        string? start = System.IO.Directory.Exists(current) ? current : null;
        IntPtr owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (this.browseForFolder(owner, start) is string chosen)
        {
            this.RepoBox.Text = Git.GitRunner.FindRepoRoot(chosen) ?? chosen;
        }
    }

    private void OnRepoChanged(object sender, TextChangedEventArgs e)
    {
        this.UpdateRepoHint();
        this.UpdateAllowedCommands();
    }

    /// <summary>Lists the commands remembered with "Always allow" for the repository in the box.</summary>
    private void UpdateAllowedCommands()
    {
        if (this.AllowedCommandsList is null || this.AllowedCommandsHint is null)
        {
            return;
        }

        string? root = Git.GitRunner.FindRepoRoot(this.RepoBox.Text.Trim());
        IReadOnlyList<string> rules = root is null ? [] : CommandRules.List(root);
        this.AllowedCommandsList.ItemsSource = rules.Select(r => new AllowedRule(r, AllowedRule.Label(r))).ToList();
        this.AllowedCommandsHint.Text = root is null
            ? "Choose a repository to see the commands allowed in it."
            : rules.Count == 0
                ? "None yet. Choose \"Always allow\" when approving a command to run it here without asking."
                : "These run in this repository without asking, with any arguments. Remove one to be asked again.";
    }

    private void OnRemoveAllowedCommand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AllowedRule allowed && Git.GitRunner.FindRepoRoot(this.RepoBox.Text.Trim()) is string root)
        {
            CommandRules.Remove(root, allowed.Rule);
            this.UpdateAllowedCommands();
        }
    }

    /// <summary>The open solution's repository root, if a solution is open and inside a git repository.</summary>
    private string? SolutionRepoRoot() => Git.GitRunner.FindRepoRoot(this.getSolutionDirectory());

    private void UpdateRepoHint()
    {
        if (this.RepoHint is null)
        {
            return;
        }

        string text = this.RepoBox.Text.Trim();
        if (text.Length == 0)
        {
            this.ShowRepoHint(false, "Enter a folder inside a cloned git repository, or use the open solution.");
            return;
        }

        if (!System.IO.Directory.Exists(text))
        {
            this.ShowRepoHint(false, "Folder not found.");
            return;
        }

        if (Git.GitRunner.FindRepoRoot(text) is not string root)
        {
            this.ShowRepoHint(false, "Not inside a git repository.");
            return;
        }

        string name = System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));
        string rootNote = string.Equals(root.TrimEnd('\\'), text.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? string.Empty : $" (root {root})";
        string follow = string.Equals(root, this.SolutionRepoRoot(), StringComparison.OrdinalIgnoreCase)
            ? "Follows the open solution."
            : "Used until you next open a solution or folder in a git repository.";
        this.ShowRepoHint(true, $"Git repository: {name}{rootNote}. {follow}");
    }

    private void ShowRepoHint(bool ok, string message)
    {
        this.RepoHint.Text = message;
        this.RepoHintIcon.Text = ok ? "" : ""; // checkmark / warning
        this.RepoHintIcon.Foreground = (Brush)this.FindResource(ok ? "Accent" : "Amber");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // A path that is the open solution's repository (or empty) follows the solution; anything else is pinned.
        string text = this.RepoBox.Text.Trim();
        string? root = Git.GitRunner.FindRepoRoot(text);
        bool follows = text.Length == 0 || (root != null && string.Equals(root, this.SolutionRepoRoot(), StringComparison.OrdinalIgnoreCase));

        // Model, effort, mode and the subagents' are set from the chips above the chat, so they're kept as they are.
        this.Result = this.current with
        {
            Engine = this.SelectedEngine,
            PinnedRepoPath = follows ? null : root ?? text,
            RunHooks = this.RunHooksBox.IsChecked == true,
            FormatChangedFiles = this.FormatChangedBox.IsChecked == true,
            ShowNotifications = this.NotificationsBox.IsChecked == true,
            CheckForExtensionUpdates = this.ExtensionUpdatesBox.IsChecked == true,
            UseNewestApiModels = this.NewestModelsBox.IsChecked == true,
            SpendLimitUsd = double.TryParse(this.SpendLimitBox.Text.Trim().TrimStart('$'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double limit) && limit > 0 ? limit : 0,
            CompactAtTokens = ParseCompactAt(this.CompactAtBox.Text),
        };
        this.ResultRepoPath = text.Length == 0 ? this.SolutionRepoRoot() ?? string.Empty : root ?? text;
        this.ResultApiKey = this.ApiKeyBox.Password.Trim() is { Length: > 0 } key ? key : null;
        this.DialogResult = true;
    }

    /// <summary>Thousands of tokens to summarize at: empty means only when the window is nearly full, and anything unreadable keeps the default.</summary>
    private static long ParseCompactAt(string text)
    {
        text = text.Trim().TrimEnd('k', 'K');
        if (text.Length == 0)
        {
            return 0;
        }

        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double thousands) && thousands > 0
            ? (long)(Math.Max(20, Math.Min(900, thousands)) * 1000)
            : AgentSettings.DefaultCompactAtTokens;
    }
}
