using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
internal sealed partial class ClaudeChatViewModel : INotifyPropertyChanged, IChatSink
{
    private const int MaxDetailChars = 8000;

    private IChatEngine? engine;
    private CancellationTokenSource? runCts;
    private TaskCompletionSource<Approval>? pendingApproval;
    private TaskCompletionSource<IReadOnlyDictionary<string, string>?>? pendingQuestions;
    private ChatEntry? currentAssistant;
    private ChatEntry? currentThinking;

    private string repoPath = string.Empty;
    private string status = string.Empty;
    private string approvalTitle = string.Empty;
    private IReadOnlyList<DiffLine> approvalLines = [];
    private FileChange? approvalChange;
    private string? allowAllLabel;
    private bool isBusy;
    private bool needsApiKey;
    private bool hasPendingApproval;
    private AgentSettings settings;
    private ClaudeCodeStatus? codeStatus;
    private string setupMessage = string.Empty;

    // Usage strip state. Plan windows persist across restarts; API totals cover this Visual Studio session.
    private readonly Dictionary<string, PlanLimit> planLimits = PlanUsageStore.Load();
    private TokenUsage sessionTokens;
    private double sessionCost;
    private ApiRateLimits? apiLimits;

    // Tokens in the current chat's context, as of the latest response; 0 until the chat's engine has answered once.
    private long contextTokens;

    // Tokens (and, for the API, estimated cost) used by the prompt currently running; shown as its footer.
    private TokenUsage promptTokens;
    private double promptCost;
    private bool promptHasCost;
    private string usageEmptyText = string.Empty;

    // Tokens and cost of the current chat since it was started or opened, for /cost.
    private TokenUsage chatTokens;
    private double chatCost;
    private bool chatHasCost;

    public ClaudeChatViewModel()
    {
        this.settings = AgentSettings.Load();
        this.needsApiKey = ApiKeyStore.Load() is null;
        this.status = GitRunner.GitPath is null ? "git.exe not found - git tools will fail." : string.Empty;

        // A repository chosen by hand is remembered; otherwise the view fills this in from the open solution.
        if (this.settings.PinnedRepoPath is string pinned && Directory.Exists(pinned))
        {
            this.repoPath = pinned;
        }

        this.Attachments.CollectionChanged += (_, _) => this.OnPropertyChanged(nameof(this.HasAttachments));
        this.RefreshUsage();
    }

    /// <summary>Tiles in the always-visible usage strip, for whichever engine is selected.</summary>
    public ObservableCollection<UsageMeter> UsageMeters { get; } = [];

    public bool HasUsageMeters => this.UsageMeters.Count > 0;

    /// <summary>Tiles per row: all in one row, except four, which go in a 2x2 grid.</summary>
    public int UsageColumns => this.UsageMeters.Count == 4 ? 2 : Math.Max(1, this.UsageMeters.Count);

    /// <summary>Shown in place of the meters before there's anything to report.</summary>
    public string UsageEmptyText { get => this.usageEmptyText; private set => this.Set(ref this.usageEmptyText, value); }

    /// <summary>Rebuilds the usage tiles. Also called on a timer so "resets in" stays current.</summary>
    public void RefreshUsage()
    {
        List<UsageMeter> meters = this.settings.Engine == ChatEngineKind.ClaudeCode ? this.PlanMeters() : this.ApiMeters();
        meters.Add(this.ContextMeter());

        this.UsageMeters.Clear();
        foreach (UsageMeter meter in meters)
        {
            this.UsageMeters.Add(meter);
        }

        this.UsageEmptyText = meters.Count > 0 ? string.Empty
            : this.settings.Engine == ChatEngineKind.ClaudeCode ? "Plan usage limits appear here after your first message."
            : "Token usage and rate limits appear here after your first message.";
        this.OnPropertyChanged(nameof(this.HasUsageMeters));
        this.OnPropertyChanged(nameof(this.UsageColumns));
    }

