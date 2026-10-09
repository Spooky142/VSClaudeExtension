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

/// <summary>Reading Claude Code's stream-json events: the reply, tool calls and results, token counts and plan usage.</summary>
internal sealed partial class ClaudeCodeEngine
{
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
                foreach (PlanLimit limit in ToPlanLimits(info))
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

    /// <summary>
    /// Converts Claude Code's rate_limit_info (claude.ai plan usage) into <see cref="PlanLimit"/>s: the window the event
    /// is about, plus every window in "unifiedWindows", which carries exact figures even when the event itself has none.
    /// </summary>
    private static List<PlanLimit> ToPlanLimits(JsonElement info)
    {
        var limits = new Dictionary<string, PlanLimit>(StringComparer.Ordinal);
        if (info.TryGetProperty("unifiedWindows", out JsonElement windows) && windows.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty window in windows.EnumerateObject())
            {
                double? utilization = Utilization(window.Value);
                string status = utilization >= 100 ? "rejected" : "allowed";
                limits[window.Name] = new PlanLimit(window.Name, utilization, ResetsAt(window.Value), status, DateTimeOffset.Now);
            }
        }

        if (Str(info, "rateLimitType") is string type)
        {
            limits.TryGetValue(type, out PlanLimit? unified);
            limits[type] = new PlanLimit(
                type,
                Utilization(info) ?? unified?.Utilization,
                ResetsAt(info) ?? unified?.ResetsAt,
                Str(info, "status") ?? "allowed",
                DateTimeOffset.Now);
        }

        return [.. limits.Values];

        static double? Utilization(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("utilization", out JsonElement u) || u.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            // The event doesn't document its scale (the /usage data is 0-100); treat values up to 1 as a fraction.
            double value = u.GetDouble();
            return Math.Max(0, Math.Min(100, value <= 1 ? value * 100 : value));
        }

        static DateTimeOffset? ResetsAt(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("resetsAt", out JsonElement r) || r.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            long epoch = r.TryGetInt64(out long whole) ? whole : (long)r.GetDouble(); // e.g. 1760000000.5
            return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
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
}
