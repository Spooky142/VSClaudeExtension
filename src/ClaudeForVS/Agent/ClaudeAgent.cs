using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Git;
using ApiEffort = Anthropic.Models.Messages.Effort;

namespace ClaudeForVS.Agent;

/// <summary>
/// A single conversation with Claude about one git repository. Runs a manual, streaming
/// tool-use loop: stream a response, execute any requested tools, send results, repeat.
/// History is append-only so the prompt cache stays valid across turns.
/// </summary>
internal sealed class ClaudeAgent : IChatEngine
{
    private const int MaxToolRounds = 40;
    private const int MaxFileChars = 100_000;
    private const int PreviewLines = 60;
    private const int DefaultCommandSeconds = 300;
    private const int MaxCommandSeconds = 1800;

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

    private readonly AnthropicClient client;
    private readonly RateLimitHeaderHandler rateLimits = new();
    private readonly List<MessageParam> history = [];
    private readonly RepoFiles files;
    private readonly string systemPrompt;
    private bool editsPreApproved;

    // Tokens in the conversation as of the latest response; 0 until the first one (or after compacting).
    private long contextTokens;

    // Bumped whenever the history is replaced wholesale, so checkpoints taken before then can't be rewound to.
    private int generation;

    private readonly string instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

    public ClaudeAgent(string apiKey, string repoRoot, ClaudeModel model, EffortLevel effort, AgentMode mode)
    {
        this.client = new AnthropicClient { ApiKey = apiKey, Handlers = [this.rateLimits] };
        this.RepoRoot = repoRoot;
        this.Model = model;
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
            - `read_file` reads a file with line numbers.
            - `edit_file` replaces an exact string in a file; `write_file` creates or overwrites a whole file.
              The user approves edits before they're applied. Prefer `edit_file` for changes to existing files.
            - `delete_file` deletes a file and `move_file` moves or renames one, with the same approval as edits.
            - `run_command` runs a Windows PowerShell command in the repository, e.g. to build or run the tests after
              making changes. The user approves commands before they run.
            - `ask_user` shows the user 1-4 multiple-choice questions. Use it when you need a decision that's genuinely
              theirs (for example choosing between approaches); the user can also type their own answer.
            All file paths are relative to the repository root, and files outside the repository can't be accessed.
            If the user declines an action, accept that and don't retry the same thing.

            Look at the repository before answering questions about it rather than guessing. Keep answers concise and
            use Markdown, with fenced code blocks for code. Reference files by their repository-relative paths.
            """;

        // Read once per chat: the system prompt has to stay the same for the prompt cache to work.
        (string instructions, this.InstructionFiles) = ProjectInstructions.Load(repoRoot);
        if (instructions.Length > 0)
        {
            this.systemPrompt += "\n\n" + instructions;
        }
    }

    /// <summary>The CLAUDE.md files this chat follows.</summary>
    public IReadOnlyList<string> InstructionFiles { get; }

    public ChatEngineKind Kind => ChatEngineKind.Api;

    public string RepoRoot { get; }

    /// <summary>Fixed for the conversation: thinking blocks in the history only make sense to the model that wrote them.</summary>
    public ClaudeModel Model { get; }

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
        // Finished responses so far; the one streaming is added on top, live.
        long promptTokens = await this.CompactIfNeededAsync(sink, midTask: false, cancellationToken);
        if (prompt.Images.Count == 0)
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
            this.history.Add(new() { Role = Role.User, Content = content });
        }

        for (int round = 0; round < MaxToolRounds; round++)
        {
            if (round > 0)
            {
                promptTokens += await this.CompactIfNeededAsync(sink, midTask: true, cancellationToken);
            }

            sink.SetStatus("Waiting for Claude…");
            StreamedTurn turn = await this.StreamTurnAsync(sink, promptTokens, cancellationToken);
            this.history.Add(new() { Role = Role.Assistant, Content = turn.Content });
            promptTokens += turn.InputTokens + turn.OutputTokens;
            sink.ReportLiveTokens(promptTokens);
            sink.SetStatus(null); // token counts are shown below the reply, not in the footer
            sink.ReportTokenUsage(this.Model, new TokenUsage(turn.UncachedInputTokens, turn.CacheWriteTokens, turn.CacheReadTokens, turn.OutputTokens));
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

            if (turn.StopReason == "max_tokens")
            {
                sink.AppendText("\n\n_Response hit the output token limit and was cut off._");
            }

            if (turn.ToolUses.Count == 0)
            {
                return;
            }

            // Every tool_use needs a matching tool_result in a single user message, even on cancel.
            var results = new List<ContentBlockParam>();
            foreach (ToolUseBlockParam toolUse in turn.ToolUses)
            {
                if (cancellationToken.IsCancellationRequested || turn.StopReason == "max_tokens")
                {
                    results.Add(Error(toolUse.ID, cancellationToken.IsCancellationRequested
                        ? "Cancelled by the user."
                        : "Tool input was truncated because the response hit max_tokens; retry with a smaller input."));
                    continue;
                }

                try
                {
                    string output = await this.ExecuteToolAsync(toolUse, sink, cancellationToken);
                    results.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = output });
                }
                catch (OperationCanceledException)
                {
                    results.Add(Error(toolUse.ID, "Cancelled by the user."));
                }
                catch (Exception ex) when (ex is ToolInputException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    sink.ToolActivity(ActivityKind.Failed, $"{toolUse.Name} failed", ex.Message);
                    results.Add(Error(toolUse.ID, ex.Message));
                }
            }

            this.history.Add(new() { Role = Role.User, Content = results });
            cancellationToken.ThrowIfCancellationRequested();
        }

        sink.AppendText($"\n\n_Stopped after {MaxToolRounds} tool rounds._");
    }

    public void Dispose() => this.client.Dispose();

    /// <summary>
    /// Once the context passes <see cref="CompactAt"/> (or a full-length reply would no longer fit), has Claude summarize the conversation and restarts it from that
    /// summary. The request reuses the normal request with the instruction appended, so the history is read from the
    /// cache. Returns the new tokens it used (counted towards the prompt's total), or 0 if it didn't compact.
    /// </summary>
    private async Task<long> CompactIfNeededAsync(IChatSink sink, bool midTask, CancellationToken cancellationToken, bool force = false)
    {
        long window = AgentSettings.ContextWindow(this.Model);
        double threshold = Math.Min(window * CompactAt, window - MaxOutputTokens - CompactHeadroom);
        if ((!force && this.contextTokens < threshold) || this.history.Count == 0)
        {
            return 0;
        }

        sink.SetStatus(force ? "Summarizing the conversation…" : "Context is nearly full · summarizing the conversation…");

        // The reply has to fit in what's left of the window.
        long maxTokens = Math.Max(4_000, Math.Min(16_000, window - this.contextTokens - 2_000));
        MessageCreateParams request = this.BuildRequest(new() { Role = Role.User, Content = CompactInstruction }, maxTokens);

        var summary = new StringBuilder();
        long newTokens = 0;
        await foreach (RawMessageStreamEvent ev in this.client.Messages.CreateStreaming(request, cancellationToken))
        {
            if (ev.TryPickStart(out RawMessageStartEvent? start))
            {
                newTokens += start.Message.Usage.InputTokens + (start.Message.Usage.CacheCreationInputTokens ?? 0);
                sink.ReportTokenUsage(this.Model, new TokenUsage(
                    start.Message.Usage.InputTokens,
                    start.Message.Usage.CacheCreationInputTokens ?? 0,
                    start.Message.Usage.CacheReadInputTokens ?? 0,
                    0));
            }
            else if (ev.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta) && delta.Delta.TryPickText(out TextDelta? text))
            {
                summary.Append(text.Text);
            }
            else if (ev.TryPickDelta(out RawMessageDeltaEvent? messageDelta))
            {
                newTokens += messageDelta.Usage.OutputTokens;
                sink.ReportTokenUsage(this.Model, new TokenUsage(0, 0, 0, messageDelta.Usage.OutputTokens));
            }
        }

        if (summary.Length == 0)
        {
            return newTokens; // nothing to restart from; carry on with the full history
        }

        long before = this.contextTokens;
        this.history.Clear();
        this.generation++;
        this.history.Add(new()
        {
            Role = Role.User,
            Content = "This conversation was compacted to free up context. Summary of everything before this point:\n\n"
                + summary.ToString().Trim()
                + (midTask ? "\n\nContinue the task from where you left off." : string.Empty),
        });
        this.contextTokens = 0;
        sink.ReportCompacted(before);
        return newTokens;
    }

    private static ToolResultBlockParam Error(string id, string message)
        => new() { ToolUseID = id, Content = message, IsError = true };

    /// <param name="extra">A message to send after the history without adding it to the history.</param>
    /// <param name="maxTokens">Overrides the output limit; doesn't affect caching.</param>
    private MessageCreateParams BuildRequest(MessageParam? extra = null, long maxTokens = MaxOutputTokens)
    {
        bool haiku = this.Model == ClaudeModel.Haiku;
        int? haikuBudget = AgentSettings.HaikuThinkingBudget(this.Effort);

        return new MessageCreateParams
        {
            Model = AgentSettings.ModelId(this.Model),
            MaxTokens = maxTokens,

            // Haiku 4.5 rejects `effort` and adaptive thinking; it takes an explicit thinking budget instead.
            // The budget has to be below the output limit, which compaction lowers.
            Thinking = haiku ? HaikuThinking(haikuBudget < maxTokens ? haikuBudget : null) :new ThinkingConfigAdaptive { Display = Display.Summarized },
            OutputConfig = haiku ? null : new OutputConfig { Effort = ToApiEffort(this.Effort) },

            // Automatic caching: the breakpoint moves forward with the conversation.
            CacheControl = new CacheControlEphemeral(),
            System = this.systemPrompt + "\n" + AgentSettings.ModeInstructions(this.Mode),
            Tools = ToolDefinitions,
            Messages = extra is null ? [.. this.history] : [.. this.history, extra],
        };
    }

    /// <summary>No budget means thinking off: leave the parameter out rather than converting a null config.</summary>
    private static ThinkingConfigParam? HaikuThinking(int? budget)
    {
        if (budget is not int tokens)
        {
            return null;
        }

        return new ThinkingConfigEnabled { BudgetTokens = tokens };
    }

    private static MediaType ToMediaType(string mediaType) => mediaType switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImagePng,
    };

    private static ApiEffort ToApiEffort(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => ApiEffort.Low,
        EffortLevel.Medium => ApiEffort.Medium,
        EffortLevel.XHigh => ApiEffort.Xhigh,
        EffortLevel.Max => ApiEffort.Max,
        _ => ApiEffort.High,
    };

    private async Task<StreamedTurn> StreamTurnAsync(IChatSink sink, long tokensBefore, CancellationToken cancellationToken)
    {
        var blocks = new SortedDictionary<long, BlockBuilder>();
        var turn = new StreamedTurn();
        long streamedChars = 0; // output isn't counted until the response ends, so estimate it (~4 chars per token) meanwhile

        await foreach (RawMessageStreamEvent ev in this.client.Messages.CreateStreaming(this.BuildRequest(), cancellationToken))
        {
            if (ev.TryPickStart(out RawMessageStartEvent? start))
            {
                // New input only: the history re-read from the cache on every round isn't counted again.
                turn.InputTokens = start.Message.Usage.InputTokens
                    + (start.Message.Usage.CacheCreationInputTokens ?? 0);
                turn.CacheReadTokens = start.Message.Usage.CacheReadInputTokens ?? 0;
                turn.CacheWriteTokens = start.Message.Usage.CacheCreationInputTokens ?? 0;
                turn.UncachedInputTokens = start.Message.Usage.InputTokens;
                sink.ReportLiveTokens(tokensBefore + turn.InputTokens);

                // The headers and input size are known now, so the strip needn't wait for the reply to finish.
                sink.ReportContext(turn.UncachedInputTokens + turn.CacheWriteTokens + turn.CacheReadTokens);
                if (this.rateLimits.Latest is ApiRateLimits limits)
                {
                    sink.ReportApiRateLimits(limits);
                }
            }
            else if (ev.TryPickContentBlockStart(out RawContentBlockStartEvent? blockStart))
            {
                var b = new BlockBuilder();
                if (blockStart.ContentBlock.TryPickText(out _))
                {
                    b.Kind = BlockKind.Text;
                }
                else if (blockStart.ContentBlock.TryPickThinking(out _))
                {
                    b.Kind = BlockKind.Thinking;
                    sink.SetStatus("Thinking…");
                }
                else if (blockStart.ContentBlock.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                {
                    b.Kind = BlockKind.RedactedThinking;
                    b.Data = redacted.Data;
                }
                else if (blockStart.ContentBlock.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    b.Kind = BlockKind.ToolUse;
                    b.ToolId = toolUse.ID;
                    b.ToolName = toolUse.Name;
                    sink.SetStatus($"Preparing {toolUse.Name}…");
                }

                blocks[blockStart.Index] = b;
            }
            else if (ev.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta)
                && blocks.TryGetValue(delta.Index, out BlockBuilder? block))
            {
                if (delta.Delta.TryPickText(out TextDelta? text))
                {
                    block.Text.Append(text.Text);
                    sink.AppendText(text.Text);
                }
                else if (delta.Delta.TryPickThinking(out ThinkingDelta? thinking))
                {
                    block.Text.Append(thinking.Thinking);
                    sink.AppendThinking(thinking.Thinking);
                }
                else if (delta.Delta.TryPickSignature(out SignatureDelta? signature))
                {
                    block.Signature += signature.Signature;
                }
                else if (delta.Delta.TryPickInputJson(out InputJsonDelta? json))
                {
                    block.Text.Append(json.PartialJson);
                }

                streamedChars += delta.Delta.TryPickText(out TextDelta? t) ? t.Text.Length
                    : delta.Delta.TryPickThinking(out ThinkingDelta? th) ? th.Thinking.Length
                    : delta.Delta.TryPickInputJson(out InputJsonDelta? ij) ? ij.PartialJson.Length
                    : 0;
                sink.ReportLiveTokens(tokensBefore + turn.InputTokens + (streamedChars / 4));
            }
            else if (ev.TryPickDelta(out RawMessageDeltaEvent? messageDelta))
            {
                turn.StopReason = messageDelta.Delta.StopReason?.Raw();
                turn.OutputTokens = messageDelta.Usage.OutputTokens;
            }
        }

        foreach (BlockBuilder b in blocks.Values)
        {
            switch (b.Kind)
            {
                case BlockKind.Text when b.Text.Length > 0:
                    turn.Content.Add(new TextBlockParam { Text = b.Text.ToString() });
                    break;
                case BlockKind.Thinking:
                    // Echo thinking back unchanged (signature included) so the model keeps its reasoning.
                    turn.Content.Add(new ThinkingBlockParam { Thinking = b.Text.ToString(), Signature = b.Signature });
                    break;
                case BlockKind.RedactedThinking:
                    turn.Content.Add(new RedactedThinkingBlockParam { Data = b.Data });
                    break;
                case BlockKind.ToolUse:
                    var toolUse = new ToolUseBlockParam { ID = b.ToolId, Name = b.ToolName, Input = ParseToolInput(b.Text.ToString()) };
                    turn.Content.Add(toolUse);
                    turn.ToolUses.Add(toolUse);
                    break;
            }
        }

        return turn;
    }

    /// <summary>
    /// Tools stream their input eagerly, so the JSON may be incomplete if the response was cut off.
    /// Invalid input becomes an empty object; the tool then reports the missing arguments as an error.
    /// </summary>
    private static Dictionary<string, JsonElement> ParseToolInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task<string> ExecuteToolAsync(ToolUseBlockParam toolUse, IChatSink sink, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonElement> input = toolUse.Input;
        return toolUse.Name switch
        {
            "git" => this.RunGitAsync(input, sink, cancellationToken),
            "read_file" => Task.FromResult(this.ReadFile(input, sink)),
            "edit_file" => this.EditFileAsync(input, sink, cancellationToken),
            "write_file" => this.WriteFileAsync(input, sink, cancellationToken),
            "delete_file" => this.DeleteFileAsync(input, sink, cancellationToken),
            "move_file" => this.MoveFileAsync(input, sink, cancellationToken),
            "run_command" => this.RunCommandAsync(input, sink, cancellationToken),
            "ask_user" => AskUserAsync(toolUse.Input, sink, cancellationToken),
            _ => throw new ToolInputException($"Unknown tool: {toolUse.Name}"),
        };
    }

    private async Task<string> RunGitAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        if (!input.TryGetValue("args", out JsonElement argsEl) || argsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ToolInputException("`args` must be an array of strings.");
        }

        List<string> args = argsEl.EnumerateArray()
            .Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : throw new ToolInputException("`args` must contain only strings."))
            .ToList();
        if (args.Count == 0)
        {
            throw new ToolInputException("`args` must not be empty.");
        }

        string commandLine = "git " + string.Join(" ", args.Select(GitRunner.QuoteArgument));
        if (!GitRunner.IsReadOnly(args) && this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, commandLine, "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (!GitRunner.IsReadOnly(args) && this.Mode == AgentMode.Coding && !CommandRules.IsAllowed(this.RepoRoot, commandLine, viaShell: false))
        {
            sink.SetStatus("Waiting for your approval…");
            string? rule = CommandRules.RuleFor(commandLine, viaShell: false);
            Approval approval = await sink.RequestApprovalAsync(ActivityKind.Git, commandLine, string.Empty, rule is null ? null : $"Always allow \"{rule}\" here", cancellationToken);
            if (approval == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, commandLine, "Declined by you.");
                return "The user declined to run this command.";
            }

            if (approval == Approval.AllowAll && rule != null)
            {
                CommandRules.Allow(this.RepoRoot, rule);
            }
        }

        sink.SetStatus($"Running {commandLine}");
        (int exitCode, string output) = await GitRunner.RunAsync(this.RepoRoot, args, cancellationToken);
        sink.ToolActivity(ActivityKind.Git, commandLine, output.Length == 0 ? "(no output)" : output);
        return exitCode == 0
            ? (output.Length == 0 ? "(no output)" : output)
            : $"git exited with code {exitCode}.\n{output}";
    }

    private string ReadFile(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink)
    {
        string path = RequireString(input, "path");
        RepoFiles.TextFile file = this.files.Read(path);
        string[] lines = file.Text.Replace("\r\n", "\n").Split('\n');
        int startLine = OptionalInt(input, "start_line") is int s ? Math.Max(1, s) : 1;
        int endLine = OptionalInt(input, "end_line") is int e ? Math.Min(lines.Length, e) : lines.Length;

        var sb = new StringBuilder();
        for (int i = startLine; i <= endLine; i++)
        {
            sb.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
            if (sb.Length > MaxFileChars)
            {
                sb.Append($"[truncated at line {i} of {lines.Length}; use start_line/end_line to read more]\n");
                break;
            }
        }

        sink.ToolActivity(ActivityKind.Read, $"Read {this.files.Relative(file.FullPath)}", $"lines {startLine}-{endLine} of {lines.Length}");
        return sb.Length == 0 ? "(empty file)" : sb.ToString();
    }

    private async Task<string> EditFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string path = RequireString(input, "path");
        string oldString = RequireString(input, "old_string");
        string newString = RequireString(input, "new_string", allowEmpty: true);
        bool replaceAll = input.TryGetValue("replace_all", out JsonElement ra) && ra.ValueKind == JsonValueKind.True;
        if (oldString.Length == 0)
        {
            throw new ToolInputException("`old_string` must not be empty. Use write_file to create a file.");
        }

        RepoFiles.TextFile file = this.files.Read(path);
        string find = RepoFiles.NormalizeNewlines(oldString, file.Newline);
        string replace = RepoFiles.NormalizeNewlines(newString, file.Newline);

        int count = CountOccurrences(file.Text, find);
        if (count == 0)
        {
            throw new ToolInputException($"`old_string` was not found in {path}. Read the file again and copy the text exactly (without line numbers).");
        }

        if (count > 1 && !replaceAll)
        {
            throw new ToolInputException($"`old_string` appears {count} times in {path}. Include more surrounding context to make it unique, or set replace_all.");
        }

        string relative = this.files.Relative(file.FullPath);
        string diff = Diff(oldString, newString) + (count > 1 ? $"\n({count} occurrences)" : string.Empty);
        string updated = replaceAll ? file.Text.Replace(find, replace) : ReplaceFirst(file.Text, find, replace);
        var change = new FileChange(file.FullPath, file.Text, updated);
        if (!await this.ApproveEditAsync(ActivityKind.Edit, $"Edit {relative}", diff, change, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"Edit {relative}", "Declined by you.");
            return "The user declined this edit.";
        }

        this.files.Write(file.FullPath, updated, file.Encoding);
        sink.ToolActivity(ActivityKind.Edit, $"Edited {relative}", diff, change);
        return $"Edited {relative} ({count} replacement{(count == 1 ? "" : "s")}).";
    }

    private async Task<string> WriteFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string path = RequireString(input, "path");
        string content = RequireString(input, "content", allowEmpty: true);
        string full = this.files.Resolve(path);
        string relative = this.files.Relative(full);

        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string newline = "\r\n";
        string action = "Create";
        string before = string.Empty;
        if (File.Exists(full))
        {
            RepoFiles.TextFile existing = this.files.Read(path);
            encoding = existing.Encoding;
            newline = existing.Newline;
            before = existing.Text;
            action = "Overwrite";
        }
        else if (Directory.Exists(full))
        {
            throw new ToolInputException($"{path} is a directory.");
        }

        string preview = Preview(content);
        string after = RepoFiles.NormalizeNewlines(content, newline);
        var change = new FileChange(full, before, after);
        if (!await this.ApproveEditAsync(ActivityKind.Create, $"{action} {relative}", preview, change, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"{action} {relative}", "Declined by you.");
            return "The user declined this change.";
        }

        this.files.Write(full, after, encoding);
        sink.ToolActivity(ActivityKind.Create, action == "Create" ? $"Created {relative}" : $"Overwrote {relative}", preview, change);
        return $"{(action == "Create" ? "Created" : "Overwrote")} {relative}.";
    }

    private async Task<string> DeleteFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string path = RequireString(input, "path");
        string full = this.files.Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ToolInputException($"{path} is a directory; only files can be deleted.");
        }

        if (!File.Exists(full))
        {
            throw new ToolInputException($"File not found: {path}");
        }

        // Binary files can be deleted too, just without a preview.
        string? before = null;
        try
        {
            before = this.files.Read(path).Text;
        }
        catch (ToolInputException)
        {
        }

        string relative = this.files.Relative(full);
        string preview = before is null ? "(binary file)" : Removed(before);
        FileChange? change = before is null ? null : new FileChange(full, before, string.Empty);
        if (!await this.ApproveEditAsync(ActivityKind.Edit, $"Delete {relative}", preview, change, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"Delete {relative}", "Declined by you.");
            return "The user declined to delete this file.";
        }

        File.Delete(full);
        sink.ToolActivity(ActivityKind.Edit, $"Deleted {relative}", preview, change);
        return $"Deleted {relative}.";
    }

    private async Task<string> MoveFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string from = RequireString(input, "from");
        string to = RequireString(input, "to");
        string source = this.files.Resolve(from);
        string destination = this.files.Resolve(to);
        if (Directory.Exists(source))
        {
            throw new ToolInputException($"{from} is a directory; only files can be moved.");
        }

        if (!File.Exists(source))
        {
            throw new ToolInputException($"File not found: {from}");
        }

        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            throw new ToolInputException("`from` and `to` are the same path.");
        }

        // A change of case only is a rename of the same file, so the destination "exists" already.
        bool caseOnly = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new ToolInputException($"{to} already exists. Delete it first if it should be replaced.");
        }

        string title = $"{this.files.Relative(source)} → {this.files.Relative(destination)}";
        if (!await this.ApproveEditAsync(ActivityKind.Edit, "Move " + title, string.Empty, null, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, "Move " + title, "Declined by you.");
            return "The user declined to move this file.";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        sink.ToolActivity(ActivityKind.Edit, "Moved " + title, string.Empty);
        return $"Moved {title}.";
    }

    private async Task<string> RunCommandAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string command = RequireString(input, "command").Trim();
        int timeout = Math.Max(1, Math.Min(MaxCommandSeconds, OptionalInt(input, "timeout_seconds") ?? DefaultCommandSeconds));
        string directory = this.RepoRoot;
        string where = string.Empty;
        if (input.TryGetValue("cwd", out JsonElement cwdEl) && cwdEl.ValueKind == JsonValueKind.String
            && cwdEl.GetString() is string cwd && cwd.Trim().Length > 0 && cwd.Trim() != ".")
        {
            directory = this.files.Resolve(cwd);
            if (!Directory.Exists(directory))
            {
                throw new ToolInputException($"Directory not found: {cwd}");
            }

            where = $" (in {this.files.Relative(directory)})";
        }

        if (this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, command + where, "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (this.Mode == AgentMode.Coding && !CommandRules.IsAllowed(this.RepoRoot, command, viaShell: true))
        {
            sink.SetStatus("Waiting for your approval…");
            string? rule = CommandRules.RuleFor(command, viaShell: true);
            Approval approval = await sink.RequestApprovalAsync(ActivityKind.Shell, $"Run: {command}{where}", string.Empty, rule is null ? null : $"Always allow \"{rule}\" here", cancellationToken);
            if (approval == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, command + where, "Declined by you.");
                return "The user declined to run this command.";
            }

            if (approval == Approval.AllowAll && rule != null)
            {
                CommandRules.Allow(this.RepoRoot, rule);
            }
        }

        sink.SetStatus($"Running {command}");
        (int? exitCode, string output) = await ShellRunner.RunAsync(directory, command, TimeSpan.FromSeconds(timeout), cancellationToken);
        string shown = output.Length == 0 ? "(no output)" : output;
        if (exitCode is not int code)
        {
            sink.ToolActivity(ActivityKind.Failed, command + where, $"Stopped after {timeout}s.\n{output}".TrimEnd());
            return $"The command was stopped after {timeout} seconds. Pass a larger timeout_seconds (up to {MaxCommandSeconds}) if it needs longer.\n{output}".TrimEnd();
        }

        sink.ToolActivity(code == 0 ? ActivityKind.Shell : ActivityKind.Failed, command + where, code == 0 ? shown : $"Exit code {code}\n{shown}");
        return code == 0 ? shown : $"Exit code {code}.\n{shown}";
    }

    private async Task<bool> ApproveEditAsync(ActivityKind kind, string title, string detail, FileChange? change, IChatSink sink, CancellationToken cancellationToken)
    {
        if (this.Mode == AgentMode.Plan)
        {
            throw new ToolInputException(PlanModeRefusal);
        }

        if (this.editsPreApproved || this.Mode == AgentMode.Auto)
        {
            return true;
        }

        sink.SetStatus("Waiting for your approval…");
        Approval approval = await sink.RequestApprovalAsync(kind, title, detail, "Allow all edits", cancellationToken, change);
        if (approval == Approval.AllowAll)
        {
            this.editsPreApproved = true;
        }

        return approval != Approval.Deny;
    }

    private static string RequireString(IReadOnlyDictionary<string, JsonElement> input, string name, bool allowEmpty = false)
    {
        if (!input.TryGetValue(name, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            throw new ToolInputException($"`{name}` must be a string.");
        }

        string value = el.GetString()!;
        if (!allowEmpty && value.Length == 0)
        {
            throw new ToolInputException($"`{name}` must not be empty.");
        }

        return value;
    }

    private static int? OptionalInt(IReadOnlyDictionary<string, JsonElement> input, string name)
        => input.TryGetValue(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : null;

    private static int CountOccurrences(string text, string find)
    {
        int count = 0;
        for (int i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        int i = text.IndexOf(find, StringComparison.Ordinal);
        return text.Substring(0, i) + replace + text.Substring(i + find.Length);
    }

    private static string Diff(string oldText, string newText)
    {
        var sb = new StringBuilder();
        foreach (string line in Lines(oldText).Take(PreviewLines))
        {
            sb.Append("- ").Append(line).Append('\n');
        }

        foreach (string line in Lines(newText).Take(PreviewLines))
        {
            sb.Append("+ ").Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string Preview(string content)
    {
        string[] lines = Lines(content);
        string head = string.Join("\n", lines.Take(PreviewLines));
        return lines.Length > PreviewLines ? head + $"\n… ({lines.Length - PreviewLines} more lines)" : head;
    }

    private static string Removed(string content)
    {
        string[] lines = Lines(content);
        string head = string.Join("\n", lines.Take(PreviewLines).Select(l => "- " + l));
        return lines.Length > PreviewLines ? head + $"\n… ({lines.Length - PreviewLines} more lines)" : head;
    }

    private static string[] Lines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');

    private static async Task<string> AskUserAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        JsonElement root = JsonSerializer.SerializeToElement(input);
        IReadOnlyList<UserQuestion> questions = UserQuestion.ParseAll(root);
        if (questions.Count == 0)
        {
            throw new ToolInputException("`questions` must be a non-empty array of {question, header, options[{label, description}], multiSelect}.");
        }

        sink.SetStatus("Waiting for your answer…");
        IReadOnlyDictionary<string, string>? answers = await sink.AskQuestionsAsync(questions, cancellationToken);
        string title = questions.Count == 1 ? "Asked you a question" : $"Asked you {questions.Count} questions";
        if (answers is null)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Skipped.");
            return "The user skipped these questions. Continue with your best judgement, or ask in plain text.";
        }

        string summary = UserQuestion.Describe(questions, answers);
        sink.ToolActivity(ActivityKind.Question, title, summary);
        return "The user answered:\n" + summary;
    }

    private static readonly List<ToolUnion> ToolDefinitions =
    [
        new Tool
        {
            Name = "ask_user",
            Description =
                "Ask the user 1-4 multiple-choice questions and wait for their answers. Each question has a short " +
                "header (max 12 characters), the question text, and 2-4 options with a label and a one-line description. " +
                "Set multiSelect when several options can apply. The user can always type their own answer instead. " +
                "Only ask when the decision is genuinely the user's; otherwise use your judgement.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["questions"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 4,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                question = new { type = "string", description = "The full question." },
                                header = new { type = "string", description = "Short label, max 12 characters." },
                                options = new
                                {
                                    type = "array",
                                    minItems = 2,
                                    maxItems = 4,
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            label = new { type = "string" },
                                            description = new { type = "string" },
                                        },
                                        required = new[] { "label", "description" },
                                    },
                                },
                                multiSelect = new { type = "boolean" },
                            },
                            required = new[] { "question", "header", "options", "multiSelect" },
                        },
                    }),
                },
                Required = ["questions"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "git",
            Description =
                "Run a git command in the repository. Pass the arguments after `git` as an array, e.g. " +
                "[\"log\", \"--oneline\", \"-n\", \"20\"] or [\"diff\", \"--staged\"]. Read-only commands (status, diff, log, " +
                "show, blame, grep, ls-files, branch listing, ...) run immediately; anything that modifies the repository " +
                "or contacts a remote requires the user's approval. There is no interactive terminal: never use commands " +
                "that open an editor or prompt (pass -m for commits, avoid -i/-p). Output over 60k characters is truncated.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["args"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Arguments to git, without the leading `git`.",
                    }),
                },
                Required = ["args"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "read_file",
            Description =
                "Read a text file from the working tree. Each line is prefixed with its line number and a tab; the prefix " +
                "is not part of the file. Use start_line/end_line (1-based, inclusive) for large files.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                    ["start_line"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                    ["end_line"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                },
                Required = ["path"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "edit_file",
            Description =
                "Replace an exact string in an existing file. `old_string` must match the file's text exactly (including " +
                "indentation, without read_file's line-number prefixes) and must be unique in the file unless replace_all " +
                "is true; include surrounding lines to make it unique. Read the file before editing it. The user approves " +
                "each edit before it's applied.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                    ["old_string"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Exact text to replace." }),
                    ["new_string"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Replacement text." }),
                    ["replace_all"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Replace every occurrence. Default false." }),
                },
                Required = ["path", "old_string", "new_string"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "write_file",
            Description =
                "Create a new file, or replace the entire contents of an existing one, inside the repository. Parent " +
                "directories are created as needed. Prefer edit_file for changing existing files. The user approves each " +
                "write before it's applied.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                    ["content"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The complete file contents." }),
                },
                Required = ["path", "content"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "delete_file",
            Description =
                "Delete a file inside the repository (not a directory). The user approves each deletion before it happens.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                },
                Required = ["path"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "move_file",
            Description =
                "Move or rename a file inside the repository. Parent directories of the destination are created as needed; " +
                "the destination must not exist. Nothing is staged in git. The user approves each move before it happens.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["from"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Current path, relative to the repository root." }),
                    ["to"] = JsonSerializer.SerializeToElement(new { type = "string", description = "New path, relative to the repository root." }),
                },
                Required = ["from", "to"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "run_command",
            Description =
                "Run a command line in Windows PowerShell 5.1 (not bash) in the repository, e.g. `dotnet build`, " +
                "`dotnet test`, `npm test`. Use it to build and test your changes; use the git tool for git and read_file " +
                "to read files. There is no terminal: the command can't prompt for input, and stdin is closed. Returns the " +
                "exit code and combined stdout/stderr (long output keeps the start and the end). The user approves each " +
                "command before it runs unless they've allowed it already; plain commands without pipes, redirects or " +
                "chaining can be allowed permanently, so prefer those.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["command"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The PowerShell command line." }),
                    ["cwd"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Directory to run in, relative to the repository root. Default: the root." }),
                    ["timeout_seconds"] = JsonSerializer.SerializeToElement(new { type = "integer", description = $"Stop the command after this long. Default {DefaultCommandSeconds}, max {MaxCommandSeconds}." }),
                },
                Required = ["command"],
            },
            EagerInputStreaming = true,
        },
    ];

    private enum BlockKind { Other, Text, Thinking, RedactedThinking, ToolUse }

    private sealed class BlockBuilder
    {
        public BlockKind Kind;
        public StringBuilder Text = new();
        public string Signature = string.Empty;
        public string Data = string.Empty;
        public string ToolId = string.Empty;
        public string ToolName = string.Empty;
    }

    private sealed class StreamedTurn
    {
        public List<ContentBlockParam> Content = [];
        public List<ToolUseBlockParam> ToolUses = [];
        public string? StopReason;
        public long InputTokens;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long UncachedInputTokens;
        public long OutputTokens;
    }
}

internal sealed class ToolInputException(string message) : Exception(message);
