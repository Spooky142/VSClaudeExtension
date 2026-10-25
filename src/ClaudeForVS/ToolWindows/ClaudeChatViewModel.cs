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
    private ChatEntry? currentAssistant;
    private ChatEntry? currentThinking;

    private string repoPath = string.Empty;
    private string status = string.Empty;
    private bool isBusy;
    private bool needsApiKey;
    private bool hasPendingApproval;
    private AgentSettings settings;
    private ClaudeCodeStatus? codeStatus;
    private string setupMessage = string.Empty;

    // Every chat window's view model (they live as long as Visual Studio), so one chat isn't opened in two windows.
    private static readonly List<ClaudeChatViewModel> Instances = [];

    public ClaudeChatViewModel()
    {
        Instances.Add(this);
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
        this.RefreshBanners();
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

    /// <summary>The name and text of the Output window's active pane (for /output); null if it can't be read.</summary>
    public Func<(string Name, string Text)?> ReadOutputWindow { get; set; } = () => null;

    /// <summary>The open solution's file, to open again after an update; null if none. Set by the view.</summary>
    public Func<string?> GetSolutionFile { get; set; } = () => null;

    /// <summary>Closes Visual Studio as File > Exit does. Set by the view.</summary>
    public Action ExitVisualStudio { get; set; } = () => { };

    /// <summary>The files open in the editor (for /openfiles).</summary>
    public Func<IReadOnlyList<(string Path, bool Unsaved)>> ListOpenDocuments { get; set; } = () => [];

    public BulkObservableCollection<ChatEntry> Messages { get; } = [];

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
    public string? RepoRoot => this.engine?.RepoRoot ?? this.FindRepoRoot();

    // The last lookup of RepoPath's repository root. Links in replies ask for the root once per path they resolve, and
    // looking it up walks the folders on disk; it's kept briefly, so a folder made into a repository is soon noticed.
    private (string? Path, string? Root, DateTime At) repoRootLookup;

    private string? FindRepoRoot()
    {
        string path = this.repoPath.Trim();
        (string? Path, string? Root, DateTime At) lookup = this.repoRootLookup;
        if (lookup.Path != path || DateTime.UtcNow - lookup.At > TimeSpan.FromSeconds(10))
        {
            lookup = (path, GitRunner.FindRepoRoot(path), DateTime.UtcNow);
            this.repoRootLookup = lookup;
        }

        return lookup.Root;
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

    /// <summary>Shows a message in the footer.</summary>
    public void ShowStatus(string message) => this.Status = message;

    public bool IsBusy
    {
        get => this.isBusy;
        private set
        {
            this.Set(ref this.isBusy, value);
            this.OnPropertyChanged(nameof(this.IsIdle));
            if (!value && this.afterBusy is Action next)
            {
                this.afterBusy = null;
                next();
            }
        }
    }

    public bool IsIdle => !this.isBusy;

    // What was asked for while a reply (or an undo) was running, to carry out once it has finished; see WhenIdle.
    private Action? afterBusy;

    /// <summary>
    /// Runs the action now, or stops the reply in progress and runs it once that has finished, so the reply's last rows
    /// and its save land in its own chat rather than a new one (or bring back a chat that was just deleted).
    /// </summary>
    private void WhenIdle(Action action)
    {
        if (!this.IsBusy)
        {
            action();
            return;
        }

        this.afterBusy += action;
        this.Stop();
        this.Status = "Stopping…";
    }

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
            this.OnPropertyChanged(nameof(this.SubagentSummary));
            this.OnPropertyChanged(nameof(this.RepoFollowsSolution));
            this.OnPropertyChanged(nameof(this.RepoTooltip));
            this.UpdateSetupMessage();
            this.RefreshUsage(); // the strip shows different meters per engine
            this.RefreshBanners();
        }
    }

    /// <summary>Header chip text, e.g. "Opus 5.5 · High · Coding" or "Opus 5.5 · High · Coding · Claude Code".</summary>
    public string ModelSummary =>
        $"{this.settings.NameOf(this.settings.Model)} · {AgentSettings.EffortName(this.settings.Effort)} · {AgentSettings.ModeName(this.settings.Mode)}"
        + (this.settings.Engine == ChatEngineKind.ClaudeCode ? " · Claude Code" : string.Empty);

    /// <summary>
    /// Subagents chip text, e.g. "Haiku 5.5 · Low" on the Claude API, or "Automatic" when Claude Code picks the model.
    /// Claude Code has no setting for its subagents' effort, so it isn't shown there.
    /// </summary>
    public string SubagentSummary =>
        this.settings.Engine == ChatEngineKind.ClaudeCode
            ? (this.settings.SubagentModel is ClaudeModel model ? this.settings.NameOf(model) : "Automatic")
            : $"{this.settings.NameOf(this.settings.SubagentModel ?? AgentSettings.DefaultApiSubagentModel)} · {AgentSettings.EffortName(this.settings.SubagentEffort)}";

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
            unpinned.SaveChangesFrom(this.settings);
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
        bool modelChanged = newSettings.Model != this.settings.Model
            || (newSettings.UseNewestApiModels != this.settings.UseNewestApiModels
                && newSettings.ApiModelId(newSettings.Model) != this.settings.ApiModelId(this.settings.Model));
        newSettings.SaveChangesFrom(this.settings);
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
            this.WhenIdle(() =>
            {
                bool hadHistory = this.Messages.Count > 0;
                this.DisposeEngine();
                if (hadHistory)
                {
                    string via = newSettings.Engine == ChatEngineKind.ClaudeCode ? " · Claude Code" : string.Empty;
                    this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"New chat · {newSettings.NameOf(newSettings.Model)}{via}"));
                }
            });
        }
        else
        {
            this.engine.Effort = newSettings.Effort; // used from the next message on
            this.engine.Mode = newSettings.Mode;
            if (this.engine is ClaudeAgent agent)
            {
                ApplyApiSettings(agent, newSettings);
            }
            else if (this.engine is ClaudeCodeEngine code)
            {
                code.SubagentModel = newSettings.SubagentModel;
            }
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

    /// <summary>Changes the subagents' model (null for automatic) or effort from the header menu, from the next subagent on.</summary>
    public void QuickSetSubagent(ClaudeModel? model, EffortLevel effort)
    {
        AgentSettings updated = this.settings with { SubagentModel = model, SubagentEffort = effort };
        if (updated != this.settings)
        {
            this.ApplySettings(updated, this.RepoPath, null);
            this.Status = $"Subagents now use {this.SubagentSummary}.";
        }
    }

    public void NewChat() => this.WhenIdle(this.ResetChat);

    private void ResetChat()
    {
        this.DisposeEngine();
        this.Messages.Clear();
        ChatChanges.Forget(this);
        this.SetTasks([]);
        this.ResetChatUsage();
        this.chatId = null;
        this.chatPinned = false;
        this.resumeFrom = null;
        this.Status = string.Empty;
        this.ShowHistory = false;
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

            return new ClaudeCodeEngine(exe, root, this.settings.Model, this.settings.Effort, this.settings.Mode)
            {
                SubagentModel = this.settings.SubagentModel,
            };
        }

        if (ApiKeyStore.Load() is not string apiKey)
        {
            this.NeedsApiKey = true;
            this.Status = "Add your Anthropic API key to start.";
            return null;
        }

        var agent = new ClaudeAgent(apiKey, root, this.settings.Model, this.settings.Effort, this.settings.Mode)
        {
            ModelId = this.settings.ApiModelId(this.settings.Model),
        };
        ApplyApiSettings(agent, this.settings);
        return agent;
    }

    /// <summary>The settings that only the Claude API engine has, which can change between messages.</summary>
    private static void ApplyApiSettings(ClaudeAgent agent, AgentSettings settings)
    {
        agent.HooksEnabled = settings.RunHooks;
        agent.SpendLimitUsd = settings.SpendLimitUsd;
        agent.CompactAtTokens = settings.CompactAtTokens;
        agent.SubagentModel = settings.SubagentModel ?? AgentSettings.DefaultApiSubagentModel;
        agent.SubagentModelId = settings.ApiModelId(agent.SubagentModel);
        agent.SubagentEffort = settings.SubagentEffort;
    }

    /// <param name="prompt">The prompt being answered; a restore point is taken around it so its changes can be undone. Null for commands.</param>
    private async Task RunAsync(IChatEngine runEngine, Func<CancellationToken, Task> work, ChatEntry? prompt)
    {
        this.IsBusy = true;
        this.runChanges.Clear();
        this.promptTokens = default;
        this.SetLiveTokens(0);
        this.promptCost = 0;
        this.promptHasCost = false;
        string outcome = "Claude has finished";
        bool failed = false;
        bool keyRejected = false;
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
            keyRejected = true; // the engine goes once the chat is saved; disposing it now would skip the save
            outcome = "Claude stopped with an error";
        }
        catch (Exception ex)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Something went wrong", ex.Message));
            outcome = "Claude stopped with an error";
            failed = true;
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

            ChatEntry? usageEntry = this.liveUsageEntry;
            this.liveUsageEntry = null;
            this.currentAssistant = null;
            this.currentThinking = null;
            this.HasPendingApproval = false;

            // Formatting and listing the changes can take a while in a big repository, so Stop still works meanwhile: it
            // leaves the files as they are, without the list (the prompt can still be undone).
            using var finishing = new CancellationTokenSource();
            this.runCts = finishing;
            if (prompt?.SnapshotBefore != null)
            {
                try
                {
                    await this.FormatChangedFilesAsync(runEngine, usageEntry, finishing.Token); // before the snapshot, so the diff shows the files as they were left
                    prompt.SnapshotAfter = await WorkingTreeSnapshot.CaptureAsync(runEngine.RepoRoot, finishing.Token);
                    await this.ShowChangedFilesAsync(runEngine.RepoRoot, prompt, usageEntry, finishing.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    // Whatever goes wrong here, the chat must still be saved and Send enabled again below.
                    this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Couldn't list the files Claude changed", ex.Message));
                }
            }

            this.runCts = null;
            if (ReferenceEquals(this.engine, runEngine))
            {
                this.SaveChat(); // skipped if the chat was replaced or reset while this prompt was stopping
                if (keyRejected)
                {
                    this.DisposeEngine(); // a new key needs a new client
                }
            }

            this.IsBusy = false; // also carries out a new chat, deletion or engine change asked for while this ran
            if (runEngine.Kind == ChatEngineKind.ClaudeCode)
            {
                lastRunEndedUtc = DateTime.UtcNow;
                ObserveModel(runEngine);
            }

            if (this.Status.StartsWith("Waiting", StringComparison.Ordinal) || this.Status.StartsWith("Running", StringComparison.Ordinal))
            {
                this.Status = string.Empty;
            }

            this.AttentionNeeded?.Invoke(this, outcome);
        }

        // Claude Code stops at the 5-hour limit; if there's weekly limit left, offer to carry on at lower priority.
        if (failed && runEngine.Kind == ChatEngineKind.ClaudeCode && ReferenceEquals(this.engine, runEngine))
        {
            await this.OfferLowPriorityAsync();
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

    // The files this prompt's tools changed, in order, so the code Claude wrote can be formatted when it's done.
    private readonly List<FileChange> runChanges = [];

    /// <summary>
    /// Formats the lines Claude wrote in the files it changed (see <see cref="ChangeFormatter"/>) and says so in the
    /// transcript. Never fails the run: if formatting can't be done the files stay as Claude left them.
    /// </summary>
    private async Task FormatChangedFilesAsync(IChatEngine runEngine, ChatEntry? usageEntry, CancellationToken cancellationToken)
    {
        if (!this.settings.FormatChangedFiles || runEngine.Mode == AgentMode.Plan || this.runChanges.Count == 0 || CodeIntelligenceHost.Current is not ICodeIntelligence code)
        {
            return;
        }

        string status = this.Status; // e.g. "Done in 12s", which formatting shouldn't wipe
        try
        {
            this.Status = "Formatting the changed code…";
            IReadOnlyList<string> formatted = await ChangeFormatter.FormatAsync(this.runChanges.ToList(), runEngine.RepoRoot, code, OpenDocuments.HasUnsavedChanges, cancellationToken);
            if (formatted.Count == 0)
            {
                return;
            }

            string names = formatted.Count <= 3 ? string.Join(", ", formatted.Select(Path.GetFileName)) : $"{formatted.Count} files";
            var notice = new ChatEntry(ChatEntryKind.Notice, $"Formatted the code Claude wrote to match your editor settings ({names})");
            int at = usageEntry is null ? -1 : this.Messages.IndexOf(usageEntry);
            if (at >= 0)
            {
                this.Messages.Insert(at, notice);
            }
            else
            {
                this.Messages.Add(notice);
            }
        }
        catch (Exception)
        {
            // Formatting is a courtesy (and Roslyn's formatter can throw almost anything); the files are as Claude left them.
        }
        finally
        {
            this.Status = status;
        }
    }

    void IChatSink.ToolActivity(ActivityKind kind, string title, string detail, FileChange? change)
    {
        if (change != null && (kind is ActivityKind.Edit or ActivityKind.Create))
        {
            this.runChanges.Add(change);
            ChatChanges.Record(this, change); // for the editor's margin
        }

        this.currentAssistant = null;
        this.currentThinking = null;
        this.Messages.Add(new ChatEntry(ChatEntryKind.Tool, title, detail: Truncate(detail), activity: kind, change: change));
        this.ReportToolTaskResult(kind, title, detail);
    }

    void IChatSink.SetStatus(string? status) => this.Status = status ?? string.Empty;

    private static string Truncate(string detail)
        => detail.Length > MaxDetailChars ? detail.Substring(0, MaxDetailChars) + "\n…" : detail;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return; // nothing to redraw (status updates often repeat themselves)
        }

        field = value;
        this.OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
