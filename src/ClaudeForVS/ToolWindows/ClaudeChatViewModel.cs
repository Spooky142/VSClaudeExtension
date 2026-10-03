using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

namespace ClaudeForVS.ToolWindows;

/// <summary>
/// State behind the chat window. Everything here runs on the UI thread: the agent is started from
/// the UI thread and its awaits resume there, so sink callbacks can touch bound properties directly.
/// </summary>
internal sealed class ClaudeChatViewModel : INotifyPropertyChanged, IChatSink
{
    private const int MaxDetailChars = 8000;

    private IChatEngine? engine;
    private CancellationTokenSource? runCts;
    private TaskCompletionSource<Approval>? pendingApproval;
    private ChatEntry? currentAssistant;
    private ChatEntry? currentThinking;

    private string repoPath = string.Empty;
    private string status = string.Empty;
    private string approvalTitle = string.Empty;
    private IReadOnlyList<DiffLine> approvalLines = [];
    private string? allowAllLabel;
    private bool isBusy;
    private bool needsApiKey;
    private bool hasPendingApproval;
    private AgentSettings settings;
    private ClaudeCodeStatus? codeStatus;
    private string setupMessage = string.Empty;

    public ClaudeChatViewModel()
    {
        this.settings = AgentSettings.Load();
        this.needsApiKey = ApiKeyStore.Load() is null;
        this.status = GitRunner.GitPath is null ? "git.exe not found - git tools will fail." : string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user sends a prompt, so the view can jump back to the bottom.</summary>
    public event EventHandler? PromptSent;

    public ObservableCollection<ChatEntry> Messages { get; } = [];

    public string RepoPath
    {
        get => this.repoPath;
        set
        {
            this.Set(ref this.repoPath, value);
            this.OnPropertyChanged(nameof(this.RepoName));
        }
    }

    /// <summary>Short name shown in the header chip.</summary>
    public string RepoName
    {
        get
        {
            string trimmed = this.repoPath.Trim().TrimEnd('\\', '/');
            return trimmed.Length == 0 ? "No repository" : Path.GetFileName(trimmed);
        }
    }

    public string Status { get => this.status; private set => this.Set(ref this.status, value); }

    public string ApprovalTitle { get => this.approvalTitle; private set => this.Set(ref this.approvalTitle, value); }

    public IReadOnlyList<DiffLine> ApprovalLines
    {
        get => this.approvalLines;
        private set
        {
            this.Set(ref this.approvalLines, value);
            this.OnPropertyChanged(nameof(this.HasApprovalDetail));
        }
    }

    public bool HasApprovalDetail => this.approvalLines.Count > 0;

    public string? AllowAllLabel
    {
        get => this.allowAllLabel;
        private set
        {
            this.Set(ref this.allowAllLabel, value);
            this.OnPropertyChanged(nameof(this.CanAllowAll));
        }
    }

    public bool CanAllowAll => this.allowAllLabel != null;

    public bool IsBusy
    {
        get => this.isBusy;
        private set
        {
            this.Set(ref this.isBusy, value);
            this.OnPropertyChanged(nameof(this.IsIdle));
        }
    }

    public bool IsIdle => !this.isBusy;

    public bool NeedsApiKey
    {
        get => this.needsApiKey;
        private set
        {
            this.Set(ref this.needsApiKey, value);
            this.OnPropertyChanged(nameof(this.HasApiKey));
            this.UpdateSetupMessage();
        }
    }

    public bool HasApiKey => !this.needsApiKey;

    public AgentSettings Settings
    {
        get => this.settings;
        private set
        {
            this.Set(ref this.settings, value);
            this.OnPropertyChanged(nameof(this.ModelSummary));
            this.UpdateSetupMessage();
        }
    }

    /// <summary>Header chip text, e.g. "Opus 5.5 · High" or "Opus 5.5 · High · Claude Code".</summary>
    public string ModelSummary =>
        $"{AgentSettings.ModelName(this.settings.Model)} · {AgentSettings.EffortName(this.settings.Effort)}"
        + (this.settings.Engine == ChatEngineKind.ClaudeCode ? " · Claude Code" : string.Empty);

    /// <summary>Last known Claude Code install/sign-in state, or null before the first check.</summary>
    public ClaudeCodeStatus? CodeStatus
    {
        get => this.codeStatus;
        private set
        {
            this.Set(ref this.codeStatus, value);
            this.UpdateSetupMessage();
        }
    }

    /// <summary>What still needs doing before the selected engine can be used, or empty when it's ready.</summary>
    public string SetupMessage { get => this.setupMessage; private set => this.Set(ref this.setupMessage, value); }

    public bool HasSetupMessage => this.setupMessage.Length > 0;

    /// <summary>Re-checks whether Claude Code is installed and signed in.</summary>
    public async Task RefreshClaudeCodeStatusAsync()
    {
        try
        {
            this.CodeStatus = await ClaudeCodeCli.GetStatusAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            this.CodeStatus = new ClaudeCodeStatus(null, null, false, null, null);
        }
    }

    private void UpdateSetupMessage()
    {
        this.SetupMessage = this.settings.Engine switch
        {
            ChatEngineKind.Api when this.needsApiKey => "Add your Anthropic API key to get started.",
            ChatEngineKind.ClaudeCode when this.codeStatus is { Installed: false } => "Install Claude Code to use your Pro or Max plan.",
            ChatEngineKind.ClaudeCode when this.codeStatus is { SignedIn: false } => "Sign in to Claude Code to use your Pro or Max plan.",
            _ => string.Empty,
        };
        this.OnPropertyChanged(nameof(this.HasSetupMessage));
    }

    /// <summary>True once the current conversation has history that a model switch would discard.</summary>
    public bool HasConversation => this.engine != null && this.Messages.Count > 0;

    public bool HasPendingApproval { get => this.hasPendingApproval; private set => this.Set(ref this.hasPendingApproval, value); }

    public void SetRepoFromDirectory(string? directory)
    {
        string? root = GitRunner.FindRepoRoot(directory);
        if (root != null)
        {
            this.RepoPath = root;
        }
        else if (directory != null)
        {
            this.RepoPath = directory;
            this.Status = "The open solution is not inside a git repository.";
        }
        else
        {
            this.Status = "No solution or folder is open.";
        }
    }

    /// <summary>Applies the Settings dialog's result.</summary>
    public void ApplySettings(AgentSettings newSettings, string repoPath, string? newApiKey)
    {
        bool engineChanged = newSettings.Engine != this.settings.Engine;
        bool modelChanged = newSettings.Model != this.settings.Model;
        newSettings.Save();
        this.Settings = newSettings;
        this.RepoPath = repoPath;

        if (newApiKey != null)
        {
            ApiKeyStore.Save(newApiKey);
            this.NeedsApiKey = false;
        }

        if (this.engine is null)
        {
            this.Status = "Settings saved.";
            return;
        }

        // Each engine keeps its own history; a new model can't read the old model's thinking; a new key needs a new client.
        if (engineChanged || modelChanged || (newApiKey != null && this.engine.Kind == ChatEngineKind.Api))
        {
            this.Stop();
            bool hadHistory = this.Messages.Count > 0;
            this.DisposeEngine();
            if (hadHistory)
            {
                string via = newSettings.Engine == ChatEngineKind.ClaudeCode ? " · Claude Code" : string.Empty;
                this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"New chat · {AgentSettings.ModelName(newSettings.Model)}{via}"));
            }
        }
        else
        {
            this.engine.Effort = newSettings.Effort; // used from the next message on
        }

