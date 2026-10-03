using System.Runtime.Serialization;
using Anthropic.Exceptions;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace ClaudeForVS.ToolWindows;

[DataContract]
internal class ClaudeChatViewModel : NotifyPropertyChangedObject, IChatSink
{
    private readonly VisualStudioExtensibility extensibility;
    private ClaudeAgent? agent;
    private CancellationTokenSource? runCts;
    private TaskCompletionSource<bool>? pendingApproval;
    private ChatEntry? currentAssistant;
    private ChatEntry? currentThinking;

    private string prompt = string.Empty;
    private string repoPath = string.Empty;
    private string status = string.Empty;
    private string apiKeyInput = string.Empty;
    private string approvalText = string.Empty;
    private bool isBusy;
    private bool needsApiKey;
    private bool hasPendingApproval;

    public ClaudeChatViewModel(VisualStudioExtensibility extensibility)
    {
        this.extensibility = extensibility;
        this.needsApiKey = ApiKeyStore.Load() is null;

        this.SendCommand = new AsyncCommand((_, ct) => this.SendAsync());
        this.StopCommand = new AsyncCommand(async (_, ct) => { this.pendingApproval?.TrySetResult(false); if (this.runCts is { } cts) { await cts.CancelAsync(); } });
        this.NewChatCommand = new AsyncCommand((_, ct) => { this.ResetConversation(); return Task.CompletedTask; });
        this.UseSolutionRepoCommand = new AsyncCommand((_, ct) => this.RefreshRepoFromSolutionAsync(ct));
        this.SaveApiKeyCommand = new AsyncCommand((_, ct) => this.SaveApiKeyAsync());
        this.ChangeApiKeyCommand = new AsyncCommand((_, ct) => { this.NeedsApiKey = true; return Task.CompletedTask; });
        this.ApproveCommand = new AsyncCommand((_, ct) => { this.pendingApproval?.TrySetResult(true); return Task.CompletedTask; });
        this.DenyCommand = new AsyncCommand((_, ct) => { this.pendingApproval?.TrySetResult(false); return Task.CompletedTask; });

        this.Status = GitRunner.GitPath is null ? "git.exe not found - git tools will fail." : string.Empty;
    }

    [DataMember] public ObservableList<ChatEntry> Messages { get; } = [];

    [DataMember] public IAsyncCommand SendCommand { get; }
    [DataMember] public IAsyncCommand StopCommand { get; }
    [DataMember] public IAsyncCommand NewChatCommand { get; }
    [DataMember] public IAsyncCommand UseSolutionRepoCommand { get; }
    [DataMember] public IAsyncCommand SaveApiKeyCommand { get; }
    [DataMember] public IAsyncCommand ChangeApiKeyCommand { get; }
    [DataMember] public IAsyncCommand ApproveCommand { get; }
    [DataMember] public IAsyncCommand DenyCommand { get; }

    [DataMember]
    public string Prompt { get => this.prompt; set => this.SetProperty(ref this.prompt, value); }

    [DataMember]
    public string RepoPath { get => this.repoPath; set => this.SetProperty(ref this.repoPath, value); }

    [DataMember]
    public string Status { get => this.status; set => this.SetProperty(ref this.status, value); }

    [DataMember]
    public string ApiKeyInput { get => this.apiKeyInput; set => this.SetProperty(ref this.apiKeyInput, value); }

    [DataMember]
    public string ApprovalText { get => this.approvalText; set => this.SetProperty(ref this.approvalText, value); }

    [DataMember]
    public bool IsBusy
    {
        get => this.isBusy;
        set
        {
            this.SetProperty(ref this.isBusy, value);
            this.RaiseNotifyPropertyChangedEvent(nameof(this.IsIdle));
        }
    }

    [DataMember] public bool IsIdle => !this.isBusy;

    [DataMember]
    public bool NeedsApiKey
    {
        get => this.needsApiKey;
        set
        {
            this.SetProperty(ref this.needsApiKey, value);
            this.RaiseNotifyPropertyChangedEvent(nameof(this.HasApiKey));
        }
    }

    [DataMember] public bool HasApiKey => !this.needsApiKey;

    [DataMember]
    public bool HasPendingApproval { get => this.hasPendingApproval; set => this.SetProperty(ref this.hasPendingApproval, value); }

    public async Task RefreshRepoFromSolutionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var solutions = await this.extensibility.Workspaces().QuerySolutionAsync(
                solution => solution.With(s => s.Directory),
                cancellationToken);
            string? dir = solutions.FirstOrDefault()?.Directory;
            string? root = GitRunner.FindRepoRoot(dir);
            if (root != null)
            {
                this.RepoPath = root;
            }
            else if (dir != null)
            {
                this.RepoPath = dir;
                this.Status = "The open solution is not inside a git repository.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Status = "Could not read the open solution: " + ex.Message;
        }
    }

    private Task SaveApiKeyAsync()
    {
        string key = this.ApiKeyInput.Trim();
        if (key.Length == 0)
        {
            this.Status = "Enter an API key first.";
            return Task.CompletedTask;
        }

        ApiKeyStore.Save(key);
        this.ApiKeyInput = string.Empty;
        this.NeedsApiKey = false;
        this.agent = null; // pick up the new key
        this.Status = "API key saved (encrypted for your Windows account).";
        return Task.CompletedTask;
    }

    private void ResetConversation()
    {
        this.runCts?.Cancel();
        this.agent = null;
        this.Messages.Clear();
        this.Status = string.Empty;
    }

    private async Task SendAsync()
    {
        string text = this.Prompt.Trim();
        if (text.Length == 0 || this.IsBusy)
        {
            return;
        }

        string? apiKey = ApiKeyStore.Load();
        if (apiKey is null)
        {
            this.NeedsApiKey = true;
            this.Status = "Add your Anthropic API key to start.";
            return;
        }

        string? root = GitRunner.FindRepoRoot(this.RepoPath.Trim());
        if (root is null)
        {
            this.Status = "Set the repository path to a folder inside a cloned git repository.";
            return;
        }

        if (this.agent is null || !string.Equals(this.agent.RepoRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            if (this.agent != null)
            {
                this.Messages.Add(new ChatEntry("tool", "New conversation", $"Repository changed to {root}"));
            }

            this.agent = new ClaudeAgent(apiKey, root);
            this.RepoPath = root;
        }

        this.Prompt = string.Empty;
        this.Messages.Add(new ChatEntry("user", "You", text));
        this.IsBusy = true;
        this.runCts = new CancellationTokenSource();
        try
        {
            await this.agent.SendAsync(text, this, this.runCts.Token);
        }
        catch (OperationCanceledException)
        {
            this.Messages.Add(new ChatEntry("error", "Stopped", "Cancelled by you."));
        }
        catch (AnthropicUnauthorizedException)
        {
            this.Messages.Add(new ChatEntry("error", "Error", "The API key was rejected. Use \"Change API key\" to enter a new one."));
            this.agent = null;
        }
        catch (Exception ex)
        {
            this.Messages.Add(new ChatEntry("error", "Error", ex.Message));
        }
        finally
        {
            this.EndStreamingEntries();
            this.runCts.Dispose();
            this.runCts = null;
            this.IsBusy = false;
            this.HasPendingApproval = false;
        }
    }

    private void EndStreamingEntries()
    {
        this.currentAssistant?.Flush();
        this.currentThinking?.Flush();
        this.currentAssistant = null;
        this.currentThinking = null;
    }

    void IChatSink.AppendText(string text)
    {
        this.currentThinking?.Flush();
        this.currentThinking = null;
        if (this.currentAssistant is null)
        {
            this.currentAssistant = new ChatEntry("assistant", "Claude");
            this.Messages.Add(this.currentAssistant);
        }

        this.currentAssistant.Append(text);
    }

    void IChatSink.AppendThinking(string text)
    {
        if (this.currentThinking is null)
        {
            this.currentAssistant?.Flush();
            this.currentAssistant = null;
            this.currentThinking = new ChatEntry("thinking", "Thinking");
            this.Messages.Add(this.currentThinking);
        }

        this.currentThinking.Append(text);
    }

    void IChatSink.ToolActivity(string title, string detail)
    {
        this.EndStreamingEntries();
        this.Messages.Add(new ChatEntry("tool", title) { Detail = detail.Length > 4000 ? detail[..4000] + "\nâ€¦" : detail });
    }

    void IChatSink.SetStatus(string? status) => this.Status = status ?? string.Empty;

    async Task<bool> IChatSink.RequestApprovalAsync(string description, CancellationToken cancellationToken)
    {
        this.EndStreamingEntries();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingApproval = tcs;
        this.ApprovalText = description;
        this.HasPendingApproval = true;
        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
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
}
