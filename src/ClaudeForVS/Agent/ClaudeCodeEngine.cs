using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Git;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.Agent;

/// <summary>
/// Runs each message through the Claude Code CLI (<c>claude -p</c>) in the repository, signed in with the user's
/// own plan. Follow-up messages resume the same Claude Code session. Claude Code's permission prompts come back
/// through <see cref="ApprovalServer"/> and are shown in the extension's approval card.
/// </summary>
internal sealed class ClaudeCodeEngine : IChatEngine
{
    private const string ExtraSystemPrompt =
        "You are running inside a Visual Studio extension's chat window, which renders your replies as Markdown. " +
        "Keep answers concise and reference files by repository-relative paths.";

    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal) { "Edit", "MultiEdit", "Write", "NotebookEdit" };

    private readonly string executable;
    private readonly ApprovalServer approvalServer;
    private readonly string mcpConfigPath;
    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private readonly Dictionary<string, (string Name, JsonElement Input)> pendingTools = new();

    private string? sessionId;
    private bool editsPreApproved;
    private IChatSink? sink;
    private CancellationToken runToken;
    private Process? running;

    public ClaudeCodeEngine(string executable, string repoRoot, ClaudeModel model, EffortLevel effort, AgentMode mode)
    {
        this.executable = executable;
        this.RepoRoot = repoRoot;
        this.Model = model;
        this.Effort = effort;
        this.Mode = mode;
        this.approvalServer = new ApprovalServer(this.DecideAsync);

        string dir = Path.Combine(Path.GetTempPath(), "ClaudeForVS");
        Directory.CreateDirectory(dir);
        this.mcpConfigPath = Path.Combine(dir, $"mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(this.mcpConfigPath, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ApprovalServer.ServerName] = new { type = "http", url = this.approvalServer.Url },
            },
        }));
    }

    public ChatEngineKind Kind => ChatEngineKind.ClaudeCode;

    public string RepoRoot { get; }

    public ClaudeModel Model { get; }

    public EffortLevel Effort { get; set; }

    public AgentMode Mode { get; set; }

    public async Task SendAsync(string prompt, IChatSink chatSink, CancellationToken cancellationToken)
    {
        this.sink = chatSink;
        this.runToken = cancellationToken;
        this.pendingTools.Clear();

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--model", AgentSettings.ModelId(this.Model),
            "--permission-mode", this.Mode == AgentMode.Plan ? "plan" : "default",
            "--mcp-config", this.mcpConfigPath,
            "--permission-prompt-tool", ApprovalServer.QualifiedToolName,
            "--append-system-prompt", ExtraSystemPrompt + " " + AgentSettings.ModeInstructions(this.Mode),
        };
        if (this.Model != ClaudeModel.Haiku)
        {
            args.Add("--effort");
            args.Add(this.Effort switch
            {
                EffortLevel.Low => "low",
                EffortLevel.Medium => "medium",
                EffortLevel.XHigh => "xhigh",
                EffortLevel.Max => "max",
                _ => "high",
            });
        }

        if (this.sessionId != null)
        {
            args.Add("--resume");
            args.Add(this.sessionId);
        }

        var psi = new ProcessStartInfo(this.executable, string.Join(" ", args.Select(GitRunner.QuoteArgument)))
        {
            WorkingDirectory = this.RepoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ClaudeCodeCli.RemoveApiKeyVariables(psi); // use the subscription login, not a stray API key

        chatSink.SetStatus("Starting Claude Code…");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start Claude Code.");
        this.running = process;
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        // The prompt goes in on stdin as UTF-8 (net48 can't set the stdin encoding, so write the bytes directly).
        byte[] promptBytes = new UTF8Encoding(false).GetBytes(prompt);
        await process.StandardInput.BaseStream.WriteAsync(promptBytes, 0, promptBytes.Length, cancellationToken);
        process.StandardInput.Close();

        string? resultError = null;
        bool gotResult = false;
        using (cancellationToken.Register(() => KillTree(process)))
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    if (this.HandleEvent(doc.RootElement, chatSink) is { } outcome)
                    {
                        gotResult = true;
                        resultError = outcome.Error;
                    }
                }
                catch (JsonException)
                {
                    // Not an event (e.g. a stray log line); ignore it.
                }
            }

            // Output has ended, so the process is exiting; don't let a stuck child hold up the UI thread.
            if (!process.WaitForExit(10_000))
            {
                KillTree(process);
            }
        }

        this.running = null;
        cancellationToken.ThrowIfCancellationRequested();

        // The stream only says which window is binding; fetch exact percentages for every window.
        if (gotResult)
        {
            foreach (PlanLimit limit in await PlanUsageClient.FetchAsync(cancellationToken))
            {
                chatSink.ReportPlanLimit(limit);
            }
        }

        if (resultError != null)
        {
            throw new InvalidOperationException(resultError);
        }

        if (!gotResult && process.ExitCode != 0)
        {
            string err = (await stderr).Trim();
            throw new InvalidOperationException(err.Length > 0 ? err : $"Claude Code exited with code {process.ExitCode}.");
        }
    }

    public void Dispose()
    {
        if (this.running is { } process)
        {
            KillTree(process);
        }

        this.approvalServer.Dispose();
        try
        {
            File.Delete(this.mcpConfigPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Handles one stream-json event. Returns a value only for the final <c>result</c> event.</summary>
    private RunOutcome? HandleEvent(JsonElement ev, IChatSink chatSink)
    {
        string type = Str(ev, "type") ?? string.Empty;

        // Subagents' messages carry the ID of the tool call that spawned them; show only the main conversation.
        bool isTopLevel = !ev.TryGetProperty("parent_tool_use_id", out JsonElement parent) || parent.ValueKind == JsonValueKind.Null;

        switch (type)
        {
            case "system":
                switch (Str(ev, "subtype"))
                {
                    case "init":
                        this.sessionId = Str(ev, "session_id") ?? this.sessionId;
                        chatSink.SetStatus("Claude Code is working…");
                        break;
                    case "api_retry":
                        chatSink.SetStatus($"Retrying ({Num(ev, "attempt")}/{Num(ev, "max_retries")})…");
                        break;
                }

                break;

            case "stream_event" when isTopLevel && ev.TryGetProperty("event", out JsonElement inner):
                if (Str(inner, "type") == "content_block_delta" && inner.TryGetProperty("delta", out JsonElement delta))
                {
                    switch (Str(delta, "type"))
                    {
                        case "text_delta":
                            chatSink.AppendText(Str(delta, "text") ?? string.Empty);
                            break;
                        case "thinking_delta":
                            chatSink.AppendThinking(Str(delta, "thinking") ?? string.Empty);
                            break;
                    }
                }
                else if (Str(inner, "type") == "content_block_start"
                    && inner.TryGetProperty("content_block", out JsonElement block)
                    && Str(block, "type") == "tool_use")
                {
                    chatSink.SetStatus($"Preparing {Str(block, "name")}…");
                }

                break;

            case "assistant" when isTopLevel:
                // Text already streamed via deltas; remember tool calls so their results can be shown.
                foreach (JsonElement block in Content(ev))
                {
                    if (Str(block, "type") == "tool_use" && Str(block, "id") is string id)
                    {
                        this.pendingTools[id] = (Str(block, "name") ?? "tool", block.TryGetProperty("input", out JsonElement input) ? input.Clone() : default);
                    }
                }

                break;

            case "user" when isTopLevel:
                foreach (JsonElement block in Content(ev))
                {
                    if (Str(block, "type") == "tool_result"
                        && Str(block, "tool_use_id") is string id
                        && this.pendingTools.TryGetValue(id, out var call))
                    {
                        this.pendingTools.Remove(id);
                        bool isError = block.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True;
                        (ActivityKind kind, string title, string detail) = this.Describe(call.Name, call.Input, ResultText(block), isError);
                        chatSink.ToolActivity(kind, title, detail);
                    }
                }

                break;

            case "rate_limit_event" when ev.TryGetProperty("rate_limit_info", out JsonElement info):
                if (ToPlanLimit(info) is PlanLimit limit)
                {
                    chatSink.ReportPlanLimit(limit);
                }

                break;

            case "result":
                double seconds = Num(ev, "duration_ms") / 1000.0;
                chatSink.SetStatus($"Done in {seconds:0.#}s · {Num(ev, "num_turns")} turn(s) · via Claude Code");
                if (ev.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    chatSink.ReportPromptUsage(new TokenUsage(
                        (long)Num(usage, "input_tokens"),
                        (long)Num(usage, "cache_creation_input_tokens"),
                        (long)Num(usage, "cache_read_input_tokens"),
                        (long)Num(usage, "output_tokens")));
                }

                bool failed = ev.TryGetProperty("is_error", out JsonElement isErr) && isErr.ValueKind == JsonValueKind.True;
                return new RunOutcome(failed ? (Str(ev, "result") is { Length: > 0 } msg ? msg : $"Claude Code reported an error ({Str(ev, "subtype")}).") : null);
        }

        return null;
    }

    /// <summary>Called by the approval server (on a thread-pool thread) when Claude Code needs permission.</summary>
    private async Task<PermissionDecision> DecideAsync(string toolName, JsonElement input)
    {
        if (this.editsPreApproved && EditTools.Contains(toolName) && this.Mode != AgentMode.Plan)
        {
            return new PermissionDecision(true);
        }

        if (toolName == "AskUserQuestion")
        {
            return await this.AskQuestionsAsync(input).ConfigureAwait(false);
        }

        // Plan mode never leaves plan mode or changes files; the user switches modes in Settings.
        if (this.Mode == AgentMode.Plan && (toolName == "ExitPlanMode" || EditTools.Contains(toolName)))
        {
            return new PermissionDecision(false, "Plan mode is on, so no changes can be made. Present your suggestions as text; the user can switch modes in Settings.");
        }

        if (this.Mode == AgentMode.Auto)
        {
            return new PermissionDecision(true);
        }

        IChatSink? chatSink = this.sink;
        if (chatSink is null)
        {
            return new PermissionDecision(false, "No one is available to approve this.");
        }

        await this.approvalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            (ActivityKind kind, string title, string detail) = this.DescribeRequest(toolName, input);
            bool isEdit = EditTools.Contains(toolName);

            // The chat UI must be touched on Visual Studio's main thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.runToken);
            chatSink.SetStatus("Waiting for your approval…");
            Approval approval = await chatSink.RequestApprovalAsync(kind, title, detail, isEdit ? "Allow all edits" : null, this.runToken);

            if (approval == Approval.AllowAll)
            {
                this.editsPreApproved = true;
            }

            return approval == Approval.Deny
                ? new PermissionDecision(false, "The user declined this action. Don't retry it; ask what they'd like instead.")
                : new PermissionDecision(true);
        }
        catch (OperationCanceledException)
        {
            return new PermissionDecision(false, "Cancelled by the user.");
        }
        finally
        {
            this.approvalGate.Release();
        }
    }

    /// <summary>
    /// Shows Claude Code's AskUserQuestion questions in the question card. Answers go back as the tool's input:
    /// the original questions plus an "answers" map from question text to the chosen label(s) or typed text.
    /// </summary>
    private async Task<PermissionDecision> AskQuestionsAsync(JsonElement input)
    {
        IReadOnlyList<UserQuestion> questions = UserQuestion.ParseAll(input);
        if (questions.Count == 0 || this.sink is not IChatSink chatSink)
        {
            return new PermissionDecision(false, "Couldn't show these questions; ask in plain text instead.");
        }

        await this.approvalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.runToken);
            chatSink.SetStatus("Waiting for your answer…");
            IReadOnlyDictionary<string, string>? answers = await chatSink.AskQuestionsAsync(questions, this.runToken);
            if (answers is null)
            {
                return new PermissionDecision(false, "The user declined to answer these questions. Continue with your best judgement, or ask in plain text.");
            }

            string updatedInput = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["questions"] = input.GetProperty("questions"),
                ["answers"] = answers,
            });
            return new PermissionDecision(true, UpdatedInputJson: updatedInput);
        }
        catch (OperationCanceledException)
        {
            return new PermissionDecision(false, "Cancelled by the user.");
        }
        finally
        {
            this.approvalGate.Release();
        }
    }

    /// <summary>How a pending permission request is shown on the approval card.</summary>
    private (ActivityKind Kind, string Title, string Detail) DescribeRequest(string tool, JsonElement input) => tool switch
    {
        "Bash" or "PowerShell" => (ActivityKind.Shell, $"Run: {Str(input, "command")}", Str(input, "description") ?? string.Empty),
        "Edit" => (ActivityKind.Edit, $"Edit {this.Relative(Str(input, "file_path"))}", EditDiff(input)),
        "MultiEdit" => (ActivityKind.Edit, $"Edit {this.Relative(Str(input, "file_path"))}", MultiEditDiff(input)),
        "Write" => (ActivityKind.Create, $"Write {this.Relative(Str(input, "file_path"))}", Str(input, "content") ?? string.Empty),
        "NotebookEdit" => (ActivityKind.Edit, $"Edit notebook {this.Relative(Str(input, "notebook_path"))}", Str(input, "new_source") ?? string.Empty),
        "WebFetch" => (ActivityKind.Web, $"Fetch {Str(input, "url")}", Str(input, "prompt") ?? string.Empty),
        "WebSearch" => (ActivityKind.Web, $"Search the web: {Str(input, "query")}", string.Empty),
        _ => (ActivityKind.Other, $"Use {tool}", input.ValueKind == JsonValueKind.Undefined ? string.Empty : Pretty(input)),
    };

    /// <summary>How a completed tool call is shown in the transcript.</summary>
    private (ActivityKind Kind, string Title, string Detail) Describe(string tool, JsonElement input, string result, bool isError)
    {
        (ActivityKind kind, string title, string detail) = tool switch
        {
            "Bash" or "PowerShell" => (ActivityKind.Shell, Str(input, "command") ?? tool, result),
            "Read" => (ActivityKind.Read, $"Read {this.Relative(Str(input, "file_path"))}", string.Empty),
            "Edit" => (ActivityKind.Edit, $"Edited {this.Relative(Str(input, "file_path"))}", EditDiff(input)),
            "MultiEdit" => (ActivityKind.Edit, $"Edited {this.Relative(Str(input, "file_path"))}", MultiEditDiff(input)),
            "Write" => (ActivityKind.Create, $"Wrote {this.Relative(Str(input, "file_path"))}", Str(input, "content") ?? string.Empty),
            "NotebookEdit" => (ActivityKind.Edit, $"Edited notebook {this.Relative(Str(input, "notebook_path"))}", string.Empty),
            "Glob" => (ActivityKind.Search, $"Find files: {Str(input, "pattern")}", result),
            "Grep" => (ActivityKind.Search, $"Search: {Str(input, "pattern")}", result),
            "WebFetch" => (ActivityKind.Web, $"Fetched {Str(input, "url")}", result),
            "WebSearch" => (ActivityKind.Web, $"Searched the web: {Str(input, "query")}", result),
            "TodoWrite" => (ActivityKind.Other, "Updated the task list", TodoList(input)),
            "AskUserQuestion" => (ActivityKind.Question, "Asked you " + QuestionCount(input), result),
            "Task" or "Agent" => (ActivityKind.Other, $"Subagent: {Str(input, "description") ?? "task"}", result),
            _ => (ActivityKind.Other, tool, result),
        };

        if (isError)
        {
            bool declined = result.IndexOf("declined", StringComparison.OrdinalIgnoreCase) >= 0
                || result.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0
                || result.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0;
            return (declined ? ActivityKind.Declined : ActivityKind.Failed, title, result);
        }

        return (kind, title, detail);
    }

    private string Relative(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(unknown file)";
        }

        string root = this.RepoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path!.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
    }

    /// <summary>Converts Claude Code's rate_limit_info (claude.ai plan usage) into a <see cref="PlanLimit"/>.</summary>
    private static PlanLimit? ToPlanLimit(JsonElement info)
    {
        if (Str(info, "rateLimitType") is not string window)
        {
            return null;
        }

        double? utilization = null;
        if (info.TryGetProperty("utilization", out JsonElement u) && u.ValueKind == JsonValueKind.Number)
        {
            // The event doesn't document its scale (the /usage data is 0-100); treat values up to 1 as a fraction.
            double value = u.GetDouble();
            utilization = Math.Max(0, Math.Min(100, value <= 1 ? value * 100 : value));
        }

        DateTimeOffset? resetsAt = null;
        if (info.TryGetProperty("resetsAt", out JsonElement r) && r.ValueKind == JsonValueKind.Number)
        {
            long epoch = r.GetInt64();
            resetsAt = epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return new PlanLimit(window, utilization, resetsAt, Str(info, "status") ?? "allowed", DateTimeOffset.Now);
    }

    private static string EditDiff(JsonElement input) => Diff(Str(input, "old_string") ?? string.Empty, Str(input, "new_string") ?? string.Empty);

    private static string MultiEditDiff(JsonElement input)
    {
        if (!input.TryGetProperty("edits", out JsonElement edits) || edits.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join("\n", edits.EnumerateArray().Select(EditDiff));
    }

    private static string Diff(string oldText, string newText)
    {
        IEnumerable<string> Lines(string text, string prefix) =>
            text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n').Select(l => prefix + l);
        return string.Join("\n", Lines(oldText, "- ").Concat(Lines(newText, "+ ")));
    }

    private static string QuestionCount(JsonElement input) =>
        UserQuestion.ParseAll(input).Count is int n && n == 1 ? "a question" : $"{UserQuestion.ParseAll(input).Count} questions";

    private static string TodoList(JsonElement input)
    {
        if (!input.TryGetProperty("todos", out JsonElement todos) || todos.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join("\n", todos.EnumerateArray().Select(t =>
            (Str(t, "status") switch { "completed" => "[x] ", "in_progress" => "[~] ", _ => "[ ] " }) + Str(t, "content")));
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out JsonElement content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join("\n", content.EnumerateArray().Where(c => Str(c, "type") == "text").Select(c => Str(c, "text")));
        }

        return string.Empty;
    }

    private static IEnumerable<JsonElement> Content(JsonElement ev) =>
        ev.TryGetProperty("message", out JsonElement message)
        && message.TryGetProperty("content", out JsonElement content)
        && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : [];

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static string Pretty(JsonElement element) => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Stops Claude Code and anything it started (shell commands run as child processes).</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            using var kill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}") { CreateNoWindow = true, UseShellExecute = false });
            kill?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private readonly record struct RunOutcome(string? Error);
}