    private List<UsageMeter> PlanMeters()
    {
        // The 5-hour and weekly windows always show (at 0% until known); the first other window seen takes the third slot.
        string[] order = ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "seven_day_overage_included", "overage"];
        IEnumerable<string> windows = new[] { "five_hour", "seven_day" }
            .Concat(this.planLimits.Keys
                .Where(w => w is not "five_hour" and not "seven_day")
                .OrderBy(w => Array.IndexOf(order, w) is int i && i >= 0 ? i : order.Length)
                .Take(1));
        var meters = new List<UsageMeter>();
        foreach (string window in windows)
        {
            string label = window switch
            {
                "five_hour" => "5-hour limit",
                "seven_day" => "Weekly limit",
                "seven_day_opus" => "Weekly · Opus",
                "seven_day_sonnet" => "Weekly · Sonnet",
                "seven_day_overage_included" => "Weekly · with extra",
                "overage" => "Extra usage",
                _ => window,
            };

            if (!this.planLimits.TryGetValue(window, out PlanLimit? limit))
            {
                meters.Add(new UsageMeter(label, "0%", "Updates after your next message", 0, UsageSeverity.Normal));
                continue;
            }

            if (limit.ResetsAt is DateTimeOffset reset && reset <= DateTimeOffset.Now)
            {
                meters.Add(new UsageMeter(label, "0%", "Reset · updates after your next message", 0, UsageSeverity.Normal));
                continue;
            }

            // Without a percentage the bar still shows: full if the limit's been hit, empty otherwise.
            double percent = limit.Utilization ?? (limit.Status == "rejected" ? 100 : 0);
            string value = limit.Utilization is double pct ? $"{pct:0}%"
                : limit.Status switch { "rejected" => "Limit reached", "allowed_warning" => "Near limit", _ => "OK" };
            meters.Add(new UsageMeter(label, value, FormatReset(limit.ResetsAt), percent, UsageMeter.SeverityFor(limit.Utilization, limit.Status)));
        }

