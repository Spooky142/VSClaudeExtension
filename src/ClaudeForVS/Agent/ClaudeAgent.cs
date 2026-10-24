using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.Git;
using ApiEffort = Anthropic.Models.Messages.Effort;

namespace ClaudeForVS.Agent;

/// <summary>
/// A single conversation with Claude about one git repository. Runs a manual, streaming
/// tool-use loop: stream a response, execute any requested tools, send results, repeat.
/// History is append-only so the prompt cache stays valid across turns.
/// </summary>
internal sealed partial class ClaudeAgent : IChatEngine
{
    private const int MaxToolRounds = 40;
    private const int MaxFileChars = 100_000;
    private const int PreviewLines = 60;
    private const int DefaultCommandSeconds = 300;
    private const int MaxCommandSeconds = 1800;

    // A running command's live view (in the Claude Tasks window) shows this many of its latest output lines, refreshed this often.
    private const int LiveOutputLines = 200;
    private static readonly TimeSpan LiveOutputInterval = TimeSpan.FromMilliseconds(250);

    // Web searches and fetches allowed per response; each search is billed on top of tokens.
    private const int MaxWebUses = 8;

    private const long MaxOutputTokens = 64000;

    // At this share of the context window the conversation is summarized and restarted from the summary.
    private const double CompactAt = 0.95;

    // Input plus max_tokens has to fit in the window, so compact earlier if a full-length reply (plus this much
    // headroom for the next prompt or tool results) would no longer fit.
    private const long CompactHeadroom = 8_000;

    private const string CompactInstruction =
        """
        The conversation is close to the context limit, so it will be replaced by a summary that you write now. Don't call
        any tools and don't continue the task. Write a summary that lets you carry on seamlessly: what the user has asked
        for, decisions made, the files involved and what was changed in each, anything still pending or in progress
        (including the step you were in the middle of), and any preferences the user has expressed. Be specific: include
        paths, names and key code details, since this summary is all that will remain.
        """;

    private const string PlanModeRefusal =
        "Plan mode is on, so no changes can be made. Present your suggestions as text; the user can switch modes in Settings.";

    private const string SettingsRefusal =
        "The user's permission settings (.claude/settings.json) don't allow this, so it wasn't done. Don't try to get around that; ask the user if it matters.";

    // How long an MCP server gets to start and list its tools.
    private static readonly TimeSpan McpStartTimeout = TimeSpan.FromSeconds(30);

    private readonly AnthropicClient client;
    private readonly RateLimitHeaderHandler rateLimits = new();
    private readonly List<MessageParam> history = [];
    private readonly RepoFiles files;
    private readonly string systemPrompt;
    private readonly PermissionRules permissions;
    private bool editsPreApproved;

    // Fixed once the first message is sent (built-in tools plus MCP servers' tools), so the prompt cache stays valid.
    private List<ToolUnion>? tools;
    private readonly List<McpClient> mcpClients = [];
    private readonly Dictionary<string, (McpClient Client, McpTool Tool)> mcpTools = new(StringComparer.Ordinal);

    // Tokens in the conversation as of the latest response; 0 until the first one (or after compacting).
    private long contextTokens;

    // Bumped whenever the history is replaced wholesale, so checkpoints taken before then can't be rewound to.
    private int generation;

    // The mode Claude was last told about in the conversation; null when it needs telling again.
    private AgentMode? announcedMode;

