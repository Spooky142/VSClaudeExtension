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

/// <summary>Running the tools Claude asks for, one at a time or in parallel.</summary>
internal sealed partial class ClaudeAgent
{
    /// <summary>Runs one tool call, turning failures into an error result for Claude.</summary>
    private async Task<ToolResultBlockParam> RunToolAsync(ToolUseBlockParam toolUse, IChatSink sink, CancellationToken cancellationToken)
    {
        bool changesFiles = this.CanChangeFiles(toolUse);
        if (changesFiles)
        {
            this.ForgetRepoFiles(); // it may write files (or run something that does)
        }

        try
        {
            if (await this.RunPreToolHooksAsync(toolUse, sink, cancellationToken) is string blockedBy)
            {
                return Error(toolUse.ID, blockedBy);
            }

            string output = await this.ExecuteToolAsync(toolUse, sink, cancellationToken);
            output += await this.RunPostToolHooksAsync(toolUse, output, sink, cancellationToken);
            return new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = output };
        }
        catch (OperationCanceledException)
        {
            return Error(toolUse.ID, "Cancelled by the user.");
        }
        catch (Exception ex)
        {
            // Any failure (an API error in a subagent, a process that won't start, a regex timeout) must still answer the
            // tool call: a tool_use left without a tool_result makes every later request in this chat fail.
            sink.ToolActivity(ActivityKind.Failed, $"{toolUse.Name} failed", ex.Message);
            return Error(toolUse.ID, ex.Message);
        }
        finally
        {
            if (changesFiles)
            {
                this.ForgetRepoFiles(); // in case a glob or grep (in a subagent, say) listed them while it ran
            }
        }
    }

    /// <summary>
    /// Tools that may add or remove files, directly or by running something that does, after which the repository's
    /// files are listed afresh. Hooks can too, but they run around any tool, so they're left to the listing's lifetime.
    /// </summary>
    private bool CanChangeFiles(ToolUseBlockParam toolUse)
    {
        if (this.mcpTools.TryGetValue(toolUse.Name, out var mcp))
        {
            return !mcp.Tool.ReadOnly;
        }

        return toolUse.Name switch
        {
            "edit_file" or "write_file" or "delete_file" or "move_file" or "run_command" or "run_tests" or "build_solution"
                or "rename_symbol" or "apply_code_fix" or "add_package" or "remove_package" => true,
            "git" => !(toolUse.Input.TryGetValue("args", out JsonElement args)
                && args.ValueKind == JsonValueKind.Array
                && args.EnumerateArray().All(a => a.ValueKind == JsonValueKind.String)
                && GitRunner.IsReadOnly(args.EnumerateArray().Select(a => a.GetString()!).ToList())),
            _ => false,
        };
    }

    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Runs read-only tool calls at the same time, on background threads. What each reports is held back and then
    /// shown in the order Claude asked for them, so the transcript reads the same as if they'd run one by one.
    /// </summary>
    private async Task<ToolResultBlockParam[]> RunInParallelAsync(List<ToolUseBlockParam> toolUses, IChatSink sink, CancellationToken cancellationToken)
    {
        sink.SetStatus($"Running {toolUses.Count} read-only tools…");
        List<DeferredSink> deferred = toolUses.Select(_ => new DeferredSink()).ToList();
        Task<ToolResultBlockParam[]> running = Task.WhenAll(
            toolUses.Select((toolUse, i) => Task.Run(async () =>
            {
                try
                {
                    return await this.RunToolAsync(toolUse, deferred[i], cancellationToken);
                }
                finally
                {
                    deferred[i].Finish();
                }
            })));

        // A subagent can run for minutes; what each one is doing shows meanwhile (from this thread, which the sink needs).
        while (await Task.WhenAny(running, Task.Delay(StatusPollInterval)) != running)
        {
            List<string> doing = deferred.Select(d => d.Status).OfType<string>().ToList();
            if (doing.Count > 0)
            {
                sink.SetStatus(doing.Count == 1 ? doing[0] : $"{doing[0]} (+{doing.Count - 1} more running)");
            }

            foreach (DeferredSink tool in deferred)
            {
                tool.ShowLive(sink);
            }
        }

        ToolResultBlockParam[] results = await running;
        foreach (DeferredSink tool in deferred)
        {
            tool.ShowLive(sink); // removes the rows of tools that have finished, which is all of them now
            tool.ReplayTo(sink);
        }

        return results;
    }

    /// <summary>Tools that only read and never ask the user anything, so they can run alongside each other.</summary>
    private bool CanRunInParallel(ToolUseBlockParam toolUse) => toolUse.Name switch
    {
        "glob" or "grep" or "task" or "go_to_definition" or "find_references" or "find_symbols" or "get_code_fixes" or "list_packages" => true,

        // Unless a permission rule says to ask first.
        "read_file" => !(toolUse.Input.TryGetValue("path", out JsonElement path) && path.ValueKind == JsonValueKind.String
            && this.ReadVerdict(path.GetString()!) == PermissionVerdict.Ask),
        "git" => toolUse.Input.TryGetValue("args", out JsonElement args)
            && args.ValueKind == JsonValueKind.Array
            && args.EnumerateArray().All(a => a.ValueKind == JsonValueKind.String)
            && args.GetArrayLength() > 0
            && GitRunner.IsReadOnly(args.EnumerateArray().Select(a => a.GetString()!).ToList())
            && this.permissions.ForCommand("git " + string.Join(" ", args.EnumerateArray().Select(a => a.GetString()!))) is PermissionVerdict.None or PermissionVerdict.Allow,
        _ => false,
    };

    /// <summary>What the permission rules say about reading a repository path; None if the path isn't valid.</summary>
    private PermissionVerdict ReadVerdict(string path)
    {
        try
        {
            return this.permissions.ForRead(this.files.Resolve(path));
        }
        catch (ToolInputException)
        {
            return PermissionVerdict.None;
        }
    }

    private Task<string> ExecuteToolAsync(ToolUseBlockParam toolUse, IChatSink sink, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonElement> input = toolUse.Input;
        if (this.mcpTools.TryGetValue(toolUse.Name, out var mcp))
        {
            return this.CallMcpToolAsync(toolUse.Name, mcp.Client, mcp.Tool, input, sink, cancellationToken);
        }

        return toolUse.Name switch
        {
            "git" => this.RunGitAsync(input, sink, cancellationToken),
            "glob" => this.GlobAsync(input, sink, cancellationToken),
            "grep" => this.GrepAsync(input, sink, cancellationToken),
            "read_file" => this.ReadFileAsync(input, sink, cancellationToken),
            "edit_file" => this.EditFileAsync(input, sink, cancellationToken),
            "write_file" => this.WriteFileAsync(input, sink, cancellationToken),
            "delete_file" => this.DeleteFileAsync(input, sink, cancellationToken),
            "move_file" => this.MoveFileAsync(input, sink, cancellationToken),
            "run_command" => this.RunCommandAsync(input, sink, cancellationToken),
            "run_tests" => this.RunTestsAsync(input, sink, cancellationToken),
            "get_diagnostics" => this.GetDiagnosticsAsync(input, sink, cancellationToken),
            "build_solution" => this.BuildSolutionAsync(sink, cancellationToken),
            "get_debug_state" => this.GetDebugStateAsync(input, sink, cancellationToken),
            "get_active_document" => this.GetActiveDocumentAsync(sink, cancellationToken),
            "go_to_definition" => this.GoToDefinitionAsync(input, sink, cancellationToken),
            "find_references" => this.FindReferencesAsync(input, sink, cancellationToken),
            "find_symbols" => this.FindSymbolsAsync(input, sink, cancellationToken),
            "rename_symbol" => this.RenameSymbolAsync(input, sink, cancellationToken),
            "get_code_fixes" => this.GetCodeFixesAsync(input, sink, cancellationToken),
            "apply_code_fix" => this.ApplyCodeFixAsync(input, sink, cancellationToken),
            "list_packages" => this.ListPackagesAsync(input, sink, cancellationToken),
            "add_package" => this.AddPackageAsync(input, sink, cancellationToken),
            "remove_package" => this.RemovePackageAsync(input, sink, cancellationToken),
            "task" => this.RunSubagentAsync(input, sink, cancellationToken),
            "todo_write" => Task.FromResult(WriteTodos(input, sink)),
            "ask_user" => AskUserAsync(toolUse.Input, sink, cancellationToken),
            _ => throw new ToolInputException($"Unknown tool: {toolUse.Name}"),
        };
    }

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

    /// <summary>
    /// Stands in for the chat while a tool runs on a background thread: what it reports is kept, to be replayed on
    /// the UI thread later, and the rows it shows while it runs are brought up to date from there (see
    /// <see cref="ShowLive"/>). Only for tools that never ask the user anything.
    /// </summary>
    private sealed class DeferredSink : IChatSink
    {
        private readonly List<Action<IChatSink>> calls = [];
        private readonly List<DeferredLive> live = [];

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

        private volatile string? status;
        private volatile bool finished;

        /// <summary>What the tool is doing now, for the batch's status line; null once it's done.</summary>
        public string? Status => this.finished ? null : this.status;

        public void Finish() => this.finished = true;

        // Not replayed: each tool's "Reading…" would be stale by then. It's only kept for the batch's status while the tool runs.
        public void SetStatus(string? status) => this.status = status;

        public ILiveActivity StartLiveActivity(ActivityKind kind, string title, Action? stop, bool expanded = false)
        {
            var row = new DeferredLive(kind, title, stop, expanded);
            lock (this.live)
            {
                this.live.Add(row);
            }

            return row;
        }

        /// <summary>Shows the tool's rows in the chat with their output so far, and removes finished ones. On the UI thread.</summary>
        public void ShowLive(IChatSink sink)
        {
            DeferredLive[] rows;
            lock (this.live)
            {
                rows = [.. this.live];
            }

            foreach (DeferredLive row in rows)
            {
                if (row.Show(sink))
                {
                    lock (this.live)
                    {
                        this.live.Remove(row);
                    }
                }
            }
        }

        public void ReportTokenUsage(ClaudeModel model, TokenUsage usage) => this.Add(s => s.ReportTokenUsage(model, usage));

        public void ReportPromptUsage(TokenUsage usage) => this.Add(s => s.ReportPromptUsage(usage));

        public void ReportLiveTokens(long totalTokens) => this.Add(s => s.ReportLiveTokens(totalTokens));

        public void ReportContext(long tokens) => this.Add(s => s.ReportContext(tokens));

        public void ReportCompacted(long tokensBefore) => this.Add(s => s.ReportCompacted(tokensBefore));

        public void ReportApiRateLimits(ApiRateLimits limits) => this.Add(s => s.ReportApiRateLimits(limits));

        public void ReportPlanLimit(PlanLimit limit) => this.Add(s => s.ReportPlanLimit(limit));

        public Task<IReadOnlyDictionary<string, string>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Tools running alongside others can't ask the user anything.");

        public Task<ApprovalResult> RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change = null, bool canKeepSome = false)
            => throw new InvalidOperationException("Tools running alongside others can't ask for approval.");

        public void ReportTasks(IReadOnlyList<TaskItem> tasks) => this.Add(s => s.ReportTasks(tasks));

        private void Add(Action<IChatSink> call)
        {
            lock (this.calls)
            {
                this.calls.Add(call);
            }
        }

        /// <summary>A row started on the tool's thread; the chat's own row is made, updated and removed by <see cref="Show"/>.</summary>
        private sealed class DeferredLive(ActivityKind kind, string title, Action? stop, bool expanded) : ILiveActivity
        {
            private volatile string? output;
            private volatile bool disposed;

            // UI thread only.
            private ILiveActivity? shown;
            private string? shownOutput;

            public void SetOutput(string output) => this.output = output;

            public void Dispose() => this.disposed = true;

            /// <summary>Brings the chat's row up to date; returns true once it's gone for good.</summary>
            public bool Show(IChatSink sink)
            {
                if (this.disposed)
                {
                    this.shown?.Dispose();
                    this.shown = null;
                    return true;
                }

                // Stop can be pressed just after the tool finished, before its row is removed; by then there's nothing to stop.
                this.shown ??= sink.StartLiveActivity(kind, title, stop is null ? null : () => { if (!this.disposed) stop(); }, expanded);
                string? now = this.output;
                if (now != null && !ReferenceEquals(now, this.shownOutput))
                {
                    this.shown.SetOutput(now);
                    this.shownOutput = now;
                }

                return false;
            }
        }
    }
}