        return meters;
    }

    private List<UsageMeter> ApiMeters()
    {
        var meters = new List<UsageMeter>();
        TokenUsage t = this.sessionTokens;
        string sessionDetail = $"{FormatTokens(t.Input + t.CacheWrite)} in · {FormatTokens(t.Output)} out"
            + (t.CacheRead > 0 ? $" · {FormatTokens(t.CacheRead)} cached" : string.Empty);
        meters.Add(new UsageMeter("This session", $"≈ ${this.sessionCost:0.00}", sessionDetail, null, UsageSeverity.Normal));

        if (this.apiLimits is ApiRateLimits limits && limits.PercentUsed is double used)
        {
            bool reset = limits.ResetsAt is DateTimeOffset at && at <= DateTimeOffset.Now;
            double shown = reset ? 0 : used;
            string detail = reset ? "Per-minute window has reset" : FormatReset(limits.ResetsAt);
            meters.Add(new UsageMeter("Rate limit (per minute)", $"{shown:0}% used", detail, shown, UsageMeter.SeverityFor(shown)));
        }
        else
        {
            meters.Add(new UsageMeter("Rate limit (per minute)", "0% used", "Updates after your next message", 0, UsageSeverity.Normal));
        }

        return meters;
    }

    private UsageMeter ContextMeter()
    {
        long window = AgentSettings.ContextWindow(this.engine?.Model ?? this.settings.Model);
        double used = Math.Min(100, 100.0 * this.contextTokens / window);
        string detail = this.contextTokens > 0 ? $"{FormatTokens(this.contextTokens)} of {FormatTokens(window)} tokens"
            : $"{FormatTokens(window)} token window · updates as Claude replies";
        return new UsageMeter("Context", $"{used:0}%", detail, used, UsageMeter.SeverityFor(used));
    }

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1_000 ? $"{n / 1_000.0:0.#}k" : n.ToString("N0");

    private static string FormatReset(DateTimeOffset? at)
    {
        if (at is not DateTimeOffset reset)
        {
            return string.Empty;
        }

        TimeSpan left = reset - DateTimeOffset.Now;
        return left.TotalMinutes < 1 ? (left.TotalSeconds > 0 ? $"Resets in {left.Seconds}s" : "Resetting now")
            : left.TotalHours < 1 ? $"Resets in {(int)left.TotalMinutes}m"
            : left.TotalHours < 24 ? $"Resets in {(int)left.TotalHours}h {left.Minutes}m"
            : $"Resets {reset.ToLocalTime():ddd h:mm tt}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user sends a prompt, so the view can jump back to the bottom.</summary>
    public event EventHandler? PromptSent;

    /// <summary>Raised with a prompt's text when it's taken back for editing; the view puts it in the prompt box.</summary>
    public event EventHandler<string>? EditRequested;

    /// <summary>Raised when Claude needs the user (an approval, a question, or a finished run), with a short message.</summary>
    public event EventHandler<string>? AttentionNeeded;

    /// <summary>Asks the user to confirm something; returns true to go ahead. Set by the view.</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    /// <summary>Asks where to save an export, given a suggested file name; null if cancelled. Set by the view.</summary>
    public Func<string, string?> ChooseExportPath { get; set; } = _ => null;

    /// <summary>Puts text on the clipboard; false if the clipboard is busy. Set by the view.</summary>
    public Func<string, bool> CopyText { get; set; } = _ => false;

    public ObservableCollection<ChatEntry> Messages { get; } = [];

    /// <summary>Prompts sent before, for Up/Down recall.</summary>
    public PromptHistory History { get; } = new();

    public string RepoPath
    {
        get => this.repoPath;
        set
        {
            this.Set(ref this.repoPath, value);
            this.OnPropertyChanged(nameof(this.RepoName));
            this.OnPropertyChanged(nameof(this.RepoTooltip));
        }
    }

    /// <summary>The repository root for <see cref="RepoPath"/>, or null if it isn't inside one.</summary>
    public string? RepoRoot => this.engine?.RepoRoot ?? GitRunner.FindRepoRoot(this.repoPath.Trim());

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

    /// <summary>Shows a message in the footer.</summary>
    public void ShowStatus(string message) => this.Status = message;

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

    /// <summary>The whole-file change behind the pending approval, for the diff viewer; null for commands.</summary>
    public FileChange? ApprovalChange
    {
        get => this.approvalChange;
        private set
        {
            this.Set(ref this.approvalChange, value);
            this.OnPropertyChanged(nameof(this.HasApprovalChange));
        }
    }

    public bool HasApprovalChange => this.approvalChange != null;

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

    private long liveTokens;

    // The usage entry of the prompt in progress; its text is the realtime "1,234 tokens" count.
    private ChatEntry? liveUsageEntry;

    private void SetLiveTokens(long tokens)
    {
        this.liveTokens = tokens;
        if (tokens > 0)
        {
            this.ShowPromptUsage($"{tokens:N0} tokens");
        }
    }

    /// <summary>Shows the prompt's token count as the last transcript entry, below anything added since it last changed.</summary>
    private void ShowPromptUsage(string text)
    {
        if (this.liveUsageEntry is null)
        {
            this.liveUsageEntry = new ChatEntry(ChatEntryKind.Usage, string.Empty, text);
            this.Messages.Add(this.liveUsageEntry);
            return;
        }

        this.liveUsageEntry.SetText(text);
        int at = this.Messages.IndexOf(this.liveUsageEntry);
        if (at >= 0 && at < this.Messages.Count - 1)
        {
            this.Messages.Move(at, this.Messages.Count - 1);
        }
    }

    void IChatSink.ReportLiveTokens(long totalTokens) => this.SetLiveTokens(totalTokens);

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
            this.OnPropertyChanged(nameof(this.RepoFollowsSolution));
            this.OnPropertyChanged(nameof(this.RepoTooltip));
            this.UpdateSetupMessage();
            this.RefreshUsage(); // the strip shows different meters per engine
        }
    }

    /// <summary>Header chip text, e.g. "Opus 5.5 · High · Coding" or "Opus 5.5 · High · Coding · Claude Code".</summary>
    public string ModelSummary =>
        $"{AgentSettings.ModelName(this.settings.Model)} · {AgentSettings.EffortName(this.settings.Effort)} · {AgentSettings.ModeName(this.settings.Mode)}"
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

    /// <summary>Fetches the plan usage windows now, rather than waiting for the next prompt to report them.</summary>
    public async Task RefreshPlanUsageAsync()
    {
        foreach (PlanLimit limit in await PlanUsageClient.FetchAsync(CancellationToken.None))
        {
            ((IChatSink)this).ReportPlanLimit(limit);
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

    /// <summary>True unless the user picked a repository in Settings that no solution has replaced yet.</summary>
    public bool RepoFollowsSolution => this.settings.PinnedRepoPath is null;

    /// <summary>Header chip tooltip: the full path and whether it follows the open solution.</summary>
    public string RepoTooltip =>
        (this.repoPath.Length == 0 ? "No repository selected" : this.repoPath)
        + (this.RepoFollowsSolution
            ? "\nFollows the open solution"
            : "\nChosen in Settings, until you open a solution or folder in a git repository");

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

    /// <summary>
    /// Called when Visual Studio opens a solution or folder, and when the window loads with one already open.
    /// A solution inside a git repository always becomes the chat's repository, replacing any repository chosen
    /// in Settings; anything else leaves the current repository alone.
    /// </summary>
    public void OnSolutionOpened(string directory)
    {
        string? root = GitRunner.FindRepoRoot(directory);
        if (root is null)
        {
            this.Status = "The open solution isn't inside a git repository; keeping the current repository.";
            return;
        }

        if (this.settings.PinnedRepoPath != null)
        {
            AgentSettings unpinned = this.settings with { PinnedRepoPath = null };
            unpinned.Save();
            this.Settings = unpinned;
        }

        if (!string.Equals(root, this.RepoPath, StringComparison.OrdinalIgnoreCase))
        {
            this.RepoPath = root;
            this.Status = this.engine != null
                ? $"Switched to {this.RepoName} from the open solution. Your next message starts a new chat."
                : $"Repository: {this.RepoName} (from the open solution)";
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
            this.engine.Mode = newSettings.Mode;
        }

        this.Status = "Settings saved.";
    }

    /// <summary>Changes the model, effort or mode from the header menu or a slash command.</summary>
    public void QuickSet(ClaudeModel? model = null, EffortLevel? effort = null, AgentMode? mode = null)
    {
        AgentSettings updated = this.settings with
        {
            Model = model ?? this.settings.Model,
            Effort = effort ?? this.settings.Effort,
            Mode = mode ?? this.settings.Mode,
        };

        if (updated != this.settings)
        {
            this.ApplySettings(updated, this.RepoPath, null);
            this.Status = $"Now using {this.ModelSummary}.";
        }
    }

    public void NewChat()
    {
        this.Stop();
        this.DisposeEngine();
        this.Messages.Clear();
        this.ResetChatUsage();
        this.chatId = null;
        this.chatPinned = false;
        this.resumeFrom = null;
        this.Status = string.Empty;
        this.ShowHistory = false;
    }

    private void ResetChatUsage()
    {
        this.chatTokens = default;
        this.chatCost = 0;
        this.chatHasCost = false;
    }

    private void DisposeEngine()
    {
        this.engine?.Dispose();
        this.engine = null;

        // The context belongs to the engine's conversation; a reopened chat reports its size again after the next reply.
        if (this.contextTokens != 0)
        {
            this.contextTokens = 0;
            this.RefreshUsage();
        }
    }

    public void Stop()
    {
        this.pendingApproval?.TrySetResult(Approval.Deny);
        this.pendingQuestions?.TrySetResult(null);
        this.runCts?.Cancel();
    }

    public void Resolve(Approval approval) => this.pendingApproval?.TrySetResult(approval);

    /// <summary>The engine for the current repository, creating it (and handing it any reopened chat) if needed.</summary>
    private bool TryGetEngine(out IChatEngine result)
    {
        result = this.engine!;
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
            this.ApplyResume(created); // before the new prompt joins the transcript
        }

        result = this.engine;
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

            return new ClaudeCodeEngine(exe, root, this.settings.Model, this.settings.Effort, this.settings.Mode);
        }

        if (ApiKeyStore.Load() is not string apiKey)
        {
            this.NeedsApiKey = true;
            this.Status = "Add your Anthropic API key to start.";
            return null;
        }

        return new ClaudeAgent(apiKey, root, this.settings.Model, this.settings.Effort, this.settings.Mode);
    }

    /// <param name="prompt">The prompt being answered; a restore point is taken around it so its changes can be undone. Null for commands.</param>
    private async Task RunAsync(IChatEngine runEngine, Func<CancellationToken, Task> work, ChatEntry? prompt)
    {
        this.IsBusy = true;
        this.promptTokens = default;
        this.SetLiveTokens(0);
        this.promptCost = 0;
        this.promptHasCost = false;
        string outcome = "Claude has finished";
        using var cts = new CancellationTokenSource();
        this.runCts = cts;
        try
        {
            // Plan mode can't change files, so there's nothing to undo.
            if (prompt != null && runEngine.Mode != AgentMode.Plan)
            {
                prompt.SnapshotBefore = await WorkingTreeSnapshot.CaptureAsync(runEngine.RepoRoot, cts.Token);
            }

            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, "Stopped"));
            outcome = "Stopped";
        }
        catch (AnthropicUnauthorizedException)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "API key rejected", "Open settings and enter a valid Anthropic API key."));
            this.DisposeEngine();
            outcome = "Claude stopped with an error";
        }
        catch (Exception ex)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Something went wrong", ex.Message));
            outcome = "Claude stopped with an error";
            if (runEngine.Kind == ChatEngineKind.ClaudeCode)
            {
                await this.RefreshClaudeCodeStatusAsync(); // e.g. the sign-in expired
            }
        }
        finally
        {
            // Settle the live count with the final figure, below the reply.
            if (this.liveUsageEntry != null || this.promptTokens != default)
            {
                this.ShowPromptUsage(this.FormatPromptUsage());
            }

            if (ReferenceEquals(this.engine, runEngine))
            {
                this.chatTokens += this.promptTokens;
                this.chatCost += this.promptCost;
                this.chatHasCost |= this.promptHasCost;
            }

            this.liveUsageEntry = null;
            this.currentAssistant = null;
            this.currentThinking = null;
            this.runCts = null;
            this.HasPendingApproval = false;

            if (prompt?.SnapshotBefore != null)
            {
                prompt.SnapshotAfter = await WorkingTreeSnapshot.CaptureAsync(runEngine.RepoRoot, CancellationToken.None);
            }

            if (ReferenceEquals(this.engine, runEngine))
            {
                this.SaveChat(); // skipped if the chat was replaced or reset while this prompt was stopping
            }

            this.IsBusy = false;
            if (this.Status.StartsWith("Waiting", StringComparison.Ordinal) || this.Status.StartsWith("Running", StringComparison.Ordinal))
            {
                this.Status = string.Empty;
            }

            this.AttentionNeeded?.Invoke(this, outcome);
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

    void IChatSink.ToolActivity(ActivityKind kind, string title, string detail, FileChange? change)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        this.Messages.Add(new ChatEntry(ChatEntryKind.Tool, title, detail: Truncate(detail), activity: kind, change: change));
    }

    void IChatSink.SetStatus(string? status) => this.Status = status ?? string.Empty;

    /// <summary>Questions Claude is waiting on, shown in the question card; empty when there are none.</summary>
    public ObservableCollection<QuestionViewModel> PendingQuestions { get; } = [];

    public bool HasPendingQuestions => this.PendingQuestions.Count > 0;

    public bool CanSubmitAnswers => this.PendingQuestions.Count > 0 && this.PendingQuestions.All(q => q.IsAnswered);

    public string QuestionCardTitle => this.PendingQuestions.Count == 1 ? "Claude has a question" : $"Claude has {this.PendingQuestions.Count} questions";

    public void SubmitAnswers()
    {
        if (this.CanSubmitAnswers)
        {
            this.pendingQuestions?.TrySetResult(this.PendingQuestions.ToDictionary(q => q.Question, q => q.Answer!));
        }
    }

    public void SkipQuestions() => this.pendingQuestions?.TrySetResult(null);

    async Task<IReadOnlyDictionary<string, string>?> IChatSink.AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingQuestions = tcs;

        this.PendingQuestions.Clear();
        foreach (UserQuestion question in questions)
        {
            this.PendingQuestions.Add(new QuestionViewModel(question, this.OnAnswersChanged));
        }

        this.OnQuestionsChanged();
        this.AttentionNeeded?.Invoke(this, this.QuestionCardTitle);
        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                this.pendingQuestions = null;
                this.PendingQuestions.Clear();
                this.OnQuestionsChanged();
            }
        }
    }

    private void OnAnswersChanged() => this.OnPropertyChanged(nameof(this.CanSubmitAnswers));

    private void OnQuestionsChanged()
    {
        this.OnPropertyChanged(nameof(this.HasPendingQuestions));
        this.OnPropertyChanged(nameof(this.QuestionCardTitle));
        this.OnPropertyChanged(nameof(this.CanSubmitAnswers));
    }

    void IChatSink.ReportTokenUsage(ClaudeModel model, TokenUsage usage)
    {
        double cost = ModelPricing.EstimateCost(model, usage);
        this.sessionTokens += usage;
        this.sessionCost += cost;
        this.promptTokens += usage;
        this.promptCost += cost;
        this.promptHasCost = true;
        this.RefreshUsage();
    }

    // Claude Code usage comes out of the plan, so there's no per-token cost to show.
    void IChatSink.ReportPromptUsage(TokenUsage usage) => this.promptTokens += usage;

    private string FormatPromptUsage()
    {
        TokenUsage t = this.promptTokens;
        long total = this.liveTokens > 0 ? this.liveTokens : t.Input + t.CacheWrite + t.Output; // new tokens only; cache reads re-send the history every round
        string text = $"{total:N0} tokens";
        return this.promptHasCost ? $"{text} · ≈ ${this.promptCost:0.00}" : text;
    }

    void IChatSink.ReportContext(long tokens)
    {
        this.contextTokens = tokens;
        this.RefreshUsage();
    }

    void IChatSink.ReportCompacted(long tokensBefore)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        string was = tokensBefore > 0 ? $" · was {FormatTokens(tokensBefore)} tokens" : string.Empty;
        this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"Context compacted{was}"));
        this.contextTokens = 0; // the next reply reports the new size
        this.RefreshUsage();
    }

    void IChatSink.ReportApiRateLimits(ApiRateLimits limits)
    {
        this.apiLimits = limits;
        this.RefreshUsage();
    }

    void IChatSink.ReportPlanLimit(PlanLimit limit)
    {
        // Stream events often omit the percentage; keep the last exact figure for the same window period.
        if (limit.Utilization is null
            && this.planLimits.TryGetValue(limit.Window, out PlanLimit? known)
            && known.Utilization is double pct
            && known.ResetsAt is DateTimeOffset knownReset && limit.ResetsAt is DateTimeOffset newReset
            && Math.Abs((knownReset - newReset).TotalMinutes) < 5)
        {
            limit = limit with { Utilization = pct };
        }

        this.planLimits[limit.Window] = limit;
        PlanUsageStore.Save(this.planLimits.Values);
        this.RefreshUsage();
    }

    async Task<Approval> IChatSink.RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        var tcs = new TaskCompletionSource<Approval>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingApproval = tcs;
        this.ApprovalTitle = title;
        this.ApprovalLines = DiffLine.Parse(Truncate(detail), kind);
        this.ApprovalChange = change;
        this.AllowAllLabel = allowAllLabel;
        this.HasPendingApproval = true;
        this.AttentionNeeded?.Invoke(this, "Claude needs your approval");
        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                this.HasPendingApproval = false;
                this.ApprovalChange = null;
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