    private readonly string instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <param name="baseUrl">The API's address; only tests give one, to talk to a fake server.</param>
    public ClaudeAgent(string apiKey, string repoRoot, ClaudeModel model, EffortLevel effort, AgentMode mode, string? baseUrl = null)
    {
        this.client = baseUrl is null
            ? new AnthropicClient { ApiKey = apiKey, Handlers = [this.rateLimits] }
            : new AnthropicClient { ApiKey = apiKey, Handlers = [this.rateLimits], BaseUrl = baseUrl, MaxRetries = 0 };

        this.RepoRoot = repoRoot;
        this.Model = model;
        this.ModelId = AgentSettings.ModelId(model);
        this.Effort = effort;
        this.Mode = mode;
        this.files = new RepoFiles(repoRoot);
        this.systemPrompt =
            $"""
            You are Claude, working inside a Visual Studio extension as a coding assistant for the git repository at:
            {repoRoot}

            Tools:
            - `git` runs git commands. Read-only commands run immediately. Commands that change the repository or talk
              to a remote (commit, checkout, reset, push, merge, stash, etc.) need the user's approval first.
            - `glob` finds files by name pattern and `grep` searches file contents with a regular expression. Both
              cover tracked and untracked files but skip git-ignored ones. Prefer them to `git ls-files`/`git grep`.
            - `read_file` reads a file with line numbers.
            - `edit_file` replaces an exact string in a file; `write_file` creates or overwrites a whole file.
              The user approves edits before they're applied. Prefer `edit_file` for changes to existing files.
            - `delete_file` deletes a file and `move_file` moves or renames one, with the same approval as edits.
            - `run_command` runs a Windows PowerShell command in the repository, e.g. to build or run the tests after
              making changes. The user approves commands before they run.
            - `get_debug_state` reads Visual Studio's debugger: the exception, call stack and locals when the user has
              paused at a breakpoint or exception. Use it when they ask about a bug they're debugging.
            - `run_tests` runs the tests and reports the counts and each failure compactly; prefer it to `run_command`
              for running tests. The user approves test runs.
            - `get_diagnostics` lists Visual Studio's Error List: build errors and the live (IntelliSense) errors in
              open files. Check it after changing code. `build_solution` builds the open solution in Visual Studio, which
              also works for projects `dotnet build` can't handle (C++, older .NET Framework, solutions that need
              Visual Studio's MSBuild); the user approves builds.
            - `go_to_definition`, `find_references` and `find_symbols` use Visual Studio's code model (Roslyn) for C#
              and Visual Basic, which knows what a name really refers to; prefer them to `grep` for questions like
              "where is this declared" or "what calls this". `rename_symbol` renames a symbol everywhere with one
              approval; prefer it to editing each file by hand.
            - `list_packages`, `add_package` and `remove_package` manage NuGet packages through Visual Studio, so the
              project is restored; prefer them to editing a project file or running `dotnet add package`. The user
              approves package changes.
            - `task` hands a research job to a subagent with its own conversation, which reads and searches and comes
              back with one report. Use it for broad exploration so the files it reads don't fill your context; call it
              several times in one response to explore in parallel.
            - `todo_write` keeps a task list that the user sees next to the chat. Use it for work with several steps:
              write the whole list each time, with one task in progress at a time, and mark tasks completed as you go.
            - `ask_user` shows the user 1-4 multiple-choice questions. Use it when you need a decision that's genuinely
              theirs (for example choosing between approaches); the user can also type their own answer.
            - `web_search` and `web_fetch` search the web and read pages, e.g. for library documentation or an error
              message. Look at the repository first; use the web for what it can't tell you.
            - Tools named `mcp__<server>__<tool>` come from MCP servers the user has configured.
            All file paths are relative to the repository root, and files outside the repository can't be accessed.
            If the user declines an action, accept that and don't retry the same thing. The user may also approve only
            some of the changes in an edit; the result then says what was left out.

            Look at the repository before answering questions about it rather than guessing. Keep answers concise and
            use Markdown, with fenced code blocks for code. Reference files by their repository-relative paths.
            """;

        // Read once per chat: the system prompt has to stay the same for the prompt cache to work.
        (string instructions, this.InstructionFiles) = ProjectInstructions.Load(repoRoot);
        if (instructions.Length > 0)
        {
            this.systemPrompt += "\n\n" + instructions;
        }

        this.permissions = PermissionRules.Load(repoRoot);
        this.allHooks = HookRules.Load(repoRoot);
    }

    /// <summary>The CLAUDE.md files this chat follows.</summary>
    public IReadOnlyList<string> InstructionFiles { get; }

    /// <summary>The Claude Code settings files whose permission rules this chat follows.</summary>
    public IReadOnlyList<string> PermissionFiles => this.permissions.Files;

    /// <summary>The MCP servers this chat connected to, with how many tools each offers.</summary>
    public IReadOnlyList<(string Name, int Tools)> McpServers => this.mcpClients.Select(c => (c.Config.Name, c.Tools.Count)).ToList();

    public ChatEngineKind Kind => ChatEngineKind.Api;

    public string RepoRoot { get; }

