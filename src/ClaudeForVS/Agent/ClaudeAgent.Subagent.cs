using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Messages;

namespace ClaudeForVS.Agent;

/// <summary>
/// The <c>task</c> tool: Claude hands a research job to a subagent that has its own conversation, so all the files it
/// reads and searches it runs stay out of the main context. It can only read, and it comes back with one report.
/// </summary>
internal sealed partial class ClaudeAgent
{
    private const int MaxSubagentRounds = 25;
    private const long SubagentMaxTokens = 32_000;
    private const int MaxSubagentReportChars = 30_000;

    // What a subagent can use: tools that only read.
    private static readonly HashSet<string> SubagentTools = new(StringComparer.Ordinal) { "glob", "grep", "read_file", "git", "web_search", "web_fetch" };

    private const string SubagentInstructions =
        "\n\n" + """
        You are now a research subagent, started by another instance of Claude that is working with the user. You can't
        see their conversation, and you can only read: `glob`, `grep`, `read_file`, read-only `git` commands and the web
        tools. Don't try to change anything. Investigate what the prompt asks and finish with one report that stands on
        its own: what you found, with file paths and line numbers, and facts rather than guesses. If you couldn't find
        something, say so. Be concise; your report is all that comes back.
        """;

    private static string? ToolName(ToolUnion tool) =>
        tool.TryPickTool(out Tool? plain) ? plain.Name
        : tool.TryPickWebSearchTool20250305(out _) ? "web_search"
        : tool.TryPickWebFetchTool20250910(out _) ? "web_fetch"
        : null;

    private async Task<string> RunSubagentAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string description = RequireString(input, "description").Trim();
        string prompt = RequireString(input, "prompt");
        string title = $"Subagent: {description}";

        IChatSink inner = new SubagentSink(sink, description);
        List<ToolUnion> tools = (this.tools ?? BuiltInTools(this.permissions)).Where(t => ToolName(t) is string name && SubagentTools.Contains(name)).ToList();
        string system = this.systemPrompt + SubagentInstructions;
        var conversation = new List<MessageParam> { new() { Role = Role.User, Content = prompt } };

