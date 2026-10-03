using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.ToolWindows;

/// <summary>Engine, model, effort, repository and API key. Read the result properties after ShowModal() returns true.</summary>
public partial class SettingsDialog : DialogWindow
{
    private readonly AgentSettings current;
    private readonly bool hasConversation;
    private readonly Func<string?> getSolutionDirectory;
    private ClaudeCodeStatus? codeStatus;
    private string? lastError;
    private bool busy;

    /// <summary>The install/sign-in/status step in progress, if any.</summary>
    private Microsoft.VisualStudio.Threading.JoinableTask? setupTask;

    internal SettingsDialog(AgentSettings settings, string repoPath, bool hasApiKey, bool hasConversation, ClaudeCodeStatus? codeStatus, Func<string?> getSolutionDirectory)
    {
        this.current = settings;
        this.hasConversation = hasConversation;
        this.codeStatus = codeStatus;
        this.getSolutionDirectory = getSolutionDirectory;
        this.InitializeComponent();

        (settings.Engine == ChatEngineKind.ClaudeCode ? this.CodeEngineRadio : this.ApiEngineRadio).IsChecked = true;
        this.ModelRadio(settings.Model).IsChecked = true;
        this.EffortRadio(settings.Effort).IsChecked = true;
        this.RepoBox.Text = repoPath;
        this.ApiKeyHint.Text = hasApiKey
            ? "✓ A key is saved. Leave this empty to keep it, or enter a new one to replace it."
            : "Get a key at console.anthropic.com. It's stored encrypted for your Windows account.";
        this.UpdateHints();
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

    private ClaudeModel SelectedModel =>
        this.SonnetRadio.IsChecked == true ? ClaudeModel.Sonnet :
        this.HaikuRadio.IsChecked == true ? ClaudeModel.Haiku :
        ClaudeModel.Opus;

    private EffortLevel SelectedEffort =>
        this.EffortLow.IsChecked == true ? EffortLevel.Low :
        this.EffortMedium.IsChecked == true ? EffortLevel.Medium :
        this.EffortXHigh.IsChecked == true ? EffortLevel.XHigh :
        this.EffortMax.IsChecked == true ? EffortLevel.Max :
        EffortLevel.High;

    private RadioButton ModelRadio(ClaudeModel model) => model switch
    {
        ClaudeModel.Sonnet => this.SonnetRadio,
        ClaudeModel.Haiku => this.HaikuRadio,
        _ => this.OpusRadio,
    };

    private RadioButton EffortRadio(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => this.EffortLow,
        EffortLevel.Medium => this.EffortMedium,
        EffortLevel.XHigh => this.EffortXHigh,
        EffortLevel.Max => this.EffortMax,
        _ => this.EffortHigh,
    };

    private void UpdateHints()
    {
        // Radio buttons raise Checked during InitializeComponent, before every named element exists.
        if (this.EffortHint is null || this.ModelChangeNote is null)
        {
            return;
        }

        bool restarts = this.SelectedModel != this.current.Model || this.SelectedEngine != this.current.Engine;
        this.ModelChangeNote.Visibility = this.hasConversation && restarts ? Visibility.Visible : Visibility.Collapsed;

        if (this.SelectedModel == ClaudeModel.Haiku)
        {
            this.EffortHint.Text = this.SelectedEngine == ChatEngineKind.ClaudeCode
                ? "Haiku has no effort setting, so Claude Code uses its own default for it."
                : AgentSettings.HaikuThinkingBudget(this.SelectedEffort) is int budget
                    ? $"Haiku has no effort setting, so this sets its thinking budget: up to {budget:N0} tokens."
                    : "Haiku has no effort setting, so Low turns thinking off for the fastest replies.";
            return;
        }

        this.EffortHint.Text = this.SelectedEffort switch
        {
            EffortLevel.Low => "Quickest and cheapest. Fine for simple questions and lookups.",
            EffortLevel.Medium => "Balanced speed and depth.",
            EffortLevel.High => "Thinks things through. A good default for coding.",
            EffortLevel.XHigh => "More thorough reasoning for tricky changes. Slower and uses more tokens.",
            _ => "Deepest reasoning when correctness matters most. Slowest and most expensive.",
        };
    }

    private void UpdateEngineUi()
    {
        if (this.CodePanel is null || this.ApiKeySection is null)
        {
            return;
        }

        bool code = this.SelectedEngine == ChatEngineKind.ClaudeCode;
        this.CodePanel.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        this.ApiKeySection.Visibility = code ? Visibility.Collapsed : Visibility.Visible;

        // Per-token prices only apply to the API; Claude Code usage comes out of the plan.
        Visibility prices = code ? Visibility.Collapsed : Visibility.Visible;
        this.OpusPrice.Visibility = this.SonnetPrice.Visibility = this.HaikuPrice.Visibility = prices;

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

    private void OnEngineChanged(object sender, RoutedEventArgs e)
    {
        this.UpdateHints();
        this.UpdateEngineUi();
    }

    private void OnModelChanged(object sender, RoutedEventArgs e) => this.UpdateHints();

    private void OnEffortChanged(object sender, RoutedEventArgs e) => this.UpdateHints();

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
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        this.Result = new AgentSettings { Engine = this.SelectedEngine, Model = this.SelectedModel, Effort = this.SelectedEffort };
        this.ResultRepoPath = this.RepoBox.Text.Trim();
        this.ResultApiKey = this.ApiKeyBox.Password.Trim() is { Length: > 0 } key ? key : null;
        this.DialogResult = true;
    }
}