    /// <summary>Fixed for the conversation: thinking blocks in the history only make sense to the model that wrote them.</summary>
    public ClaudeModel Model { get; }

    /// <summary>The API model ID sent for <see cref="Model"/>; the built-in version unless set to a newer one.</summary>
    public string ModelId { get; init; }

    /// <summary>Can change between turns; takes effect on the next request.</summary>
    public EffortLevel Effort { get; set; }

    /// <summary>Can change between turns; takes effect on the next request.</summary>
    public AgentMode Mode { get; set; }

    /// <summary>
    /// Seeds an empty conversation from a saved transcript. Only the text of each turn comes back: thinking and
    /// tool calls aren't kept, so what Claude did is carried by its replies.
    /// </summary>
    public void RestoreHistory(IEnumerable<(bool FromUser, string Text)> turns)
    {
        this.history.Clear();
        this.generation++;
        this.announcedMode = null;
        Role? last = null;
        var text = new StringBuilder();

        void Flush()
        {
            if (last is Role role && text.Length > 0)
            {
                this.history.Add(new() { Role = role, Content = text.ToString() });
            }

            text.Clear();
        }

        foreach ((bool fromUser, string body) in turns)
        {
            if (body.Length == 0)
            {
                continue;
            }

            Role role = fromUser ? Role.User : Role.Assistant;
            if (last is null && !fromUser)
            {
                continue; // a conversation has to open with the user
            }

            if (last != role)
            {
                Flush();
                last = role;
            }
            else
            {
                text.Append("\n\n");
            }

            text.Append(body);
        }

        Flush();
    }

    /// <summary>"instance:generation:message count"; only meaningful to this engine instance.</summary>
    public string? Checkpoint => $"{this.instanceId}:{this.generation}:{this.history.Count}";

    public bool Rewind(string checkpoint)
    {
        string[] parts = checkpoint.Split(':');
        if (parts.Length != 3 || parts[0] != this.instanceId || !int.TryParse(parts[1], out int gen) || !int.TryParse(parts[2], out int count)
            || gen != this.generation || count > this.history.Count)
        {
            return false;
        }

        this.history.RemoveRange(count, this.history.Count - count);
        this.announcedMode = null; // the note may have been in what was removed; repeating it is harmless
        return true;
    }

    public async Task CompactAsync(IChatSink sink, CancellationToken cancellationToken)
    {
        if (this.history.Count == 0)
        {
            sink.SetStatus("Nothing to compact yet.");
            return;
        }

        await this.CompactIfNeededAsync(sink, midTask: false, cancellationToken, force: true);
        sink.SetStatus(null);
    }

