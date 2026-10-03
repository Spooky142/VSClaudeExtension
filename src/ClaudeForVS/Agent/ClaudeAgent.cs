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

    private const string PlanModeRefusal =
        "Plan mode is on, so no changes can be made. Present your suggestions as text; the user can switch modes in Settings.";

    private readonly AnthropicClient client;
    private readonly RateLimitHeaderHandler rateLimits = new();
    private readonly List<MessageParam> history = [];
    private readonly RepoFiles files;
    private readonly string systemPrompt;
    private bool editsPreApproved;

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
            - `ask_user` shows the user 1-4 multiple-choice questions. Use it when you need a decision that's genuinely
              theirs (for example choosing between approaches); the user can also type their own answer.
            All file paths are relative to the repository root, and files outside the repository can't be accessed.
            If the user declines an action, accept that and don't retry the same thing.

            Look at the repository before answering questions about it rather than guessing. Keep answers concise and
            use Markdown, with fenced code blocks for code. Reference files by their repository-relative paths.
            """;
    }

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

    public async Task SendAsync(string prompt, IChatSink sink, CancellationToken cancellationToken)
    {
        this.history.Add(new() { Role = Role.User, Content = prompt });

        long promptTokens = 0; // finished responses so far; the one streaming is added on top, live
        for (int round = 0; round < MaxToolRounds; round++)
        {
            sink.SetStatus("Waiting for Claude…");
            StreamedTurn turn = await this.StreamTurnAsync(sink, promptTokens, cancellationToken);
            this.history.Add(new() { Role = Role.Assistant, Content = turn.Content });
            promptTokens += turn.InputTokens + turn.OutputTokens;
            sink.ReportLiveTokens(promptTokens);
            sink.SetStatus(null); // token counts are shown below the reply, not in the footer
            sink.ReportTokenUsage(this.Model, new TokenUsage(turn.UncachedInputTokens, turn.CacheWriteTokens, turn.CacheReadTokens, turn.OutputTokens));
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

    private static ToolResultBlockParam Error(string id, string message)
        => new() { ToolUseID = id, Content = message, IsError = true };

    private MessageCreateParams BuildRequest()
    {
        bool haiku = this.Model == ClaudeModel.Haiku;
        int? haikuBudget = AgentSettings.HaikuThinkingBudget(this.Effort);

        return new MessageCreateParams
        {
            Model = AgentSettings.ModelId(this.Model),
            MaxTokens = 64000,

            // Haiku 4.5 rejects `effort` and adaptive thinking; it takes an explicit thinking budget instead.
            Thinking = haiku ? HaikuThinking(haikuBudget) : new ThinkingConfigAdaptive { Display = Display.Summarized },
            OutputConfig = haiku ? null : new OutputConfig { Effort = ToApiEffort(this.Effort) },

            // Automatic caching: the breakpoint moves forward with the conversation.
            CacheControl = new CacheControlEphemeral(),
            System = this.systemPrompt + "\n" + AgentSettings.ModeInstructions(this.Mode),
            Tools = ToolDefinitions,
            Messages = [.. this.history],
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

        if (!GitRunner.IsReadOnly(args) && this.Mode == AgentMode.Coding)
        {
            sink.SetStatus("Waiting for your approval…");
            if (await sink.RequestApprovalAsync(ActivityKind.Git, commandLine, string.Empty, null, cancellationToken) == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, commandLine, "Declined by you.");
                return "The user declined to run this command.";
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
        if (!await this.ApproveEditAsync(ActivityKind.Edit, $"Edit {relative}", diff, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"Edit {relative}", "Declined by you.");
            return "The user declined this edit.";
        }

        string updated = replaceAll ? file.Text.Replace(find, replace) : ReplaceFirst(file.Text, find, replace);
        this.files.Write(file.FullPath, updated, file.Encoding);
        sink.ToolActivity(ActivityKind.Edit, $"Edited {relative}", diff);
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
        if (File.Exists(full))
        {
            RepoFiles.TextFile existing = this.files.Read(path);
            encoding = existing.Encoding;
            newline = existing.Newline;
            action = "Overwrite";
        }
        else if (Directory.Exists(full))
        {
            throw new ToolInputException($"{path} is a directory.");
        }

        string preview = Preview(content);
        if (!await this.ApproveEditAsync(ActivityKind.Create, $"{action} {relative}", preview, sink, cancellationToken))
        {
            sink.ToolActivity(ActivityKind.Declined, $"{action} {relative}", "Declined by you.");
            return "The user declined this change.";
        }

        this.files.Write(full, RepoFiles.NormalizeNewlines(content, newline), encoding);
        sink.ToolActivity(ActivityKind.Create, action == "Create" ? $"Created {relative}" : $"Overwrote {relative}", preview);
        return $"{(action == "Create" ? "Created" : "Overwrote")} {relative}.";
    }

    private async Task<bool> ApproveEditAsync(ActivityKind kind, string title, string detail, IChatSink sink, CancellationToken cancellationToken)
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
        Approval approval = await sink.RequestApprovalAsync(kind, title, detail, "Allow all edits", cancellationToken);
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
