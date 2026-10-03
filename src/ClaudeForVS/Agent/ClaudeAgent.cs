using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Git;

namespace ClaudeForVS.Agent;

/// <summary>
/// A single conversation with Claude about one git repository. Runs a manual, streaming
/// tool-use loop: stream a response, execute any requested tools, send results, repeat.
/// History is append-only so the prompt cache stays valid across turns.
/// </summary>
internal sealed class ClaudeAgent
{
    private const string ModelId = "claude-opus-5-5";
    private const int MaxToolRounds = 40;
    private const int MaxFileChars = 100_000;

    private readonly AnthropicClient client;
    private readonly List<MessageParam> history = [];
    private readonly string systemPrompt;

    public ClaudeAgent(string apiKey, string repoRoot)
    {
        this.client = new AnthropicClient { ApiKey = apiKey };
        this.RepoRoot = repoRoot;
        this.systemPrompt =
            $"""
            You are Claude, working inside a Visual Studio extension as a coding assistant for the git repository at:
            {repoRoot}

            You can inspect and operate on the repository with the `git` tool and read files with `read_file`.
            Read-only git commands run immediately. Commands that change the repository or talk to a remote
            (commit, checkout, reset, push, merge, stash, etc.) are shown to the user, who must approve them first;
            if the user declines, accept that and don't retry the same command.
            Look at the repository before answering questions about it rather than guessing. Keep answers concise
            and use Markdown code fences for code. When you reference files, use paths relative to the repository root.
            """;
    }

    public string RepoRoot { get; }

    public async Task SendAsync(string prompt, IChatSink sink, CancellationToken cancellationToken)
    {
        this.history.Add(new() { Role = Role.User, Content = prompt });

        for (int round = 0; round < MaxToolRounds; round++)
        {
            sink.SetStatus("Waiting for Claude…");
            StreamedTurn turn = await this.StreamTurnAsync(sink, cancellationToken);
            this.history.Add(new() { Role = Role.Assistant, Content = turn.Content });

            sink.SetStatus(
                $"Tokens: {turn.InputTokens:N0} in ({turn.CacheReadTokens:N0} cached), {turn.OutputTokens:N0} out");

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
                    results.Add(Error(toolUse.ID, ex.Message));
                }
            }

            this.history.Add(new() { Role = Role.User, Content = results });
            cancellationToken.ThrowIfCancellationRequested();
        }

        sink.AppendText($"\n\n_Stopped after {MaxToolRounds} tool rounds._");
    }

    private static ToolResultBlockParam Error(string id, string message)
        => new() { ToolUseID = id, Content = message, IsError = true };

    private MessageCreateParams BuildRequest() => new()
    {
        Model = ModelId,
        MaxTokens = 64000,
        Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
        OutputConfig = new OutputConfig { Effort = Effort.High },
        // Automatic caching: the breakpoint moves forward with the conversation.
        CacheControl = new CacheControlEphemeral(),
        System = this.systemPrompt,
        Tools = ToolDefinitions,
        Messages = [.. this.history],
    };

    private async Task<StreamedTurn> StreamTurnAsync(IChatSink sink, CancellationToken cancellationToken)
    {
        var blocks = new SortedDictionary<long, BlockBuilder>();
        var turn = new StreamedTurn();

        await foreach (RawMessageStreamEvent ev in this.client.Messages.CreateStreaming(this.BuildRequest(), cancellationToken))
        {
            if (ev.TryPickStart(out RawMessageStartEvent? start))
            {
                turn.InputTokens = start.Message.Usage.InputTokens
                    + (start.Message.Usage.CacheReadInputTokens ?? 0)
                    + (start.Message.Usage.CacheCreationInputTokens ?? 0);
                turn.CacheReadTokens = start.Message.Usage.CacheReadInputTokens ?? 0;
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

    private async Task<string> ExecuteToolAsync(ToolUseBlockParam toolUse, IChatSink sink, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonElement> input = toolUse.Input;
        switch (toolUse.Name)
        {
            case "git":
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

                string commandLine = "git " + string.Join(' ', args.Select(QuoteForDisplay));
                if (!GitRunner.IsReadOnly(args))
                {
                    sink.SetStatus("Waiting for your approval…");
                    if (!await sink.RequestApprovalAsync(commandLine, cancellationToken))
                    {
                        sink.ToolActivity(commandLine, "Declined by user.");
                        return "The user declined to run this command.";
                    }
                }

                sink.SetStatus($"Running {commandLine}");
                (int exitCode, string output) = await GitRunner.RunAsync(this.RepoRoot, args, cancellationToken);
                sink.ToolActivity(commandLine, output.Length == 0 ? "(no output)" : output);
                return exitCode == 0
                    ? (output.Length == 0 ? "(no output)" : output)
                    : $"git exited with code {exitCode}.\n{output}";
            }

            case "read_file":
            {
                if (!input.TryGetValue("path", out JsonElement pathEl) || pathEl.ValueKind != JsonValueKind.String)
                {
                    throw new ToolInputException("`path` must be a string.");
                }

                string fullPath = Path.GetFullPath(Path.Combine(this.RepoRoot, pathEl.GetString()!));
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(this.RepoRoot)) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolInputException("Path is outside the repository.");
                }

                if (!File.Exists(fullPath))
                {
                    throw new ToolInputException($"File not found: {pathEl.GetString()}");
                }

                string[] lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
                int startLine = input.TryGetValue("start_line", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? Math.Max(1, s.GetInt32()) : 1;
                int endLine = input.TryGetValue("end_line", out JsonElement e) && e.ValueKind == JsonValueKind.Number ? Math.Min(lines.Length, e.GetInt32()) : lines.Length;

                var sb = new StringBuilder();
                for (int i = startLine; i <= endLine; i++)
                {
                    sb.Append(i).Append('\t').AppendLine(lines[i - 1]);
                    if (sb.Length > MaxFileChars)
                    {
                        sb.AppendLine($"[truncated at line {i} of {lines.Length}; use start_line/end_line to read more]");
                        break;
                    }
                }

                string relative = Path.GetRelativePath(this.RepoRoot, fullPath);
                sink.ToolActivity($"Read {relative}", $"lines {startLine}-{endLine} of {lines.Length}");
                return sb.Length == 0 ? "(empty file)" : sb.ToString();
            }

            default:
                throw new ToolInputException($"Unknown tool: {toolUse.Name}");
        }
    }

    private static string QuoteForDisplay(string arg)
        => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    private static readonly List<ToolUnion> ToolDefinitions =
    [
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
                "Read a text file from the working tree, with line numbers. The path is relative to the repository root. " +
                "Use start_line/end_line (1-based, inclusive) for large files.",
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
        public long OutputTokens;
    }
}

internal sealed class ToolInputException(string message) : Exception(message);