    public async Task SendAsync(ChatPrompt prompt, IChatSink sink, CancellationToken cancellationToken)
    {
        await this.EnsureToolsAsync(sink, cancellationToken);
        await this.EnsureHooksAsync(sink, cancellationToken);

        string? hookContext = null;
        if (await this.RunPromptHooksAsync(prompt.Text, sink, cancellationToken, text => hookContext = text) is string blockedBy)
        {
            sink.ToolActivity(ActivityKind.Declined, "A hook blocked your message", blockedBy);
            sink.AppendText($"_A hook blocked this message: {blockedBy}_");
            return;
        }

        // Finished responses so far; the one streaming is added on top, live.
        long promptTokens = await this.CompactIfNeededAsync(sink, midTask: false, cancellationToken);
        string? modeNote = this.ModeNote();
        if (prompt.Images.Count == 0 && modeNote is null && hookContext is null)
        {
            this.history.Add(new() { Role = Role.User, Content = prompt.Text });
        }
        else
        {
            var content = new List<ContentBlockParam>();
            foreach (PromptImage image in prompt.Images)
            {
                content.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = image.Base64, MediaType = ToMediaType(image.MediaType) } });
            }

            content.Add(new TextBlockParam { Text = prompt.Text });
            if (hookContext != null)
            {
                content.Add(new TextBlockParam { Text = $"[Context from a hook in the user's settings: {hookContext}]" });
            }

            if (modeNote != null)
            {
                content.Add(new TextBlockParam { Text = modeNote });
            }

            this.history.Add(new() { Role = Role.User, Content = content });
        }

        // A response the API paused mid-turn (during long web searches); the next request continues it.
        List<ContentBlockParam>? paused = null;
        int stopContinuations = 0;
        for (int round = 0; round < MaxToolRounds; round++)
        {
            if (round > 0 && paused is null)
            {
                promptTokens += await this.CompactIfNeededAsync(sink, midTask: true, cancellationToken);
            }

            if (!await this.WithinSpendLimitAsync(sink, cancellationToken))
            {
                return;
            }

            sink.SetStatus("Waiting for Claude…");
            StreamedTurn turn = await this.StreamTurnAsync(sink, promptTokens, cancellationToken);
            if (paused != null)
            {
                // The continuation belongs to the same assistant message.
                this.history.RemoveAt(this.history.Count - 1);
                turn.Content.InsertRange(0, paused);
                paused = null;
            }

            this.history.Add(new() { Role = Role.Assistant, Content = turn.Content });
            promptTokens += turn.InputTokens + turn.OutputTokens;
            sink.ReportLiveTokens(promptTokens);
            sink.SetStatus(null); // token counts are shown below the reply, not in the footer
            this.ReportUsage(sink, new TokenUsage(turn.UncachedInputTokens, turn.CacheWriteTokens, turn.CacheReadTokens, turn.OutputTokens, turn.WebSearches));
            this.contextTokens = turn.UncachedInputTokens + turn.CacheWriteTokens + turn.CacheReadTokens + turn.OutputTokens;
            sink.ReportContext(this.contextTokens);
            if (this.rateLimits.Latest is ApiRateLimits limits)
            {
                sink.ReportApiRateLimits(limits);
            }

            if (turn.StopReason == "refusal")
            {
                sink.AppendText("\n\n_Claude declined to continue with this request._");
                return;
            }

            if (turn.StopReason == "pause_turn")
            {
                paused = turn.Content;
                continue;
            }

            if (turn.StopReason == "max_tokens")
            {
                sink.AppendText("\n\n_Response hit the output token limit and was cut off._");
            }

            if (turn.ToolUses.Count == 0)
            {
                // A Stop hook can say Claude isn't done; it's allowed to a few times in a row.
                if (turn.StopReason != "max_tokens" && stopContinuations < MaxStopContinuations
                    && await this.RunStopHooksAsync(stopContinuations > 0, sink, cancellationToken) is string carryOn)
                {
                    stopContinuations++;
                    this.history.Add(new() { Role = Role.User, Content = carryOn });
                    continue;
                }

                return;
            }

            // Every tool_use needs a matching tool_result in a single user message, even on cancel.
            var results = new List<ContentBlockParam>();
            List<ToolUseBlockParam> uses = turn.ToolUses;
            for (int i = 0; i < uses.Count;)
            {
                if (cancellationToken.IsCancellationRequested || turn.StopReason == "max_tokens")
                {
                    results.Add(Error(uses[i].ID, cancellationToken.IsCancellationRequested
                        ? "Cancelled by the user."
                        : "Tool input was truncated because the response hit max_tokens; retry with a smaller input."));
                    i++;
                    continue;
                }

                // Read-only tools that Claude asked for together run at the same time.
                int end = i;
                while (end < uses.Count && CanRunInParallel(uses[end]))
                {
                    end++;
                }

                if (end - i > 1)
                {
                    foreach (ToolResultBlockParam result in await this.RunInParallelAsync(uses.GetRange(i, end - i), sink, cancellationToken))
                    {
                        results.Add(result);
                    }

                    i = end;
                }
                else
                {
                    results.Add(await this.RunToolAsync(uses[i], sink, cancellationToken));
                    i++;
                }
            }

            if (this.ModeNote() is string note)
            {
                results.Add(new TextBlockParam { Text = note }); // the mode changed while Claude was working
            }

            this.history.Add(new() { Role = Role.User, Content = results });
            cancellationToken.ThrowIfCancellationRequested();
        }

        sink.AppendText($"\n\n_Stopped after {MaxToolRounds} tool rounds._");
    }

    public void Dispose()
    {
        this.client.Dispose();

        // Stopping a server can take a moment (it's given time to exit), so it's done off the UI thread.
        List<McpClient> servers = [.. this.mcpClients];
        this.mcpClients.Clear();
        if (servers.Count > 0)
        {
            _ = Task.Run(() => servers.ForEach(s => s.Dispose()));
        }
    }
}

internal sealed class ToolInputException(string message) : Exception(message);
