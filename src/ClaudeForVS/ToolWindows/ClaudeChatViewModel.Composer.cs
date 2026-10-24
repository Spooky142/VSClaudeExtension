using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

namespace ClaudeForVS.ToolWindows;

/// <summary>The prompt box: attachments, @file and /command completion, sending, and slash commands.</summary>
internal sealed partial class ClaudeChatViewModel
{
    private readonly RepoFileIndex repoFiles = new();

    private Attachment? activeFile;

    // The editor document behind the active-file chip, and one the user dismissed (left out until another file is opened).
    private string? editorPath;
    private string? dismissedEditorPath;

    // True when the file was left out only because its selection was attached, which lasts for one message.
    private bool dismissedForSelection;

    /// <summary>Things attached to the next prompt by hand: selections, errors, images.</summary>
    public ObservableCollection<Attachment> Attachments { get; } = [];

    /// <summary>The editor's document and caret, sent with the next prompt unless dismissed.</summary>
    public Attachment? ActiveFile
    {
        get => this.activeFile;
        private set
        {
            this.Set(ref this.activeFile, value);
            this.OnPropertyChanged(nameof(this.HasActiveFile));
            this.OnPropertyChanged(nameof(this.HasAttachments));
        }
    }

    public bool HasActiveFile => this.activeFile != null;

    public bool HasAttachments => this.activeFile != null || this.Attachments.Count > 0;

