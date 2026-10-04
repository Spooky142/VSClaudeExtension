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

    // A running command's row shows this many of its latest output lines, refreshed this often.
    private const int LiveOutputLines = 40;
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

    // The mode Claude was last told about in the conversation; null when it needs telling again.
    private AgentMode? announcedMode;

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
            - `glob` finds files by name pattern and `grep` searches file contents with a regular expression. Both
              cover tracked and untracked files but skip git-ignored ones. Prefer them to `git ls-files`/`git grep`.
            - `read_file` reads a file with line numbers.
            - `edit_file` replaces an exact string in a file; `write_file` creates or overwrites a whole file.
              The user approves edits before they're applied. Prefer `edit_file` for changes to existing files.
            - `delete_file` deletes a file and `move_file` moves or renames one, with the same approval as edits.
            - `run_command` runs a Windows PowerShell command in the repository, e.g. to build or run the tests after
              making changes. The user approves commands before they run.
            - `ask_user` shows the user 1-4 multiple-choice questions. Use it when you need a decision that's genuinely
              theirs (for example choosing between approaches); the user can also type their own answer.
            - `web_search` and `web_fetch` search the web and read pages, e.g. for library documentation or an error
              message. Look at the repository first; use the web for what it can't tell you.
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
        // Finished responses so far; the one streaming is added on top, live.
        long promptTokens = await this.CompactIfNeededAsync(sink, midTask: false, cancellationToken);
        string? modeNote = this.ModeNote();
        if (prompt.Images.Count == 0 && modeNote is null)
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
            if (modeNote != null)
            {
                content.Add(new TextBlockParam { Text = modeNote });
            }

            this.history.Add(new() { Role = Role.User, Content = content });
        }

        // A response the API paused mid-turn (during long web searches); the next request continues it.
        List<ContentBlockParam>? paused = null;
        for (int round = 0; round < MaxToolRounds; round++)
        {
            if (round > 0 && paused is null)
            {
                promptTokens += await this.CompactIfNeededAsync(sink, midTask: true, cancellationToken);
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
            sink.ReportTokenUsage(this.Model, new TokenUsage(turn.UncachedInputTokens, turn.CacheWriteTokens, turn.CacheReadTokens, turn.OutputTokens, turn.WebSearches));
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
        this.announcedMode = null;
        this.history.Add(new()
        {
            Role = Role.User,
            Content = "This conversation was compacted to free up context. Summary of everything before this point:\n\n"
                + summary.ToString().Trim()
                + (midTask ? "\n\nContinue the task from where you left off." : string.Empty)
                + "\n\n" + this.ModeNote(),
        });
        this.contextTokens = 0;
        sink.ReportCompacted(before);
        return newTokens;
    }

    /// <summary>
    /// The current mode's instructions, if Claude hasn't been told them yet or the mode has changed since. They're
    /// added to the conversation rather than the system prompt, which has to stay the same for the prompt cache.
    /// </summary>
    private string? ModeNote()
    {
        if (this.announcedMode == this.Mode)
        {
            return null;
        }

        this.announcedMode = this.Mode;
        return $"[From the Visual Studio extension, not the user: {AgentSettings.ModeInstructions(this.Mode)} This applies until you're told otherwise.]";
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

            // Automatic caching: the breakpoint moves forward with the conversation. The system prompt never changes
            // (the mode goes in the conversation instead; see ModeNote), so switching mode keeps the cache.
            CacheControl = new CacheControlEphemeral(),
            System = this.systemPrompt,
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
                else if (blockStart.ContentBlock.TryPickServerToolUse(out ServerToolUseBlock? serverTool))
                {
                    // Web search and fetch run on Anthropic's side; their results arrive as the next block.
                    b.Kind = BlockKind.ServerToolUse;
                    b.Raw = serverTool.RawData;
                    b.ToolId = RawString(serverTool.RawData, "id");
                    b.ToolName = RawString(serverTool.RawData, "name");
                    sink.SetStatus(b.ToolName == "web_fetch" ? "Fetching a web page…" : "Searching the web…");
                }
                else if (blockStart.ContentBlock.TryPickWebSearchToolResult(out WebSearchToolResultBlock? searchResult))
                {
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = searchResult.RawData;
                    this.ReportWebResult(searchResult.RawData, blocks.Values, sink);
                }
                else if (blockStart.ContentBlock.TryPickWebFetchToolResult(out WebFetchToolResultBlock? fetchResult))
                {
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = fetchResult.RawData;
                    this.ReportWebResult(fetchResult.RawData, blocks.Values, sink);
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
                turn.WebSearches = messageDelta.Usage.ServerToolUse?.WebSearchRequests ?? 0;
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
                case BlockKind.ServerToolUse:
                    // The input streams in like a client tool's; if it didn't, it came whole with the block.
                    JsonElement serverInput = b.Text.Length > 0
                        ? JsonSerializer.SerializeToElement(ParseToolInput(b.Text.ToString()))
                        : b.Raw.TryGetValue("input", out JsonElement given) ? given : JsonSerializer.SerializeToElement(new { });
                    turn.Content.Add(ServerToolUseBlockParam.FromRawUnchecked(new Dictionary<string, JsonElement>
                    {
                        ["type"] = JsonSerializer.SerializeToElement("server_tool_use"),
                        ["id"] = JsonSerializer.SerializeToElement(b.ToolId),
                        ["name"] = JsonSerializer.SerializeToElement(b.ToolName),
                        ["input"] = serverInput,
                    }));
                    break;
                case BlockKind.ServerToolResult:
                    // Sent back as received (encrypted content included) so Claude can cite the results in later turns.
                    var result = new Dictionary<string, JsonElement>();
                    foreach (string key in new[] { "type", "tool_use_id", "content" })
                    {
                        if (b.Raw.TryGetValue(key, out JsonElement value))
                        {
                            result[key] = value;
                        }
                    }

                    turn.Content.Add(RawString(b.Raw, "type") == "web_fetch_tool_result"
                        ? WebFetchToolResultBlockParam.FromRawUnchecked(result)
                        : WebSearchToolResultBlockParam.FromRawUnchecked(result));
                    break;
            }
        }

        return turn;
    }

    /// <summary>Shows a finished web search or fetch in the transcript.</summary>
    private void ReportWebResult(IReadOnlyDictionary<string, JsonElement> result, IEnumerable<BlockBuilder> blocks, IChatSink sink)
    {
        string id = RawString(result, "tool_use_id");
        BlockBuilder? call = blocks.LastOrDefault(b => b.Kind == BlockKind.ServerToolUse && b.ToolId == id);
        JsonElement input = call is null ? default
            : call.Text.Length > 0 ? JsonSerializer.SerializeToElement(ParseToolInput(call.Text.ToString()))
            : call.Raw.TryGetValue("input", out JsonElement given) ? given : default;
        (ActivityKind kind, string title, string detail) = DescribeWebResult(
            RawString(result, "type"), input, result.TryGetValue("content", out JsonElement content) ? content : default);
        sink.ToolActivity(kind, title, detail);
        sink.SetStatus("Waiting for Claude…");
    }

    /// <summary>The transcript row for a web search or fetch: its title (the query or URL) and the results or error.</summary>
    internal static (ActivityKind Kind, string Title, string Detail) DescribeWebResult(string type, JsonElement input, JsonElement content)
    {
        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

        bool fetch = type == "web_fetch_tool_result";
        string title = fetch ? $"Fetched {Str(input, "url")}".TrimEnd() : $"Searched the web: {Str(input, "query")}".TrimEnd(' ', ':');
        string error = Str(content, "error_code");
        if (error.Length > 0)
        {
            return (ActivityKind.Failed, fetch ? $"Couldn't fetch {Str(input, "url")}".TrimEnd() : $"Web search failed: {Str(input, "query")}".TrimEnd(' ', ':'), error.Replace('_', ' '));
        }

        if (fetch)
        {
            // The page is a document block; its title, when known, is the most useful thing to show.
            JsonElement document = content.ValueKind == JsonValueKind.Object && content.TryGetProperty("content", out JsonElement d) ? d : default;
            string pageTitle = Str(document, "title");
            string url = Str(content, "url") is { Length: > 0 } u ? u : Str(input, "url");
            return (ActivityKind.Web, title, pageTitle.Length > 0 ? $"{pageTitle}\n{url}" : url);
        }

        var lines = new List<string>();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement hit in content.EnumerateArray())
            {
                string hitTitle = Str(hit, "title");
                string hitUrl = Str(hit, "url");
                lines.Add(hitTitle.Length > 0 ? $"{hitTitle} — {hitUrl}" : hitUrl);
            }
        }

        return (ActivityKind.Web, title, lines.Count == 0 ? "No results." : string.Join("\n", lines));
    }

    private static string RawString(IReadOnlyDictionary<string, JsonElement> raw, string name) =>
        raw.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

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

    /// <summary>Runs one tool call, turning failures into an error result for Claude.</summary>
    private async Task<ToolResultBlockParam> RunToolAsync(ToolUseBlockParam toolUse, IChatSink sink, CancellationToken cancellationToken)
    {
        try
        {
            string output = await this.ExecuteToolAsync(toolUse, sink, cancellationToken);
            return new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = output };
        }
        catch (OperationCanceledException)
        {
            return Error(toolUse.ID, "Cancelled by the user.");
        }
        catch (Exception ex) when (ex is ToolInputException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            sink.ToolActivity(ActivityKind.Failed, $"{toolUse.Name} failed", ex.Message);
            return Error(toolUse.ID, ex.Message);
        }
    }

    /// <summary>
    /// Runs read-only tool calls at the same time, on background threads. What each reports is held back and then
    /// shown in the order Claude asked for them, so the transcript reads the same as if they'd run one by one.
    /// </summary>
    private async Task<ToolResultBlockParam[]> RunInParallelAsync(List<ToolUseBlockParam> toolUses, IChatSink sink, CancellationToken cancellationToken)
    {
        sink.SetStatus($"Running {toolUses.Count} read-only tools…");
        List<DeferredSink> deferred = toolUses.Select(_ => new DeferredSink()).ToList();
        ToolResultBlockParam[] results = await Task.WhenAll(
            toolUses.Select((toolUse, i) => Task.Run(() => this.RunToolAsync(toolUse, deferred[i], cancellationToken))));
        foreach (DeferredSink tool in deferred)
        {
            tool.ReplayTo(sink);
        }

        return results;
    }

    /// <summary>Tools that only read and never ask the user anything, so they can run alongside each other.</summary>
    private static bool CanRunInParallel(ToolUseBlockParam toolUse) => toolUse.Name switch
    {
        "read_file" or "glob" or "grep" => true,
        "git" => toolUse.Input.TryGetValue("args", out JsonElement args)
            && args.ValueKind == JsonValueKind.Array
            && args.EnumerateArray().All(a => a.ValueKind == JsonValueKind.String)
            && args.GetArrayLength() > 0
            && GitRunner.IsReadOnly(args.EnumerateArray().Select(a => a.GetString()!).ToList()),
        _ => false,
    };

    private Task<string> ExecuteToolAsync(ToolUseBlockParam toolUse, IChatSink sink, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonElement> input = toolUse.Input;
        return toolUse.Name switch
        {
            "git" => this.RunGitAsync(input, sink, cancellationToken),
            "glob" => this.GlobAsync(input, sink, cancellationToken),
            "grep" => this.GrepAsync(input, sink, cancellationToken),
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

    private async Task<string> GlobAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string pattern = RequireString(input, "pattern");
        string folder = this.OptionalRepoPath(input, "path", folderOnly: true);
        string where = folder.Length > 0 ? $" in {folder}" : string.Empty;

        sink.SetStatus($"Finding {pattern}{where}");
        IReadOnlyList<string> all = await RepoSearch.ListFilesAsync(this.RepoRoot, cancellationToken);
        IReadOnlyList<string> matches = RepoSearch.Glob(all, pattern, folder);
        string list = string.Join("\n", matches.Take(RepoSearch.MaxGlobResults));
        string more = matches.Count > RepoSearch.MaxGlobResults
            ? $"\n[{matches.Count - RepoSearch.MaxGlobResults} more files; narrow the pattern or path.]"
            : string.Empty;

        string count = matches.Count == 1 ? "1 file" : $"{matches.Count} files";
        sink.ToolActivity(ActivityKind.Search, $"Glob {pattern}{where}", matches.Count == 0 ? "No files found." : $"{count}\n{list}{more}");
        return matches.Count == 0 ? "No files found." : list + more;
    }

    private async Task<string> GrepAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string pattern = RequireString(input, "pattern");
        string scope = this.OptionalRepoPath(input, "path", folderOnly: false);
        string? glob = input.TryGetValue("glob", out JsonElement g) && g.ValueKind == JsonValueKind.String && g.GetString()!.Trim().Length > 0
            ? g.GetString()!.Trim()
            : null;
        bool ignoreCase = input.TryGetValue("case_insensitive", out JsonElement ci) && ci.ValueKind == JsonValueKind.True;
        string modeName = input.TryGetValue("output_mode", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "content";
        GrepOutput mode = modeName switch
        {
            "content" => GrepOutput.Content,
            "files_with_matches" => GrepOutput.FilesWithMatches,
            "count" => GrepOutput.Count,
            _ => throw new ToolInputException("`output_mode` must be \"content\", \"files_with_matches\" or \"count\"."),
        };
        int context = Math.Max(0, Math.Min(10, OptionalInt(input, "context") ?? 0));
        int maxResults = Math.Max(1, Math.Min(2000, OptionalInt(input, "max_results") ?? 200));

        string where = (scope.Length > 0 ? $" in {scope}" : string.Empty) + (glob is null ? string.Empty : $" ({glob})");
        sink.SetStatus($"Searching for {pattern}{where}");
        IReadOnlyList<string> all = await RepoSearch.ListFilesAsync(this.RepoRoot, cancellationToken);
        bool scopeIsFile = scope.Length > 0 && File.Exists(Path.Combine(this.RepoRoot, scope));
        IEnumerable<string> files = glob is null || scopeIsFile ? RepoSearch.Under(all, scope) : RepoSearch.Glob(all, glob, scope);
        string result = await Task.Run(
            () => RepoSearch.Grep(this.RepoRoot, files, pattern, ignoreCase, mode, context, maxResults, cancellationToken), cancellationToken);
        sink.ToolActivity(ActivityKind.Search, $"Grep {pattern}{where}", result);
        return result;
    }

    /// <summary>An optional repository-relative path argument, checked and normalized to forward slashes; empty for the root.</summary>
    private string OptionalRepoPath(IReadOnlyDictionary<string, JsonElement> input, string name, bool folderOnly)
    {
        if (!input.TryGetValue(name, out JsonElement el) || el.ValueKind != JsonValueKind.String
            || el.GetString()!.Trim() is not { Length: > 0 } path || path is "." or "/" or "\\")
        {
            return string.Empty;
        }

        string full = this.files.Resolve(path);
        if (!Directory.Exists(full) && (folderOnly || !File.Exists(full)))
        {
            throw new ToolInputException(folderOnly ? $"Directory not found: {path}" : $"Not found: {path}");
        }

        return this.files.Relative(full).Replace('\\', '/');
    }

    private string ReadFile(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink)
    {
        string path = RequireString(input, "path");
        RepoFiles.TextFile file = this.files.Read(path);
        string[] lines = file.Text.Replace("\r\n", "\n").Split('\n');
        int startLine = OptionalInt(input, "start_line") is int s ? Math.Max(1, s) : 1;
        int endLine = OptionalInt(input, "end_line") is int e ? Math.Min(lines.Length, e) : lines.Length;
        if (file.Text.Length == 0)
        {
            sink.ToolActivity(ActivityKind.Read, $"Read {this.files.Relative(file.FullPath)}", "empty file");
            return "(empty file)";
        }

        if (startLine > lines.Length)
        {
            throw new ToolInputException($"{path} has {lines.Length} line{(lines.Length == 1 ? "" : "s")}, so start_line {startLine} is past the end.");
        }

        if (startLine > endLine)
        {
            throw new ToolInputException($"end_line ({endLine}) is before start_line ({startLine}).");
        }

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
        string updated = replaceAll ? file.Text.Replace(find, replace) : ReplaceFirst(file.Text, find, replace);
        string diff = UnifiedDiff.Create(file.Text, updated);
        var change = new FileChange(file.FullPath, file.Text, updated);
        ThrowIfUnsaved(file.FullPath, relative);
        if (!await this.ApproveEditAsync(ActivityKind.Edit, $"Edit {relative}", diff, change, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"Edit {relative}", "Declined by you.");
            return "The user declined this edit.";
        }

        this.ThrowIfChanged(path, file.Text);
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

        string after = RepoFiles.NormalizeNewlines(content, newline);

        // A new file is shown whole; an overwritten one as what changed.
        ActivityKind kind = action == "Create" ? ActivityKind.Create : ActivityKind.Edit;
        string preview = action == "Create" ? Preview(content) : UnifiedDiff.Create(before, after);
        var change = new FileChange(full, before, after);
        ThrowIfUnsaved(full, relative);
        if (!await this.ApproveEditAsync(kind, $"{action} {relative}", preview, change, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"{action} {relative}", "Declined by you.");
            return "The user declined this change.";
        }

        this.ThrowIfChanged(path, action == "Create" ? null : before);
        this.files.Write(full, after, encoding);
        sink.ToolActivity(kind, action == "Create" ? $"Created {relative}" : $"Overwrote {relative}", preview, change);
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
        ThrowIfUnsaved(full, relative);
        if (!await this.ApproveEditAsync(ActivityKind.Edit, $"Delete {relative}", preview, change, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"Delete {relative}", "Declined by you.");
            return "The user declined to delete this file.";
        }

        if (before != null)
        {
            this.ThrowIfChanged(path, before);
        }
        else
        {
            ThrowIfUnsaved(full, relative);
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
        ThrowIfUnsaved(source, this.files.Relative(source));
        if (!await this.ApproveEditAsync(ActivityKind.Edit, "Move " + title, string.Empty, null, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, "Move " + title, "Declined by you.");
            return "The user declined to move this file.";
        }

        // Things may have moved on while the user was deciding.
        ThrowIfUnsaved(source, this.files.Relative(source));
        if (!File.Exists(source) || (!caseOnly && (File.Exists(destination) || Directory.Exists(destination))))
        {
            throw new ToolInputException($"{from} or {to} changed while waiting for approval, so the file wasn't moved. Check them again first.");
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
        (int? exitCode, string output) result;
        using (var stop = new CancellationTokenSource())
        using (ILiveActivity live = sink.StartLiveActivity(ActivityKind.Shell, command + where, stop.Cancel))
        {
            // The row shows the end of the output as it arrives, refreshed a few times a second.
            var tail = new OutputTail(LiveOutputLines);
            Task<(int?, string)> run = ShellRunner.RunAsync(directory, command, TimeSpan.FromSeconds(timeout), cancellationToken, stop.Token, tail.Add);
            while (await Task.WhenAny(run, Task.Delay(LiveOutputInterval)) != run)
            {
                live.SetOutput(tail.Text);
            }

            result = await run;
            if (stop.IsCancellationRequested)
            {
                sink.ToolActivity(ActivityKind.Declined, command + where, $"Stopped by you.\n{result.output}".TrimEnd());
                return $"The user stopped this command before it finished. Don't run it again unless they ask.\n{result.output}".TrimEnd();
            }
        }

        (int? exitCode, string output) = result;
        string shown = output.Length == 0 ? "(no output)" : output;
        if (exitCode is not int code)
        {
            sink.ToolActivity(ActivityKind.Failed, command + where, $"Stopped after {timeout}s.\n{output}".TrimEnd());
            return $"The command was stopped after {timeout} seconds. Pass a larger timeout_seconds (up to {MaxCommandSeconds}) if it needs longer.\n{output}".TrimEnd();
        }

        sink.ToolActivity(code == 0 ? ActivityKind.Shell : ActivityKind.Failed, command + where, code == 0 ? shown : $"Exit code {code}\n{shown}");
        return code == 0 ? shown : $"Exit code {code}.\n{shown}";
    }

    /// <summary>Refuses to change a file the user is editing in Visual Studio, rather than racing their unsaved changes.</summary>
    private static void ThrowIfUnsaved(string fullPath, string relative)
    {
        if (OpenDocuments.HasUnsavedChanges(fullPath))
        {
            throw new ToolInputException(
                $"{relative} has unsaved changes in Visual Studio, so it wasn't changed. Ask the user to save or discard them, then read the file again.");
        }
    }

    /// <summary>
    /// Refuses the change if the file isn't as it was when the change was worked out (null: didn't exist), e.g.
    /// because the user edited it while deciding, so their work isn't overwritten.
    /// </summary>
    private void ThrowIfChanged(string path, string? expected)
    {
        string full = this.files.Resolve(path);
        ThrowIfUnsaved(full, this.files.Relative(full));
        string? now = File.Exists(full) ? this.files.Read(path).Text : null;
        if (!string.Equals(now, expected, StringComparison.Ordinal))
        {
            throw new ToolInputException($"{path} changed while waiting for approval, so the change wasn't made. Read it again first.");
        }
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
            Name = "glob",
            Description =
                "Find files by path pattern. `*` and `?` match within one folder, `**` across folders, `{a,b}` either. " +
                "A pattern without a slash matches file names at any depth (\"*.cs\"); one with a slash matches the path " +
                "relative to `path` (\"src/**/*Tests.cs\"). Matching is case-insensitive. Covers tracked and untracked " +
                $"files but not git-ignored ones. Returns up to {RepoSearch.MaxGlobResults} paths, sorted.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["pattern"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The glob, e.g. \"*.csproj\" or \"src/**/*.xaml\"." }),
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Folder to search in, relative to the repository root. Default: the root." }),
                },
                Required = ["pattern"],
            },
            EagerInputStreaming = true,
        },
        new Tool
        {
            Name = "grep",
            Description =
                "Search file contents line by line with a .NET regular expression (escape regex characters such as " +
                "`(`, `.` or `{` to match them literally). Covers tracked and untracked files but not git-ignored, binary " +
                "or very large ones. Narrow the search with `path` (a folder or a file) and `glob` (e.g. \"*.cs\"). " +
                "output_mode \"content\" (default) returns \"path:line:text\" for each match, with `context` lines around " +
                "it marked \"path-line-text\"; \"files_with_matches\" returns only paths; \"count\" returns matches per file.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["pattern"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The regular expression." }),
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Folder or file to search, relative to the repository root. Default: the whole repository." }),
                    ["glob"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Only search files matching this glob, as in the glob tool." }),
                    ["case_insensitive"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Ignore case. Default false." }),
                    ["output_mode"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "content", "files_with_matches", "count" } }),
                    ["context"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Lines of context before and after each match (content mode), 0-10. Default 0." }),
                    ["max_results"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Most matching lines (or files) to return. Default 200." }),
                },
                Required = ["pattern"],
            },
            EagerInputStreaming = true,
        },
        new WebSearchTool20250305 { MaxUses = MaxWebUses },
        new WebFetchTool20250910 { MaxUses = MaxWebUses, MaxContentTokens = 50_000 },
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

    private enum BlockKind { Other, Text, Thinking, RedactedThinking, ToolUse, ServerToolUse, ServerToolResult }

    private sealed class BlockBuilder
    {
        public BlockKind Kind;
        public StringBuilder Text = new();
        public string Signature = string.Empty;
        public string Data = string.Empty;
        public string ToolId = string.Empty;
        public string ToolName = string.Empty;

        // Server tool blocks as received.
        public IReadOnlyDictionary<string, JsonElement> Raw = new Dictionary<string, JsonElement>();
    }

    /// <summary>
    /// Stands in for the chat while a tool runs on a background thread: what it reports is kept, to be replayed on
    /// the UI thread later. Only for tools that never ask the user anything.
    /// </summary>
    private sealed class DeferredSink : IChatSink
    {
        private readonly List<Action<IChatSink>> calls = [];

        public void ReplayTo(IChatSink sink)
        {
            foreach (Action<IChatSink> call in this.calls)
            {
                call(sink);
            }
        }

        public void AppendText(string text) => this.Add(s => s.AppendText(text));

        public void AppendThinking(string text) => this.Add(s => s.AppendThinking(text));

        public void ToolActivity(ActivityKind kind, string title, string detail, FileChange? change = null) => this.Add(s => s.ToolActivity(kind, title, detail, change));

        public void SetStatus(string? status)
        {
            // Each tool's "Reading…" would be stale by the time it's replayed; the batch has its own status.
        }

        public ILiveActivity StartLiveActivity(ActivityKind kind, string title, Action stop)
            => throw new InvalidOperationException("Tools running alongside others can't show live progress.");

        public void ReportTokenUsage(ClaudeModel model, TokenUsage usage) => this.Add(s => s.ReportTokenUsage(model, usage));

        public void ReportPromptUsage(TokenUsage usage) => this.Add(s => s.ReportPromptUsage(usage));

        public void ReportLiveTokens(long totalTokens) => this.Add(s => s.ReportLiveTokens(totalTokens));

        public void ReportContext(long tokens) => this.Add(s => s.ReportContext(tokens));

        public void ReportCompacted(long tokensBefore) => this.Add(s => s.ReportCompacted(tokensBefore));

        public void ReportApiRateLimits(ApiRateLimits limits) => this.Add(s => s.ReportApiRateLimits(limits));

        public void ReportPlanLimit(PlanLimit limit) => this.Add(s => s.ReportPlanLimit(limit));

        public Task<IReadOnlyDictionary<string, string>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Tools running alongside others can't ask the user anything.");

        public Task<Approval> RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change = null)
            => throw new InvalidOperationException("Tools running alongside others can't ask for approval.");

        private void Add(Action<IChatSink> call)
        {
            lock (this.calls)
            {
                this.calls.Add(call);
            }
        }
    }

    /// <summary>The latest lines of a running command's output. Lines arrive on background threads.</summary>
    private sealed class OutputTail(int maxLines)
    {
        private readonly Queue<string> lines = new();

        public string Text
        {
            get
            {
                lock (this.lines)
                {
                    return string.Join("\n", this.lines);
                }
            }
        }

        public void Add(string line)
        {
            lock (this.lines)
            {
                this.lines.Enqueue(line);
                while (this.lines.Count > maxLines)
                {
                    this.lines.Dequeue();
                }
            }
        }
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
        public long WebSearches;
    }
}

internal sealed class ToolInputException(string message) : Exception(message);
