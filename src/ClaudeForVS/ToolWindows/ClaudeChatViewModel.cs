using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private ClaudeAgent? agent;
    private CancellationTokenSource? runCts;
    private TaskCompletionSource<Approval>? pendingApproval;
    private ChatEntry? currentAssistant;
    private ChatEntry? currentThinking;

    private string repoPath = string.Empty;
    private string status = string.Empty;
    private string approvalTitle = string.Empty;
    private string approvalDetail = string.Empty;
    private string? allowAllLabel;
    private bool isBusy;
    private bool needsApiKey;
    private bool hasPendingApproval;

    public ClaudeChatViewModel()
    {
        this.needsApiKey = ApiKeyStore.Load() is null;
        this.status = GitRunner.GitPath is null ? "git.exe not found - git tools will fail." : string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user sends a prompt, so the view can jump back to the bottom.</summary>
    public event EventHandler? PromptSent;

    public ObservableCollection<ChatEntry> Messages { get; } = [];

    public string RepoPath { get => this.repoPath; set => this.Set(ref this.repoPath, value); }

    public string Status { get => this.status; private set => this.Set(ref this.status, value); }

    public string ApprovalTitle { get => this.approvalTitle; private set => this.Set(ref this.approvalTitle, value); }

    public string ApprovalDetail
    {
        get => this.approvalDetail;
        private set
        {
            this.Set(ref this.approvalDetail, value);
            this.OnPropertyChanged(nameof(this.HasApprovalDetail));
        }
    }

    public bool HasApprovalDetail => this.approvalDetail.Length > 0;

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
        set
        {
            this.Set(ref this.needsApiKey, value);
            this.OnPropertyChanged(nameof(this.HasApiKey));
        }
    }

    public bool HasApiKey => !this.needsApiKey;

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

    public void SaveApiKey(string key)
    {
        key = key.Trim();
        if (key.Length == 0)
        {
            this.Status = "Enter an API key first.";
            return;
        }

        ApiKeyStore.Save(key);
        this.NeedsApiKey = false;
        this.agent = null; // pick up the new key
        this.Status = "API key saved (encrypted for your Windows account).";
    }

    public void NewChat()
    {
        this.Stop();
        this.agent = null;
        this.Messages.Clear();
        this.Status = string.Empty;
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

        string? apiKey = ApiKeyStore.Load();
        if (apiKey is null)
        {
            this.NeedsApiKey = true;
            this.Status = "Add your Anthropic API key to start.";
            return false;
        }

        string? root = GitRunner.FindRepoRoot(this.RepoPath.Trim());
        if (root is null)
        {
            this.Status = "Set the repository path to a folder inside a cloned git repository.";
            return false;
        }

        if (this.agent is null || !string.Equals(this.agent.RepoRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            if (this.agent != null)
            {
                this.Messages.Add(new ChatEntry(ChatEntryKind.Tool, $"New conversation for {root}"));
            }

            this.agent = new ClaudeAgent(apiKey, root);
            this.RepoPath = root;
        }

        this.Messages.Add(new ChatEntry(ChatEntryKind.User, "You", text));
        this.PromptSent?.Invoke(this, EventArgs.Empty);
        run = this.RunAsync(this.agent, text);
        return true;
    }

    private async Task RunAsync(ClaudeAgent runAgent, string text)
    {
        this.IsBusy = true;
        using var cts = new CancellationTokenSource();
        this.runCts = cts;
        try
        {
            await runAgent.SendAsync(text, this, cts.Token);
        }
        catch (OperationCanceledException)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Stopped", "Cancelled by you."));
        }
        catch (AnthropicUnauthorizedException)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Error", "The API key was rejected. Use \"Change API key\" to enter a new one."));
            this.agent = null;
        }
        catch (Exception ex)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Error", ex.Message));
        }
        finally
        {
            this.currentAssistant = null;
            this.currentThinking = null;
            this.runCts = null;
            this.IsBusy = false;
            this.HasPendingApproval = false;
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

    void IChatSink.ToolActivity(string title, string detail)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        this.Messages.Add(new ChatEntry(ChatEntryKind.Tool, title, detail: detail.Length > 8000 ? detail.Substring(0, 8000) + "\n…" : detail));
    }

    void IChatSink.SetStatus(string? status) => this.Status = status ?? string.Empty;

    async Task<Approval> IChatSink.RequestApprovalAsync(string title, string detail, string? allowAllLabel, CancellationToken cancellationToken)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        var tcs = new TaskCompletionSource<Approval>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingApproval = tcs;
        this.ApprovalTitle = title;
        this.ApprovalDetail = detail;
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

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        this.OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
