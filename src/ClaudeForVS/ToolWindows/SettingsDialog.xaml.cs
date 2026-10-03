using System;
using System.Windows;
using System.Windows.Controls;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeForVS.ToolWindows;

/// <summary>Model, effort, repository and API key. Read the result properties after ShowModal() returns true.</summary>
public partial class SettingsDialog : DialogWindow
{
    private readonly ClaudeModel currentModel;
    private readonly bool hasConversation;
    private readonly Func<string?> getSolutionDirectory;

    internal SettingsDialog(AgentSettings settings, string repoPath, bool hasApiKey, bool hasConversation, Func<string?> getSolutionDirectory)
    {
        this.currentModel = settings.Model;
        this.hasConversation = hasConversation;
        this.getSolutionDirectory = getSolutionDirectory;
        this.InitializeComponent();

        this.ModelRadio(settings.Model).IsChecked = true;
        this.EffortRadio(settings.Effort).IsChecked = true;
        this.RepoBox.Text = repoPath;
        this.ApiKeyHint.Text = hasApiKey
            ? "✓ A key is saved. Leave this empty to keep it, or enter a new one to replace it."
            : "Get a key at console.anthropic.com. It's stored encrypted for your Windows account.";
        this.UpdateHints();
    }

    internal AgentSettings Result { get; private set; } = new();

    internal string ResultRepoPath { get; private set; } = string.Empty;

    /// <summary>A newly entered API key, or null to keep the saved one.</summary>
    internal string? ResultApiKey { get; private set; }

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

        this.ModelChangeNote.Visibility = this.hasConversation && this.SelectedModel != this.currentModel
            ? Visibility.Visible
            : Visibility.Collapsed;

        this.EffortHint.Text = this.SelectedModel == ClaudeModel.Haiku
            ? AgentSettings.HaikuThinkingBudget(this.SelectedEffort) is int budget
                ? $"Haiku has no effort setting, so this sets its thinking budget: up to {budget:N0} tokens."
                : "Haiku has no effort setting, so Low turns thinking off for the fastest replies."
            : this.SelectedEffort switch
            {
                EffortLevel.Low => "Quickest and cheapest. Fine for simple questions and lookups.",
                EffortLevel.Medium => "Balanced speed and depth.",
                EffortLevel.High => "Thinks things through. A good default for coding.",
                EffortLevel.XHigh => "More thorough reasoning for tricky changes. Slower and uses more tokens.",
                _ => "Deepest reasoning when correctness matters most. Slowest and most expensive.",
            };
    }

    private void OnModelChanged(object sender, RoutedEventArgs e) => this.UpdateHints();

    private void OnEffortChanged(object sender, RoutedEventArgs e) => this.UpdateHints();

    private void OnUseSolution(object sender, RoutedEventArgs e)
    {
        if (this.getSolutionDirectory() is string dir)
        {
            this.RepoBox.Text = Git.GitRunner.FindRepoRoot(dir) ?? dir;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        this.Result = new AgentSettings { Model = this.SelectedModel, Effort = this.SelectedEffort };
        this.ResultRepoPath = this.RepoBox.Text.Trim();
        this.ResultApiKey = this.ApiKeyBox.Password.Trim() is { Length: > 0 } key ? key : null;
        this.DialogResult = true;
    }
}
