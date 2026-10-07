using System;
using System.Collections.Concurrent;
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
        "Keep answers concise and reference files by repository-relative paths. " +
        "The " + ApprovalServer.DiagnosticsTool + " tool lists Visual Studio's Error List (build errors and live IntelliSense errors in open files); " +
        "check it after changing code. " + ApprovalServer.BuildTool + " builds the open solution in Visual Studio, which also works for " +
        "projects `dotnet build` can't build. " + ApprovalServer.DebugTool + " reads Visual Studio's debugger (the exception, call stack and locals when the " +
        "user has paused at a breakpoint or exception). " + ApprovalServer.DefinitionTool + ", " + ApprovalServer.ReferencesTool + " and " + ApprovalServer.SymbolsTool +
        " use Visual Studio's code model (Roslyn) for C# and Visual Basic, which knows what a name really refers to; prefer them to grep for " +
        "\"where is this declared\" or \"what calls this\", and " + ApprovalServer.RenameTool + " to rename a symbol everywhere with one approval. " +
        ApprovalServer.ListPackagesTool + ", " + ApprovalServer.AddPackageTool + " and " + ApprovalServer.RemovePackageTool + " manage NuGet packages " +
        "through Visual Studio so the project is restored; prefer them to editing a project file or `dotnet add package`. " +
        "The user may approve only some of the changes in an edit; you're then told what was left out.";

    private static readonly TimeSpan PlanUsagePollInterval = TimeSpan.FromSeconds(15);

    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal) { "Edit", "MultiEdit", "Write", "NotebookEdit" };

    private readonly string executable;
    private readonly ApprovalServer approvalServer;
    private readonly string mcpConfigPath;
    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private readonly Dictionary<string, (string Name, JsonElement Input, string? Before)> pendingTools = new();

    private bool editsPreApproved;

    // Set by Rewind: the next message resumes the session as it was at this message, in a forked copy.
    private string? resumeAt;
    private IChatSink? sink;
    private CancellationToken runToken;

    // One Claude Code process answers every message in the chat, reading them from stdin, so each message doesn't
    // pay for starting it and reconnecting the approval server. It's replaced when something on its command line
    // changes (mode, effort), after a rewind, or after it was stopped.
    private Process? process;
    private Task<string>? processStderr;
    private (AgentMode Mode, EffortLevel Effort) processSettings;
    private bool restartNeeded;

    // Its output is read between messages too (see DrainIdleOutputAsync). The read in progress then is handed to the
    // next message, which takes over reading.
    private readonly object outputLock = new();
    private Task<string?>? idleRead;
    private bool messageReading;

    public ClaudeCodeEngine(string executable, string repoRoot, ClaudeModel model, EffortLevel effort, AgentMode mode)
    {
        this.executable = executable;
        this.RepoRoot = repoRoot;
        this.Model = model;
        this.Effort = effort;
        this.Mode = mode;
        this.approvalServer = new ApprovalServer(this.DecideAsync, this.RunVisualStudioToolAsync);

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

    /// <summary>The Claude Code session this conversation runs in; set before the first message to continue an earlier one.</summary>
    public string? SessionId { get; set; }

    /// <summary>ID of the latest message in the session's main conversation; saved with the chat so it can be rewound after reopening.</summary>
    public string? LastMessageId { get; set; }

    /// <summary>"" before the session exists; otherwise "session|message", or null if the latest message isn't known.</summary>
    public string? Checkpoint =>
        this.SessionId is null ? string.Empty
        : this.LastMessageId is null ? null
        : $"{this.SessionId}|{this.LastMessageId}";

    /// <summary>A rewind that the next message will carry out, as a checkpoint; saved with the chat in case Visual Studio closes first.</summary>
    public string? PendingRewind => this.resumeAt is null ? null : $"{this.SessionId}|{this.resumeAt}";

    public bool Rewind(string checkpoint)
    {
        this.restartNeeded = true; // the running process is in the session as it was before the rewind
        if (checkpoint.Length == 0)
        {
            this.SessionId = this.LastMessageId = this.resumeAt = null; // back to before the first message
            return true;
        }

        string[] parts = checkpoint.Split('|');
        if (parts.Length != 2)
        {
            return false;
        }

        // The original session is left untouched; the next message forks a copy that ends at this message.
        this.SessionId = parts[0];
        this.LastMessageId = this.resumeAt = parts[1];
        return true;
    }

    public Task CompactAsync(IChatSink chatSink, CancellationToken cancellationToken)
    {
        if (this.SessionId is null)
        {
            chatSink.SetStatus("Nothing to compact yet.");
            return Task.CompletedTask;
        }

        return this.SendAsync(new ChatPrompt("/compact"), chatSink, cancellationToken);
    }

    public async Task SendAsync(ChatPrompt prompt, IChatSink chatSink, CancellationToken cancellationToken)
    {
        this.sink = chatSink;
        this.runToken = cancellationToken;
        this.pendingTools.Clear();
        this.liveDone = this.liveInput = this.liveOutput = this.liveChars = 0;

        // The prompt goes in on stdin as one stream-json user message, which can carry images as well as text.
        // It's written as UTF-8 bytes because net48 can't set the stdin encoding.
        var content = new List<object>();
        foreach (PromptImage image in prompt.Images)
        {
            content.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data = image.Base64 } });
        }

        content.Add(new { type = "text", text = prompt.Text });
        string message = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } }) + "\n";
        byte[] promptBytes = new UTF8Encoding(false).GetBytes(message);

        Process process = await this.EnsureProcessAsync(chatSink);
        Task<string?>? firstRead = this.ReadFirstLineAsync(process);
        try
        {
            await WriteAsync(process, promptBytes, cancellationToken);
        }
        catch (IOException)
        {
            // It exited while idle (the pipe is closed); start another and try once more.
            _ = firstRead.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await this.StopProcessAsync();
            process = await this.EnsureProcessAsync(chatSink);
            firstRead = this.ReadFirstLineAsync(process);
            await WriteAsync(process, promptBytes, cancellationToken);
        }

        string? resultError = null;
        bool gotResult = false;
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task poll = PollPlanUsageAsync(chatSink, pollCts.Token);
        try
        {
            // Stopping must not wait for the output to end: something Claude Code started (a subagent's shell, an MCP server)
            // can outlive the kill and keep the pipe open, and a read can't be cancelled. The kill itself takes a moment, so
            // it isn't done on the thread that pressed Stop.
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() =>
            {
                stopped.TrySetResult(true);
                _ = Task.Run(() => KillTree(process));
            }))
            {
                // Read this message's events, up to and including its result; the process then waits for the next one.
                while (!gotResult)
                {
                    Task<string?> read = firstRead ?? process.StandardOutput.ReadLineAsync();
                    firstRead = null;
                    if (await Task.WhenAny(read, stopped.Task) != read)
                    {
                        _ = read.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // it ends in an error once the process is disposed
                        break;
                    }

                    if (await read is not { } line)
                    {
                        break;
                    }

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

                if (gotResult)
                {
                    _ = this.DrainIdleOutputAsync(process);
                }
            }
        }
        finally
        {
            pollCts.Cancel();
        }

        await poll;
        if (cancellationToken.IsCancellationRequested)
        {
            // It was stopped part way (or is being); the next message resumes the session in a new process.
            await this.StopProcessAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!gotResult)
        {
            // Output ended without a result, so Claude Code has exited.
            string err = await this.StopProcessAsync();
            throw new InvalidOperationException(err.Length > 0 ? err : "Claude Code exited without answering.");
        }

        // The stream only says which window is binding; fetch exact percentages for every window.
        foreach (PlanLimit limit in await PlanUsageClient.FetchAsync(cancellationToken))
        {
            chatSink.ReportPlanLimit(limit);
        }

        if (resultError != null)
        {
            throw new InvalidOperationException(resultError);
        }
    }

    /// <summary>
    /// A message takes over reading Claude Code's output, before its prompt goes in so no line of the reply can be
    /// dropped: the first line is the idle read in progress, if there is one, or a new read.
    /// </summary>
    private Task<string?> ReadFirstLineAsync(Process process)
    {
        lock (this.outputLock)
        {
            this.messageReading = true;
            Task<string?> read = this.idleRead ?? process.StandardOutput.ReadLineAsync();
            this.idleRead = null;
            return read;
        }
    }

    /// <summary>
    /// Reads and drops what Claude Code writes after a reply has ended, until the next message takes over. Nothing should
    /// come then, but if something did and wasn't read, the pipe would fill, and a Claude Code that can't write its output
    /// stops dead (subagents, API requests and all) until someone reads it.
    /// </summary>
    private async Task DrainIdleOutputAsync(Process process)
    {
        lock (this.outputLock)
        {
            this.messageReading = false;
        }

        try
        {
            while (true)
            {
                Task<string?> read;
                lock (this.outputLock)
                {
                    if (this.messageReading || this.process != process)
                    {
                        return;
                    }

                    read = this.idleRead = process.StandardOutput.ReadLineAsync();
                }

                string? line = await read.ConfigureAwait(false);
                lock (this.outputLock)
                {
                    if (this.idleRead != read)
                    {
                        return; // a message took this read over, line and all
                    }

                    this.idleRead = null;
                }

                if (line is null)
                {
                    return; // it exited
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // It was stopped.
        }
    }

    private static async Task WriteAsync(Process process, byte[] bytes, CancellationToken cancellationToken)
    {
        Stream stdin = process.StandardInput.BaseStream;
        await stdin.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        await stdin.FlushAsync(cancellationToken);
    }

    /// <summary>The Claude Code process for the next message: the running one if it still fits the chat's settings, otherwise a new one.</summary>
    private async Task<Process> EnsureProcessAsync(IChatSink chatSink)
    {
        if (this.process is { HasExited: false } running && !this.restartNeeded && this.processSettings == (this.Mode, this.Effort))
        {
            chatSink.SetStatus("Claude Code is working…");
            return running;
        }

        await this.StopProcessAsync();
        this.restartNeeded = false;
        this.processSettings = (this.Mode, this.Effort);

        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
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

        if (this.SessionId != null)
        {
            args.Add("--resume");
            args.Add(this.SessionId);
            if (this.resumeAt != null)
            {
                args.Add("--resume-session-at");
                args.Add(this.resumeAt);
                args.Add("--fork-session");
                this.resumeAt = null;
            }
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

        // The approval tool's reply echoes the tool's whole input back (e.g. a file being written), JSON-escaped inside a
        // text block. Past Claude Code's default MCP output limit (25k tokens) the reply gets truncated, which Claude Code
        // then rejects as an invalid permission result, so large writes would always fail.
        psi.EnvironmentVariables["MAX_MCP_OUTPUT_TOKENS"] = "2000000";

        // Left to itself, Claude Code runs subagents (and long commands) in the background and ends its reply while they
        // work, and a scheduled prompt starts a reply of its own later. Either way the output comes when no message is
        // waiting for it, so the user never sees it; with these off, a reply waits for its subagents.
        psi.EnvironmentVariables["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1";
        psi.EnvironmentVariables["CLAUDE_CODE_DISABLE_CRON"] = "1";

        chatSink.SetStatus("Starting Claude Code…");
        var started = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!started.Start())
        {
            started.Dispose();
            throw new InvalidOperationException("Couldn't start Claude Code.");
        }

        this.process = started;
        this.processStderr = started.StandardError.ReadToEndAsync();
        return started;
    }

    /// <summary>
    /// Ends the Claude Code process, if there is one, without blocking: closing stdin lets an idle one finish and
    /// exit, and one that doesn't within a few seconds is stopped. Returns what it wrote to stderr.
    /// </summary>
    private async Task<string> StopProcessAsync()
    {
        if (this.process is not Process old)
        {
            return string.Empty;
        }

        Task<string>? stderr = this.processStderr;
        lock (this.outputLock)
        {
            this.process = null;
            this.idleRead = null;
        }

        this.processStderr = null;
        CloseInput(old);
        if (!await WaitForExitAsync(old, TimeSpan.FromSeconds(5)))
        {
            await Task.Run(() => KillTree(old));
            await WaitForExitAsync(old, TimeSpan.FromSeconds(5));
        }

        // Once it has exited, stderr reaches its end; read it before Dispose closes the pipe.
        string err = string.Empty;
        if (stderr != null && await Task.WhenAny(stderr, Task.Delay(TimeSpan.FromSeconds(2))) == stderr && stderr.Status == TaskStatus.RanToCompletion)
        {
            err = (await stderr).Trim();
        }

        old.Dispose();
        return err;
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Waits for the process to exit without blocking a thread; false if it's still running after the timeout.</summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        try
        {
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return true; // never started, or already released
        }

        return await Task.WhenAny(exited.Task, Task.Delay(timeout)).ConfigureAwait(false) == exited.Task;
    }

    /// <summary>Refreshes plan usage every so often while a prompt runs, so the bars move before the reply ends.</summary>
    private static async Task PollPlanUsageAsync(IChatSink chatSink, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PlanUsagePollInterval, cancellationToken);
                foreach (PlanLimit limit in await PlanUsageClient.FetchAsync(cancellationToken))
                {
                    chatSink.ReportPlanLimit(limit);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (this.process is { } running)
        {
            // Between messages it's idle, so closing stdin lets it save the session and exit by itself. One still
            // working (or stuck) is stopped after a few seconds.
            lock (this.outputLock)
            {
                this.process = null;
                this.idleRead = null;
            }

            CloseInput(running);
            _ = Task.Run(async () =>
            {
                if (!await WaitForExitAsync(running, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                {
                    KillTree(running);
                }

                running.Dispose();
            });
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

    /// <summary>Handles one stream-json event. Returns a value only for the final <c>result</c> event. Internal for tests.</summary>
    internal RunOutcome? HandleEvent(JsonElement ev, IChatSink chatSink)
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
                        this.SessionId = Str(ev, "session_id") ?? this.SessionId;
                        chatSink.SetStatus("Claude Code is working…");
                        break;
                    case "compact_boundary" when isTopLevel:
                        // Claude Code compacts on its own as the context fills up (or on /compact).
                        double preTokens = ev.TryGetProperty("compact_metadata", out JsonElement meta) ? Num(meta, "pre_tokens") : 0;
                        chatSink.ReportCompacted((long)preTokens);
                        break;
                    case "api_retry":
                        chatSink.SetStatus($"Retrying ({Num(ev, "attempt")}/{Num(ev, "max_retries")})…");
                        break;
                }

                break;

            case "stream_event" when isTopLevel && ev.TryGetProperty("event", out JsonElement inner):
                this.TrackLiveTokens(inner, chatSink);
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
                this.LastMessageId = Str(ev, "uuid") ?? this.LastMessageId;

                // Text already streamed via deltas; remember tool calls so their results can be shown. The tool
                // hasn't run yet, so this is the moment to keep a copy of any file it's about to edit.
                foreach (JsonElement block in Content(ev))
                {
                    if (Str(block, "type") == "tool_use" && Str(block, "id") is string id)
                    {
                        string name = Str(block, "name") ?? "tool";
                        JsonElement input = block.TryGetProperty("input", out JsonElement i) ? i.Clone() : default;
                        string? before = EditTools.Contains(name) && this.FullPath(input) is string path ? FileChange.ReadOrEmpty(path) : null;
                        this.pendingTools[id] = (name, input, before);
                        if (name == "TodoWrite")
                        {
                            chatSink.ReportTasks(TaskList.Parse(input));
                        }
                    }
                }

                break;

            case "assistant":
                // A subagent's turn stays out of the transcript, but what it's about to do shows in the status.
                if (Str(ev, "parent_tool_use_id") is string parentId)
                {
                    string who = this.pendingTools.TryGetValue(parentId, out var parentCall) && Str(parentCall.Input, "description") is { Length: > 0 } d ? d : "task";
                    foreach (JsonElement block in Content(ev))
                    {
                        if (Str(block, "type") == "tool_use")
                        {
                            chatSink.SetStatus($"Subagent ({who}): {SubagentStep(Str(block, "name") ?? "tool", block.TryGetProperty("input", out JsonElement stepInput) ? stepInput : default)}…");
                        }
                    }
                }

                break;

            case "user" when isTopLevel:
                this.LastMessageId = Str(ev, "uuid") ?? this.LastMessageId;
                foreach (JsonElement block in Content(ev))
                {
                    if (Str(block, "type") == "tool_result"
                        && Str(block, "tool_use_id") is string id
                        && this.pendingTools.TryGetValue(id, out var call))
                    {
                        this.pendingTools.Remove(id);
                        if (call.Name is "Task" or "Agent")
                        {
                            chatSink.SetStatus("Claude Code is working…");
                        }

                        if (this.partlyApplied.TryRemove(id, out FileChange? kept))
                        {
                            // The extension wrote the changes the user kept and told Claude Code no, with what was left out.
                            chatSink.ToolActivity(ActivityKind.Edit, $"Edited {this.Relative(kept.FullPath)} (some changes left out)", UnifiedDiff.Create(kept.Before, kept.After), kept);
                            continue;
                        }

                        bool isError = block.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True;
                        bool denied = this.deniedTools.TryRemove(id, out _);
                        (ActivityKind kind, string title, string detail) = this.Describe(call.Name, call.Input, ResultText(block), isError, denied);
                        FileChange? change = !isError && call.Before != null && this.FullPath(call.Input) is string path
                            ? new FileChange(path, call.Before, FileChange.ReadOrEmpty(path))
                            : null;
                        WithDiff(call.Name, change, ref kind, ref detail);
                        chatSink.ToolActivity(kind, title, detail, change);
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

    /// <summary>A tool call in a few words, e.g. "Read Foo.cs" or "Grep TODO", for the status line.</summary>
    internal static string SubagentStep(string tool, JsonElement input)
    {
        string? detail = new[] { "file_path", "path", "pattern", "command", "query", "url", "description" }
            .Select(name => Str(input, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (detail is null)
        {
            return tool;
        }

        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        return $"{tool} {(detail.Length > 80 ? detail.Substring(0, 80) + "…" : detail)}";
    }

    // Live token count for the prompt in progress: finished messages, plus the one streaming (output estimated until it ends).
    private long liveDone, liveInput, liveOutput, liveChars;

    // Everything sent with the streaming message, cache re-read included; plus its output, that's the context size.
    private long contextInput;

    private void TrackLiveTokens(JsonElement inner, IChatSink chatSink)
    {
        switch (Str(inner, "type"))
        {
            case "message_start" when inner.TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("usage", out JsonElement start):
                this.liveInput = (long)(Num(start, "input_tokens") + Num(start, "cache_creation_input_tokens")); // new input only, not the cache re-read
                this.contextInput = this.liveInput + (long)Num(start, "cache_read_input_tokens");
                this.liveOutput = 0;
                this.liveChars = 0;
                chatSink.ReportContext(this.contextInput);
                break;
            case "content_block_delta" when inner.TryGetProperty("delta", out JsonElement delta):
                this.liveChars += (Str(delta, "text") ?? Str(delta, "thinking") ?? Str(delta, "partial_json") ?? string.Empty).Length;
                break;
            case "message_delta" when inner.TryGetProperty("usage", out JsonElement usage):
                this.liveOutput = (long)Num(usage, "output_tokens");
                this.liveChars = this.liveOutput * 4;
                break;
            case "message_stop":
                chatSink.ReportContext(this.contextInput + Math.Max(this.liveOutput, this.liveChars / 4));
                this.liveDone += this.liveInput + Math.Max(this.liveOutput, this.liveChars / 4);
                this.liveInput = this.liveOutput = this.liveChars = 0;
                chatSink.ReportLiveTokens(this.liveDone);
                return;
            default:
                return;
        }

        chatSink.ReportLiveTokens(this.liveDone + this.liveInput + Math.Max(this.liveOutput, this.liveChars / 4));
    }

    // Tool calls the extension refused (the user said no, plan mode, unsaved changes...), so their failed results show
    // as declined rather than as errors. Written by the approval server's threads.
    private readonly ConcurrentDictionary<string, bool> deniedTools = new(StringComparer.Ordinal);

    // Edits the user approved only some of: the extension wrote the kept changes itself (before → what was written).
    private readonly ConcurrentDictionary<string, FileChange> partlyApplied = new(StringComparer.Ordinal);

    /// <summary>Called by the approval server (on a thread-pool thread) when Claude Code needs permission.</summary>
    private async Task<PermissionDecision> DecideAsync(string toolName, JsonElement input, string? toolUseId)
    {
        PermissionDecision decision = await this.DecideCoreAsync(toolName, input, toolUseId).ConfigureAwait(false);
        if (!decision.Allow && toolUseId != null && !this.partlyApplied.ContainsKey(toolUseId))
        {
            this.deniedTools[toolUseId] = true;
        }

        return decision;
    }

    /// <summary>Runs one of the extension's Visual Studio tools for Claude Code (called on a thread-pool thread).</summary>
    private async Task<(string Text, bool IsError)> RunVisualStudioToolAsync(string name, JsonElement input)
    {
        IVisualStudioHost vs = VisualStudioHost.Current ?? throw new InvalidOperationException("Visual Studio isn't available.");
        CancellationToken cancellationToken = this.runToken;
        switch (name)
        {
            case "get_diagnostics":
                string scope = Str(input, "path") is { Length: > 0 } path && path.Trim() is not ("." or "/")
                    ? this.Relative(this.FullPath(JsonSerializer.SerializeToElement(new { file_path = path })) ?? path).Replace('\\', '/')
                    : string.Empty;
                IReadOnlyList<ErrorListItem> items = await vs.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                return (Diagnostics.Format(items, this.RepoRoot, scope, includeWarnings: Str(input, "severity") != "error"), false);

            case "build_solution":
                string solution = await vs.GetSolutionDirectoryAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("No solution is open in Visual Studio.");
                string root = this.RepoRoot.TrimEnd('\\', '/');
                string dir = solution.TrimEnd('\\', '/');
                if (!dir.Equals(root, StringComparison.OrdinalIgnoreCase) && !dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return ($"The solution open in Visual Studio ({solution}) isn't in this repository, so it wasn't built.", true);
                }

                BuildOutcome outcome = await vs.BuildSolutionAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ErrorListItem> after = await vs.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                return ($"{outcome.Summary}\n{Diagnostics.Format(after, this.RepoRoot, string.Empty, includeWarnings: true)}", !outcome.Succeeded);

            case "get_debug_state":
                DebugState state = await vs.GetDebugStateAsync(cancellationToken).ConfigureAwait(false);
                return (DebugReport.Format(state, this.RepoRoot, input.ValueKind == JsonValueKind.Object && input.TryGetProperty("frames", out JsonElement framesEl) && framesEl.TryGetInt32(out int frames) ? frames : DebugReport.DefaultFrames), false);

            case "go_to_definition":
                return (await CodeTools.GoToDefinitionAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "find_references":
                return (await CodeTools.FindReferencesAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "find_symbols":
                return (await CodeTools.FindSymbolsAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "rename_symbol":
                return await this.RenameSymbolAsync(CodeTools.ToDictionary(input), cancellationToken).ConfigureAwait(false);

            case "list_packages":
                return (await PackageTools.ListAsync(CodeTools.ToDictionary(input), this.RepoRoot, cancellationToken).ConfigureAwait(false), false);

            case "add_package":
                return (await PackageTools.AddAsync(PackageTools.Parse(CodeTools.ToDictionary(input), withVersion: true), cancellationToken).ConfigureAwait(false), false);

            case "remove_package":
                return (await PackageTools.RemoveAsync(PackageTools.Parse(CodeTools.ToDictionary(input), withVersion: false), cancellationToken).ConfigureAwait(false), false);

            default:
                return ($"Unknown tool: {name}", true);
        }
    }

    /// <summary>
    /// rename_symbol for Claude Code: works the rename out, shows every file's changes in one approval, then writes them.
    /// (The permission prompt lets the tool through, as only now are the changes known.)
    /// </summary>
    private async Task<(string Text, bool IsError)> RenameSymbolAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var repoFiles = new RepoFiles(this.RepoRoot);
        RenamePlan plan = await CodeTools.PlanRenameAsync(input, repoFiles, this.RepoRoot, cancellationToken).ConfigureAwait(false);
        string title = CodeTools.Title(plan);
        IChatSink? chatSink = this.sink;

        if (this.Mode != AgentMode.Auto && !this.editsPreApproved)
        {
            if (chatSink is null)
            {
                return ("No one is available to approve this.", true);
            }

            await this.approvalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                chatSink.SetStatus("Waiting for your approval…");
                ApprovalResult result = await chatSink.RequestApprovalAsync(ActivityKind.Edit, title, CodeTools.Describe(plan, this.RepoRoot), "Allow all edits", cancellationToken);
                if (result.Decision == Approval.Deny)
                {
                    return ("The user declined this rename. Don't retry it; ask what they'd like instead.", true);
                }

                if (result.Decision == Approval.AllowAll)
                {
                    this.editsPreApproved = true;
                }
            }
            catch (OperationCanceledException)
            {
                return ("Cancelled by the user.", true);
            }
            finally
            {
                this.approvalGate.Release();
            }
        }

        // Writing checks for unsaved editor changes, which Visual Studio only answers on its main thread.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        IReadOnlyList<FileChange> changes = CodeTools.ApplyRename(plan, repoFiles, this.RepoRoot);
        foreach (FileChange change in changes)
        {
            chatSink?.ToolActivity(ActivityKind.Edit, $"Renamed in {this.Relative(change.FullPath)}", UnifiedDiff.Create(change.Before, change.After), change);
        }

        return ($"Renamed {plan.Symbol} to {plan.NewName} in {string.Join(", ", changes.Select(c => this.Relative(c.FullPath)))}. Check the result with get_diagnostics or build_solution.", false);
    }

    /// <summary>The approval card's title for add_package or remove_package.</summary>
    private static string PackageTitle(JsonElement input, bool remove)
    {
        try
        {
            return PackageTools.Title(PackageTools.Parse(CodeTools.ToDictionary(input), withVersion: !remove), remove);
        }
        catch (ToolInputException)
        {
            return remove ? "Remove a NuGet package" : "Add a NuGet package";
        }
    }

    private async Task<PermissionDecision> DecideCoreAsync(string toolName, JsonElement input, string? toolUseId)
    {
        // Reading the Error List, the debugger, the code model or the package list never needs asking; a build is treated like a command.
        if (toolName is ApprovalServer.DiagnosticsTool or ApprovalServer.DebugTool or ApprovalServer.DefinitionTool or ApprovalServer.ReferencesTool
            or ApprovalServer.SymbolsTool or ApprovalServer.ListPackagesTool)
        {
            return new PermissionDecision(true);
        }

        // A rename asks for approval itself, once its changes are known and shown together.
        if (toolName == ApprovalServer.RenameTool)
        {
            return this.Mode == AgentMode.Plan
                ? new PermissionDecision(false, "Plan mode is on, so nothing can be renamed. Describe the rename as text instead; the user can switch modes in Settings.")
                : new PermissionDecision(true);
        }

        if (toolName == ApprovalServer.BuildTool)
        {
            if (this.Mode == AgentMode.Plan)
            {
                return new PermissionDecision(false, "Plan mode is on, so the solution can't be built. The user can switch modes in Settings.");
            }

            if (this.Mode == AgentMode.Auto || CommandRules.IsToolAllowed(this.RepoRoot, "build_solution"))
            {
                return new PermissionDecision(true);
            }
        }

        if (toolName is ApprovalServer.AddPackageTool or ApprovalServer.RemovePackageTool)
        {
            if (this.Mode == AgentMode.Plan)
            {
                return new PermissionDecision(false, "Plan mode is on, so packages can't be changed. The user can switch modes in Settings.");
            }

            if (this.Mode == AgentMode.Auto || CommandRules.IsToolAllowed(this.RepoRoot, PackageTools.RuleKey))
            {
                return new PermissionDecision(true);
            }
        }

        // Writing to a file with unsaved changes in the editor would leave Visual Studio with two versions of it.
        if (EditTools.Contains(toolName) && this.Mode != AgentMode.Plan && this.FullPath(input) is string editPath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (OpenDocuments.HasUnsavedChanges(editPath))
            {
                return new PermissionDecision(false,
                    $"{this.Relative(editPath)} has unsaved changes in Visual Studio, so it wasn't changed. Ask the user to save or discard them, then read the file again.");
            }
        }

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

        bool isShell = toolName is "Bash" or "PowerShell";
        string? command = isShell ? Str(input, "command") : null;
        if (command != null && CommandRules.IsAllowed(this.RepoRoot, command, viaShell: true))
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
            FileChange? change = isEdit && this.FullPath(input) is string path
                ? FileChange.ForClaudeCodeTool(toolName, input, path, FileChange.ReadOrEmpty(path))
                : null;
            WithDiff(toolName, change, ref kind, ref detail);
            bool isBuild = toolName == ApprovalServer.BuildTool;
            bool isPackage = toolName is ApprovalServer.AddPackageTool or ApprovalServer.RemovePackageTool;
            string? rule = command is null ? null : CommandRules.RuleFor(command, viaShell: true);
            string? allowAllLabel = isEdit ? "Allow all edits" : isBuild ? "Always allow builds here" : isPackage ? "Always allow package changes here" : rule != null ? $"Always allow \"{rule}\" here" : null;

            // Only some of an edit can be kept if the extension can write the file itself: a text file inside the repository.
            bool canKeepSome = change != null && toolName != "NotebookEdit" && this.InsideRepo(change.FullPath);

            // The chat UI must be touched on Visual Studio's main thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.runToken);
            chatSink.SetStatus("Waiting for your approval…");
            ApprovalResult result = await chatSink.RequestApprovalAsync(kind, title, detail, allowAllLabel, this.runToken, change, canKeepSome);
            Approval approval = result.Decision;

            if (approval == Approval.AllowAll && isEdit)
            {
                this.editsPreApproved = true;
            }
            else if (approval == Approval.AllowAll && isBuild)
            {
                CommandRules.Allow(this.RepoRoot, "build_solution");
            }
            else if (approval == Approval.AllowAll && isPackage)
            {
                CommandRules.Allow(this.RepoRoot, PackageTools.RuleKey);
            }
            else if (approval == Approval.AllowAll && rule != null)
            {
                CommandRules.Allow(this.RepoRoot, rule);
            }

            if (approval != Approval.Deny && result.PartialAfter is string partial && change != null)
            {
                return this.WriteKeptChanges(change, partial, toolUseId);
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

    /// <summary>
    /// The user approved only some of an edit: the extension writes those changes itself (in the file's own encoding)
    /// and tells Claude Code no, with a message saying what was applied and what was left out.
    /// </summary>
    private PermissionDecision WriteKeptChanges(FileChange change, string kept, string? toolUseId)
    {
        string relative = this.Relative(change.FullPath);
        try
        {
            var repoFiles = new RepoFiles(this.RepoRoot);
            string resolved = repoFiles.Resolve(relative);
            System.Text.Encoding encoding = File.Exists(resolved) ? repoFiles.Read(relative).Encoding : new UTF8Encoding(false);
            if (!string.Equals(FileChange.ReadOrEmpty(resolved), change.Before, StringComparison.Ordinal) || OpenDocuments.HasUnsavedChanges(resolved))
            {
                return new PermissionDecision(false, $"{relative} changed while waiting for approval, so nothing was written. Read it again first.");
            }

            repoFiles.Write(resolved, kept, encoding);
        }
        catch (Exception ex) when (ex is ToolInputException or IOException or UnauthorizedAccessException)
        {
            return new PermissionDecision(false, $"The user approved part of this edit, but it couldn't be written: {ex.Message}");
        }

        if (toolUseId != null)
        {
            this.partlyApplied[toolUseId] = change with { After = kept };
        }

        return new PermissionDecision(false,
            $"The user applied only some of this change to {relative} (the extension wrote it, so this tool call itself was not run). " +
            $"They left out the changes below (a diff from what the file now contains to what you proposed):\n{UnifiedDiff.Create(kept, change.After)}\n" +
            "Don't make the left-out changes again unless the user asks for them. Read the file again before editing it further.");
    }

    private bool InsideRepo(string fullPath) =>
        fullPath.StartsWith(this.RepoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>How a pending permission request is shown on the approval card.</summary>
    private (ActivityKind Kind, string Title, string Detail) DescribeRequest(string tool, JsonElement input) => tool switch
    {
        ApprovalServer.BuildTool => (ActivityKind.Shell, "Build the solution in Visual Studio", string.Empty),
        ApprovalServer.AddPackageTool => (ActivityKind.Shell, PackageTitle(input, remove: false), string.Empty),
        ApprovalServer.RemovePackageTool => (ActivityKind.Shell, PackageTitle(input, remove: true), string.Empty),
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
    /// <param name="denied">True if the extension refused this call when Claude Code asked permission.</param>
    private (ActivityKind Kind, string Title, string Detail) Describe(string tool, JsonElement input, string result, bool isError, bool denied)
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
            "TodoWrite" => (ActivityKind.Other, "Updated the task list", TaskList.Format(TaskList.Parse(input))),
            ApprovalServer.DiagnosticsTool => (ActivityKind.Search, "Checked the Error List", result),
            ApprovalServer.DebugTool => (ActivityKind.Search, "Read the debugger's state", result),
            ApprovalServer.BuildTool => (result.StartsWith("Build succeeded", StringComparison.Ordinal) ? ActivityKind.Shell : ActivityKind.Failed, "Built the solution", result),
            ApprovalServer.DefinitionTool => (ActivityKind.Search, $"Go to definition of {Str(input, "symbol")}", result),
            ApprovalServer.ReferencesTool => (ActivityKind.Search, $"Find references to {Str(input, "symbol")}", result),
            ApprovalServer.SymbolsTool => (ActivityKind.Search, Str(input, "query") is { Length: > 0 } query ? $"Find symbols matching {query}" : $"Find symbols in {Str(input, "path")}", result),
            ApprovalServer.RenameTool => (ActivityKind.Edit, $"Rename {Str(input, "symbol")} to {Str(input, "new_name")}", result),
            ApprovalServer.ListPackagesTool => (ActivityKind.Search, "Listed NuGet packages", result),
            ApprovalServer.AddPackageTool => (ActivityKind.Shell, PackageTitle(input, remove: false), result),
            ApprovalServer.RemovePackageTool => (ActivityKind.Shell, PackageTitle(input, remove: true), result),
            "AskUserQuestion" => (ActivityKind.Question, "Asked you " + QuestionCount(input), result),
            "Task" or "Agent" => (ActivityKind.Other, $"Subagent: {Str(input, "description") ?? "task"}", result),
            _ => (ActivityKind.Other, tool, result),
        };

        if (isError)
        {
            // Without the call's ID (older Claude Code), go by the wording of the refusals: the extension's own, and
            // Claude Code's for permission rules. A plain "denied" is usually an error, e.g. "Access denied".
            bool declined = denied
                || result.IndexOf("declined", StringComparison.OrdinalIgnoreCase) >= 0
                || result.IndexOf("Cancelled by the user", StringComparison.OrdinalIgnoreCase) >= 0
                || result.IndexOf("Permission to use", StringComparison.OrdinalIgnoreCase) >= 0;
            return (declined ? ActivityKind.Declined : ActivityKind.Failed, title, result);
        }

        return (kind, title, detail);
    }

    /// <summary>The absolute path an edit tool's input refers to, or null if it has none.</summary>
    private string? FullPath(JsonElement input)
    {
        string? path = Str(input, "file_path") ?? Str(input, "notebook_path");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(this.RepoRoot, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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

    /// <summary>
    /// When the whole file before and after is known, shows the edit as a diff with context instead of the tool's
    /// old and new strings. A new file stays as its contents.
    /// </summary>
    private static void WithDiff(string tool, FileChange? change, ref ActivityKind kind, ref string detail)
    {
        if (change is null || (tool == "Write" && change.Before.Length == 0))
        {
            return;
        }

        kind = ActivityKind.Edit;
        detail = UnifiedDiff.Create(change.Before, change.After);
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

    internal readonly record struct RunOutcome(string? Error);
}
