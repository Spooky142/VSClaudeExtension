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
internal sealed partial class ClaudeCodeEngine : IChatEngine
{
    private const string ExtraSystemPrompt =
        "You are running inside a Visual Studio extension's chat window, which renders your replies as Markdown. " +
        "Keep answers concise and reference files by repository-relative paths. " +
        "The " + ApprovalServer.DiagnosticsTool + " tool lists Visual Studio's Error List (build errors and live IntelliSense errors in open files); " +
        "check it after changing code. " + ApprovalServer.BuildTool + " builds the open solution in Visual Studio, which also works for " +
        "projects `dotnet build` can't build. " + ApprovalServer.DebugTool + " reads Visual Studio's debugger (the exception, call stack and locals when the " +
        "user has paused at a breakpoint or exception). " + ApprovalServer.ActiveDocumentTool + " shows the file, caret line and selection the user " +
        "has in the editor; use it when they say \"this\" or \"here\" without naming a file. " + ApprovalServer.CodeFixesTool + " lists the light bulb's " +
        "fixes for a line's errors and warnings, and " + ApprovalServer.ApplyCodeFixTool + " applies one; prefer them to hand edits for things like " +
        "missing usings or unimplemented interface members. " + ApprovalServer.DefinitionTool + ", " + ApprovalServer.ReferencesTool + " and " + ApprovalServer.SymbolsTool +
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

    // The rows of subagents at work, by the ID of the tool call that started each.
    private readonly Dictionary<string, SubagentProgress> subagents = new();

    private bool editsPreApproved;

    // Set by Rewind: the next message resumes the session as it was at this message, in a forked copy.
    private string? resumeAt;
    private IChatSink? sink;
    private CancellationToken runToken;

    // One Claude Code process answers every message in the chat, reading them from stdin, so each message doesn't
    // pay for starting it and reconnecting the approval server. It's replaced when something on its command line
    // changes (mode, effort, subagent model, MCP servers turned off, lower priority), after a rewind, or after it was stopped.
    private Process? process;
    private Task<string>? processStderr;
    private (AgentMode Mode, EffortLevel Effort, ClaudeModel? SubagentModel, string DisabledMcp, bool LowPriority) processSettings;
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
                // Claude Code gives up on a call to an HTTP server that's quiet for 5 minutes, and the approval tool is
                // quiet for as long as an approval or question waits for the user. The server's own timeout raises that.
                [ApprovalServer.ServerName] = new { type = "http", url = this.approvalServer.Url, timeout = ApprovalServer.CallTimeoutMilliseconds },
            },
        }));
    }

    public ChatEngineKind Kind => ChatEngineKind.ClaudeCode;

    public string RepoRoot { get; }

    public ClaudeModel Model { get; }

    public EffortLevel Effort { get; set; }

    public AgentMode Mode { get; set; }

    /// <summary>The model Claude Code's subagents run on; null leaves it to Claude Code. Used from the next message on.</summary>
    public ClaudeModel? SubagentModel { get; set; }

    /// <summary>The model ID Claude Code reported running for <see cref="Model"/>'s alias, e.g. "claude-opus-5-5"; null before it starts.</summary>
    public string? ResolvedModelId { get; private set; }

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
                _ = Task.Run(() => Processes.KillTree(process));
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
        catch (Exception)
        {
            // The rest of this reply, its result included, is still unread in the pipe, and the next message would take
            // that result as its own. Start that one in a new process (which resumes the session) instead.
            pollCts.Cancel();
            await this.StopProcessAsync();
            throw;
        }
        finally
        {
            pollCts.Cancel();
            this.EndSubagents();
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
                    Processes.KillTree(running);
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

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static string Pretty(JsonElement element) => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    internal readonly record struct RunOutcome(string? Error);
}