        sink.SetStatus($"Running a subagent: {description}…");
        List<ContentBlockParam>? paused = null;
        int toolCalls = 0;
        int round = 0;
        string report = string.Empty;
        bool atLimit = false;
        for (; round < MaxSubagentRounds; round++)
        {
            // A subagent can't ask the user (it may be running beside others), so it stops itself at the limit and the
            // main conversation, which can ask, takes it from there.
            if (round > 0 && this.OverSpendLimit)
            {
                atLimit = true;
                break;
            }

            StreamedTurn turn = await this.StreamTurnAsync(inner, 0, cancellationToken, this.BuildRequest(null, SubagentMaxTokens, conversation, system, tools));
            this.ReportUsage(sink, new TokenUsage(turn.UncachedInputTokens, turn.CacheWriteTokens, turn.CacheReadTokens, turn.OutputTokens, turn.WebSearches));
            if (paused != null)
            {
                // A response the API paused (during long web searches) continues as the same assistant message.
                conversation.RemoveAt(conversation.Count - 1);
                turn.Content.InsertRange(0, paused);
                paused = null;
            }

            conversation.Add(new() { Role = Role.Assistant, Content = turn.Content });
            if (turn.StopReason == "pause_turn")
            {
                paused = turn.Content;
                continue;
            }

            report = string.Concat(turn.Content.Select(block => block.TryPickText(out TextBlockParam? text) ? text.Text : string.Empty)).Trim();
            if (turn.ToolUses.Count == 0 || turn.StopReason is "refusal" or "max_tokens")
            {
                break;
            }

            // A subagent only reads, so everything it's allowed to run can run at once.
            List<ToolUseBlockParam> allowed = turn.ToolUses.Where(use => SubagentTools.Contains(use.Name) && this.CanRunInParallel(use)).ToList();
            toolCalls += allowed.Count;
            Dictionary<string, ToolResultBlockParam> ran = allowed.Count == 0
                ? []
                : (await this.RunInParallelAsync(allowed, inner, cancellationToken)).ToDictionary(r => r.ToolUseID);
            var results = new List<ContentBlockParam>();
            foreach (ToolUseBlockParam use in turn.ToolUses)
            {
                results.Add(ran.TryGetValue(use.ID, out ToolResultBlockParam? result)
                    ? result
                    : Error(use.ID, SubagentTools.Contains(use.Name)
                        ? $"`{use.Name}` needs the user's approval for this, which a subagent can't ask for. Leave it out, or say in your report that the main conversation should do it."
                        : $"`{use.Name}` isn't available to a subagent, which can only read and search."));
            }

            conversation.Add(new() { Role = Role.User, Content = results });
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Why it stopped short, if it did: its last turn may have been a tool call with no text, so there may be no report at all.
        string? cut = atLimit ? "stopped early because this chat reached its spending limit"
            : round >= MaxSubagentRounds ? $"stopped after {MaxSubagentRounds} rounds"
            : null;
        if (cut != null)
        {
            report = (report + $"\n\n[The subagent {cut}, so this may be incomplete.]").Trim();
        }
        else if (report.Length == 0)
        {
            report = "The subagent finished without writing a report.";
        }

        if (report.Length > MaxSubagentReportChars)
        {
            report = report.Substring(0, MaxSubagentReportChars) + "\n[Report truncated.]";
        }

        sink.ToolActivity(ActivityKind.Other, title, $"{(round + 1 == 1 ? "1 round" : $"{round + 1} rounds")}, {(toolCalls == 1 ? "1 tool call" : $"{toolCalls} tool calls")}\n\n{report}");
        sink.SetStatus("Waiting for Claude…");
        return report;
    }

    /// <summary>
    /// Stands in for the chat while a subagent works: its answer and reasoning stay out of the transcript (the report is
    /// shown when it's done), its tools show up marked as the subagent's, and its token use counts towards the chat's.
    /// </summary>
    private sealed class SubagentSink(IChatSink chat, string description) : IChatSink
    {
        public void AppendText(string text)
        {
        }

        public void AppendThinking(string text)
        {
        }

        public void ToolActivity(ActivityKind kind, string title, string detail, FileChange? change = null) =>
            chat.ToolActivity(kind, "↳ " + title, detail, change);

        public ILiveActivity StartLiveActivity(ActivityKind kind, string title, Action stop) => chat.StartLiveActivity(kind, "↳ " + title, stop);

        public void SetStatus(string? status) => chat.SetStatus(status is null ? null : $"Subagent ({description}): {status}");

        public void ReportTokenUsage(ClaudeModel model, TokenUsage usage) => chat.ReportTokenUsage(model, usage);

        public void ReportPromptUsage(TokenUsage usage) => chat.ReportPromptUsage(usage);

        // The subagent's context isn't the chat's, so none of its size or progress counts shows.
        public void ReportLiveTokens(long totalTokens)
        {
        }

        public void ReportContext(long tokens)
        {
        }

        public void ReportCompacted(long tokensBefore)
        {
        }

        public void ReportApiRateLimits(ApiRateLimits limits) => chat.ReportApiRateLimits(limits);

        public void ReportPlanLimit(PlanLimit limit) => chat.ReportPlanLimit(limit);

        public Task<IReadOnlyDictionary<string, string>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken)
            => chat.AskQuestionsAsync(questions, cancellationToken);

        public Task<ApprovalResult> RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change = null, bool canKeepSome = false)
            => chat.RequestApprovalAsync(kind, title, detail, allowAllLabel, cancellationToken, change, canKeepSome);

        public void ReportTasks(IReadOnlyList<TaskItem> tasks)
        {
        }
    }
}