        this.Status = "Settings saved.";
    }

    public void NewChat()
    {
        this.Stop();
        this.DisposeEngine();
        this.Messages.Clear();
        this.Status = string.Empty;
    }

    private void DisposeEngine()
    {
        this.engine?.Dispose();
        this.engine = null;
    }

    public void Stop()
    {
        this.pendingApproval?.TrySetResult(Approval.Deny);
        this.runCts?.Cancel();
    }

    public void Resolve(Approval approval) => this.pendingApproval?.TrySetResult(approval);

    /// <summary>Sends a prompt. Returns false (and leaves the prompt box alone) if it couldn't be sent.</summary>
    public bool TrySend(string text, out Task run)
    {
        run = Task.CompletedTask;
        text = text.Trim();
        if (text.Length == 0 || this.IsBusy)
        {
            return false;
        }

        string? root = GitRunner.FindRepoRoot(this.RepoPath.Trim());
        if (root is null)
        {
            this.Status = "Set the repository in Settings to a folder inside a cloned git repository.";
            return false;
        }

        if (this.engine is null || !string.Equals(this.engine.RepoRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            IChatEngine? created = this.CreateEngine(root);
            if (created is null)
            {
                return false;
            }

            if (this.engine != null)
            {
                this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"New chat · {Path.GetFileName(root)}"));
            }

            this.DisposeEngine();
            this.engine = created;
            this.RepoPath = root;
        }

        this.Messages.Add(new ChatEntry(ChatEntryKind.User, "You", text));
        this.PromptSent?.Invoke(this, EventArgs.Empty);
        run = this.RunAsync(this.engine, text);
        return true;
    }

    /// <summary>Creates the engine the settings ask for, or sets a status message and returns null if it isn't ready.</summary>
    private IChatEngine? CreateEngine(string root)
    {
        if (this.settings.Engine == ChatEngineKind.ClaudeCode)
        {
            if (ClaudeCodeCli.FindExecutable() is not string exe)
            {
                this.CodeStatus = new ClaudeCodeStatus(null, null, false, null, null);
                this.Status = "Claude Code isn't installed. Install it from Settings.";
                return null;
            }

            if (this.codeStatus is { SignedIn: false })
            {
                this.Status = "Claude Code isn't signed in. Sign in from Settings.";
                return null;
            }

            return new ClaudeCodeEngine(exe, root, this.settings.Model, this.settings.Effort);
        }

        if (ApiKeyStore.Load() is not string apiKey)
        {
            this.NeedsApiKey = true;
            this.Status = "Add your Anthropic API key to start.";
            return null;
        }

        return new ClaudeAgent(apiKey, root, this.settings.Model, this.settings.Effort);
    }

    private async Task RunAsync(IChatEngine runEngine, string text)
    {
        this.IsBusy = true;
        using var cts = new CancellationTokenSource();
        this.runCts = cts;
        try
        {
            await runEngine.SendAsync(text, this, cts.Token);
        }
        catch (OperationCanceledException)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, "Stopped"));
        }
        catch (AnthropicUnauthorizedException)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "API key rejected", "Open settings and enter a valid Anthropic API key."));
            this.DisposeEngine();
        }
        catch (Exception ex)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Something went wrong", ex.Message));
            if (runEngine.Kind == ChatEngineKind.ClaudeCode)
            {
                await this.RefreshClaudeCodeStatusAsync(); // e.g. the sign-in expired
            }
        }
        finally
        {
            this.currentAssistant = null;
            this.currentThinking = null;
            this.runCts = null;
            this.IsBusy = false;
            this.HasPendingApproval = false;
            if (this.Status.StartsWith("Waiting", StringComparison.Ordinal) || this.Status.StartsWith("Running", StringComparison.Ordinal))
            {
                this.Status = string.Empty;
            }
        }
    }

    void IChatSink.AppendText(string text)
    {
        this.currentThinking = null;
        if (this.currentAssistant is null)
        {
            this.currentAssistant = new ChatEntry(ChatEntryKind.Assistant, "Claude");
            this.Messages.Add(this.currentAssistant);
        }

        this.currentAssistant.Append(text);
    }

    void IChatSink.AppendThinking(string text)
    {
        if (this.currentThinking is null)
        {
            this.currentAssistant = null;
            this.currentThinking = new ChatEntry(ChatEntryKind.Thinking, "Thinking");
            this.Messages.Add(this.currentThinking);
        }

        this.currentThinking.Append(text);
    }

    void IChatSink.ToolActivity(ActivityKind kind, string title, string detail)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        this.Messages.Add(new ChatEntry(ChatEntryKind.Tool, title, detail: Truncate(detail), activity: kind));
    }

    void IChatSink.SetStatus(string? status) => this.Status = status ?? string.Empty;

    async Task<Approval> IChatSink.RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        var tcs = new TaskCompletionSource<Approval>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingApproval = tcs;
        this.ApprovalTitle = title;
        this.ApprovalLines = DiffLine.Parse(Truncate(detail), kind);
        this.AllowAllLabel = allowAllLabel;
        this.HasPendingApproval = true;
        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                this.HasPendingApproval = false;
                this.pendingApproval = null;
            }
        }
    }

    private static string Truncate(string detail)
        => detail.Length > MaxDetailChars ? detail.Substring(0, MaxDetailChars) + "\n…" : detail;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        this.OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