    /// <summary>Updates the active-file chip from the editor.</summary>
    public void SetEditor(EditorContext? editor)
    {
        this.editorPath = editor?.FullPath;
        if (editor is null)
        {
            this.ActiveFile = null;
            return;
        }

        if (this.dismissedEditorPath != null)
        {
            if (string.Equals(this.dismissedEditorPath, editor.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                this.ActiveFile = null;
                return;
            }

            this.dismissedEditorPath = null; // another file is open, so the dismissal has served its purpose
            this.dismissedForSelection = false;
        }

        this.ActiveFile = Attachment.ForActiveFile(editor, this.RepoRoot ?? this.RepoPath);
    }

    public void AddAttachment(Attachment attachment)
    {
        this.Attachments.Add(attachment);
        this.ShowHistory = false;
    }

    /// <summary>Attaches files, folders and projects selected in Solution Explorer. Returns how many were attached.</summary>
    public int AddSolutionItems(IReadOnlyList<SolutionItem> items)
    {
        string root = this.RepoRoot ?? this.RepoPath;
        IReadOnlyList<string> files = this.RepoRoot is string repo ? this.repoFiles.Get(repo) : [];
        foreach (SolutionItem item in items)
        {
            this.AddAttachment(item.IsFolder ? Attachment.ForFolder(item.FullPath, item.ProjectFile, root, files) : Attachment.ForFile(item.FullPath, root));
        }

        return items.Count;
    }

    /// <summary>Attaches a selection from the editor, leaving that file out of the automatic active-file context.</summary>
    public void AddSelection(EditorContext editor)
    {
        this.dismissedEditorPath = editor.FullPath;
        this.dismissedForSelection = true;
        this.ActiveFile = null;
        this.AddAttachment(Attachment.ForSelection(editor, this.RepoRoot ?? this.RepoPath));
    }

    public void RemoveAttachment(Attachment attachment)
    {
        if (attachment.Kind == AttachmentKind.ActiveFile)
        {
            this.dismissedEditorPath = this.editorPath;
            this.dismissedForSelection = false;
            this.ActiveFile = null;
        }
        else
        {
            this.Attachments.Remove(attachment);
        }
    }

    /// <summary>
    /// Completions for the word being typed at <paramref name="caret"/>: commands at the start of the prompt, or
    /// repository files after "@". <paramref name="start"/> is where the text they replace begins.
    /// </summary>
    public IReadOnlyList<Suggestion> GetSuggestions(string text, int caret, out int start)
    {
        start = 0;
        caret = Math.Max(0, Math.Min(caret, text.Length));
        string before = text.Substring(0, caret);

        if (before.StartsWith("/") && !before.Contains('\n') && before.Count(c => c == ' ') <= 1)
        {
            return SlashCommands.Suggest(before, this.LoadCustomCommands());
        }

        int at = caret - 1;
        while (at >= 0 && !char.IsWhiteSpace(text[at]) && text[at] != '@')
        {
            at--;
        }

        if (at < 0 || text[at] != '@' || (at > 0 && !char.IsWhiteSpace(text[at - 1])) || this.RepoRoot is not string root)
        {
            return [];
        }

        start = at;
        string query = text.Substring(at + 1, caret - at - 1);
        return RepoFileIndex.Match(this.repoFiles.Get(root), query, 12)
            .Select(f => new Suggestion(f, string.Empty, "@" + f + " "))
            .ToList();
    }

    /// <summary>Starts loading the repository's file list, so the first "@" has something to show.</summary>
    public void WarmFileIndex()
    {
        if (this.RepoRoot is string root)
        {
            this.repoFiles.Get(root);
        }
    }

    /// <summary>Looks a repository file up by name or path, for links in replies. Uses the cached file list.</summary>
    public string? FindRepoFile(string path)
    {
        if (this.RepoRoot is not string root)
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').TrimStart('.', '/');
        IReadOnlyList<string> files = this.repoFiles.Get(root);
        if (normalized.Contains('/'))
        {
            string? match = files.FirstOrDefault(f => f.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => f.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : System.IO.Path.Combine(root, match.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        // A bare file name links only when exactly one file in the repository has it.
        List<string> named = files.Where(f => f.Equals(normalized, StringComparison.OrdinalIgnoreCase) || f.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return named.Count == 1 ? System.IO.Path.Combine(root, named[0].Replace('/', System.IO.Path.DirectorySeparatorChar)) : null;
    }

    /// <summary>Sends a prompt (or runs a command). Returns false (and leaves the prompt box alone) if it couldn't be sent.</summary>
    public bool TrySend(string text, out Task run)
    {
        run = Task.CompletedTask;
        text = text.Trim();
        if (text.Length == 0 || this.IsBusy)
        {
            return false;
        }

        if (SlashCommands.Parse(text) is SlashCommand command)
        {
            // Commands that are really prompts go to Claude in full; the transcript shows what was typed.
            if (command.Name is "/review" or "/init")
            {
                return this.SendWithAttachments(text, command.Name == "/init" ? SlashCommands.InitPrompt : SlashCommands.ReviewPrompt(command.Argument), out run);
            }

            bool handled = this.RunCommand(command, out run);
            if (handled)
            {
                this.History.Add(text);
            }

            return handled;
        }

        // Claude Code expands its own custom commands (with everything they support); the API engine needs them expanded here.
        if (this.settings.Engine == ChatEngineKind.Api
            && SlashCommands.ParseCustom(text, this.LoadCustomCommands()) is (CustomCommand custom, string arguments))
        {
            return this.SendWithAttachments(text, custom.Expand(arguments), out run);
        }

        return this.SendWithAttachments(text, text, out run);
    }

    /// <summary>Custom commands for the current repository and user, read fresh so edits to them apply straight away.</summary>
    private IReadOnlyList<CustomCommand> LoadCustomCommands() => CustomCommands.Load(this.RepoRoot);

    /// <summary>Sends <paramref name="sendText"/> with the attachments, showing <paramref name="displayText"/> in the transcript.</summary>
    private bool SendWithAttachments(string displayText, string sendText, out Task run)
    {
        string text = displayText;
        var attachments = new List<Attachment>();
        if (this.activeFile != null)
        {
            attachments.Add(this.activeFile);
        }

        attachments.AddRange(this.Attachments);
        (ChatPrompt prompt, string note) = Attachment.Compose(sendText, attachments);
        if (!this.TrySendPrompt(text, prompt, note, out run))
        {
            return false;
        }

        this.Attachments.Clear();
        this.History.Add(text);
        if (this.dismissedForSelection)
        {
            this.dismissedEditorPath = null; // the file comes back automatically from the next message
            this.dismissedForSelection = false;
        }

        return true;
    }

    private bool TrySendPrompt(string displayText, ChatPrompt prompt, string note, out Task run)
    {
        run = Task.CompletedTask;
        if (!this.TryGetEngine(out IChatEngine runEngine))
        {
            return false;
        }

        var entry = new ChatEntry(ChatEntryKind.User, "You", displayText)
        {
            Note = note,
            SentPrompt = prompt,
            Checkpoint = runEngine.Checkpoint,
        };
        this.Messages.Add(entry);
        this.ShowHistory = false;
        this.PromptSent?.Invoke(this, EventArgs.Empty);
        this.SaveChat();
        run = this.RunAsync(runEngine, ct => runEngine.SendAsync(prompt, this, ct), entry);
        return true;
    }

    private bool RunCommand(SlashCommand command, out Task run)
    {
        run = Task.CompletedTask;
        switch (command.Name)
        {
            case "/help":
                this.ShowLocalReply(SlashCommands.HelpText(this.LoadCustomCommands()));
                return true;

            case "/copy":
                if (this.Messages.LastOrDefault(m => m.Kind == ChatEntryKind.Assistant && !m.IsLocalReply) is not ChatEntry reply)
                {
                    this.Status = "There's no reply from Claude to copy yet.";
                    return false;
                }

                this.Status = this.CopyText(reply.Text) ? "Copied Claude's last reply." : "The clipboard is busy; try again.";
                return true;

            case "/cost":
                this.ShowLocalReply(this.UsageReport());
                return true;

            case "/context":
                this.ShowLocalReply(this.ContextReport());
                return true;

            case "/clear":
                this.NewChat();
                return true;

            case "/history":
                this.ShowHistory = true;
                return true;

            case "/export":
                this.ExportCurrentChat();
                return true;

            case "/compact":
                if (!this.TryGetEngine(out IChatEngine compactEngine))
                {
                    return false;
                }

                this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, "Compacting the conversation…"));
                this.PromptSent?.Invoke(this, EventArgs.Empty);
                run = this.RunAsync(compactEngine, ct => compactEngine.CompactAsync(this, ct), null);
                return true;

            case "/retry":
                if (this.Messages.LastOrDefault(m => m.Kind == ChatEntryKind.User) is not ChatEntry retry)
                {
                    this.Status = "There's no message to retry.";
                    return false;
                }

                run = this.RetryAsync(retry);
                return true;

            case "/undo":
                if (this.Messages.LastOrDefault(m => m.CanUndo) is not ChatEntry undo)
                {
                    this.Status = "There are no file changes to undo.";
                    return false;
                }

                run = this.UndoAsync(undo);
                return true;

            case "/diff":
                run = this.AttachDiffAsync();
                return true;

            case "/output":
                if (this.ReadOutputWindow() is not (string paneName, string paneText) || paneText.Trim().Length == 0)
                {
                    this.Status = "The Output window has nothing to attach. Show the pane you want in the Output window first.";
                    return false;
                }

                this.AddAttachment(Attachment.ForOutput(paneName, paneText));
                this.Status = $"Attached the \"{(paneName.Length > 0 ? paneName : "Output")}\" pane. Send your message when you're ready.";
                return true;

            case "/openfiles":
                IReadOnlyList<(string Path, bool Unsaved)> documents = this.ListOpenDocuments();
                if (documents.Count == 0)
                {
                    this.Status = "No files are open in the editor.";
                    return false;
                }

                this.AddAttachment(Attachment.ForOpenFiles(documents, this.RepoRoot ?? this.RepoPath));
                this.Status = "Attached the list of open files. Send your message when you're ready.";
                return true;

            case "/checkpoint":
                run = this.CreateRestorePointAsync(command.Argument);
                return true;

            case "/restore":
                run = this.RestoreCommandAsync(command.Argument);
                return true;

            case "/model":
                return this.SetFromCommand(command, v => v switch
                {
                    "opus" => (ClaudeModel?)ClaudeModel.Opus,
                    "sonnet" => ClaudeModel.Sonnet,
                    "haiku" => ClaudeModel.Haiku,
                    _ => null,
                }, m => this.QuickSet(model: m));

            case "/effort":
                return this.SetFromCommand(command, v => v switch
                {
                    "low" => (EffortLevel?)EffortLevel.Low,
                    "medium" => EffortLevel.Medium,
                    "high" => EffortLevel.High,
                    "xhigh" => EffortLevel.XHigh,
                    "max" => EffortLevel.Max,
                    _ => null,
                }, e => this.QuickSet(effort: e));

            case "/mode":
                return this.SetFromCommand(command, v => v switch
                {
                    "auto" => (AgentMode?)AgentMode.Auto,
                    "plan" => AgentMode.Plan,
                    "coding" => AgentMode.Coding,
                    _ => null,
                }, m => this.QuickSet(mode: m));

            default:
                return false;
        }
    }

    // Longer diffs are cut; Claude can run git diff itself for the rest.
    private const int MaxAttachedDiffChars = 60_000;

    /// <summary>/diff: attaches the repository's uncommitted changes (against HEAD) and the names of untracked files.</summary>
    private async Task AttachDiffAsync()
    {
        if (this.RepoRoot is not string root)
        {
            this.Status = "Set the repository in Settings first.";
            return;
        }

        try
        {
            this.Status = "Reading the uncommitted changes…";
            (int code, string diff) = await GitRunner.RunAsync(root, ["diff", "HEAD", "--no-ext-diff", "--no-textconv", "--no-color"], CancellationToken.None, includeStderr: false);
            if (code != 0)
            {
                // No commits yet: there's no HEAD to compare with.
                (code, diff) = await GitRunner.RunAsync(root, ["diff", "--no-ext-diff", "--no-textconv", "--no-color"], CancellationToken.None, includeStderr: false);
            }

            (_, string others) = await GitRunner.RunAsync(root, ["ls-files", "--others", "--exclude-standard"], CancellationToken.None, includeStderr: false);
            List<string> untracked = others.Replace("\r\n", "\n").Split(['\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (diff.Length > MaxAttachedDiffChars)
            {
                diff = diff.Substring(0, MaxAttachedDiffChars) + "\n… (diff cut; run git diff for the rest)";
            }

            this.Status = string.Empty;
            if (diff.Trim().Length == 0 && untracked.Count == 0)
            {
                this.Status = "There are no uncommitted changes to attach.";
                return;
            }

            this.AddAttachment(Attachment.ForDiff(diff, untracked));
            this.Status = "Attached your uncommitted changes. Send your message when you're ready.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            this.Status = "Couldn't read the changes: " + ex.Message;
        }
    }

    /// <summary>Shows the extension's own answer to a command, styled like a reply but kept out of the conversation.</summary>
    private void ShowLocalReply(string markdown)
    {
        this.Messages.Add(new ChatEntry(ChatEntryKind.Assistant, ChatEntry.LocalReplyHeader, markdown));
        this.PromptSent?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>/cost: this chat's tokens, then everything the usage strip shows except the context.</summary>
    private string UsageReport()
    {
        var sb = new System.Text.StringBuilder("**Usage**\n\n");
        TokenUsage t = this.chatTokens;
        string chat = t == default ? "nothing yet"
            : $"{FormatTokens(t.Input + t.CacheWrite)} in · {FormatTokens(t.Output)} out"
                + (t.CacheRead > 0 ? $" · {FormatTokens(t.CacheRead)} read from cache" : string.Empty)
                + (this.chatHasCost ? $" · ≈ ${this.chatCost:0.00}" : string.Empty);
        sb.Append($"- **This chat** (since it was opened): {chat}\n");

        foreach (UsageMeter meter in this.UsageMeters.Where(m => m.Label != "Context"))
        {
            sb.Append($"- **{meter.Label}**: {meter.Value}{(meter.Detail.Length > 0 ? " · " + meter.Detail : string.Empty)}\n");
        }

        sb.Append(this.settings.Engine == ChatEngineKind.Api
            ? "\nCosts are estimates at list prices. Your Console account has the exact figures."
            : "\nClaude Code usage counts against your plan's limits rather than costing per token.");
        return sb.ToString();
    }

    /// <summary>/context: how full the context window is, and where the instructions come from.</summary>
    private string ContextReport()
    {
        ClaudeModel model = this.engine?.Model ?? this.settings.Model;
        long window = AgentSettings.ContextWindow;
        var sb = new System.Text.StringBuilder("**Context**\n\n");
        sb.Append(this.contextTokens > 0
            ? $"- {FormatTokens(this.contextTokens)} of {FormatTokens(window)} tokens ({100.0 * this.contextTokens / window:0}%) · {this.settings.NameOf(model)}\n"
            : $"- {FormatTokens(window)} token window ({this.settings.NameOf(model)}). How much is used is known after Claude's next reply.\n");
        sb.Append($"- {this.Messages.Count(m => m.Kind == ChatEntryKind.User)} of your messages and {this.Messages.Count(m => m.Kind == ChatEntryKind.Assistant && !m.IsLocalReply)} replies in this chat\n");

        bool api = (this.engine?.Kind ?? this.settings.Engine) == ChatEngineKind.Api;
        if (api)
        {
            IReadOnlyList<string> files = this.engine is ClaudeAgent agent ? agent.InstructionFiles
                : this.RepoRoot is string root ? ProjectInstructions.Load(root).Files : [];
            sb.Append(files.Count == 0
                ? "- No CLAUDE.md files (`/init` creates one)\n"
                : "- Following " + string.Join(", ", files.Select(f => $"`{f}`")) + "\n");

            IReadOnlyList<string> settingsFiles = this.engine is ClaudeAgent withRules ? withRules.PermissionFiles
                : this.RepoRoot is string repo ? PermissionRules.Load(repo).Files : [];
            if (settingsFiles.Count > 0)
            {
                sb.Append("- Permission rules from " + string.Join(", ", settingsFiles.Select(f => $"`{f}`")) + "\n");
            }

            HookRules hookRules = this.engine is ClaudeAgent running ? running.Hooks
                : this.RepoRoot is string hookRepo ? HookRules.Load(hookRepo) : HookRules.Empty;
            if (hookRules.Count > 0)
            {
                sb.Append($"- {(hookRules.Count == 1 ? "1 hook" : $"{hookRules.Count} hooks")} in {string.Join(", ", hookRules.Files.Select(f => $"`{f}`"))}"
                    + (this.settings.RunHooks ? string.Empty : " (not run: turn on **Run hooks** in Settings)") + "\n");
            }

            if (this.settings.SpendLimitUsd > 0)
            {
                sb.Append($"- Spending limit ${this.settings.SpendLimitUsd:0.00} per chat\n");
            }

            if (this.engine is ClaudeAgent { McpServers.Count: > 0 } withMcp)
            {
                sb.Append("- MCP servers: " + string.Join(", ", withMcp.McpServers.Select(s => $"{s.Name} ({(s.Tools == 1 ? "1 tool" : $"{s.Tools} tools")})")) + "\n");
            }
        }
        else
        {
            sb.Append("- Claude Code reads the CLAUDE.md files itself\n");
        }

        sb.Append(api
            ? "\nWhen the context is nearly full, the conversation is summarized automatically. `/compact` does it now."
            : "\nClaude Code compacts the conversation automatically when it's nearly full. `/compact` does it now.");
        return sb.ToString();
    }

    private bool SetFromCommand<T>(SlashCommand command, Func<string, T?> parse, Action<T> apply)
        where T : struct
    {
        if (parse(command.Argument.ToLowerInvariant()) is not T value)
        {
            this.Status = "Usage: " + SlashCommands.All.First(c => c.Name == command.Name).Usage;
            return false;
        }

        apply(value);
        return true;
    }
}
