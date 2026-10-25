using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.Git;
using ApiEffort = Anthropic.Models.Messages.Effort;

namespace ClaudeForVS.Agent;

/// <summary>Compaction, mode notes and building the Messages API request.</summary>
internal sealed partial class ClaudeAgent
{
    /// <summary>
    /// Once the context passes <see cref="CompactAtTokens"/> or <see cref="CompactAt"/> (or a full-length reply would no longer fit), has Claude summarize the conversation and restarts it from that
    /// summary. The request reuses the normal request with the instruction appended, so the history is read from the
    /// cache. Returns the tokens the summary took to write (counted towards the prompt's total), or 0 if it didn't compact.
    /// </summary>
    private async Task<long> CompactIfNeededAsync(IChatSink sink, bool midTask, CancellationToken cancellationToken, bool force = false)
    {
        long window = AgentSettings.ContextWindow;
        double threshold = Math.Min(window * CompactAt, window - MaxOutputTokens - CompactHeadroom);
        if (this.CompactAtTokens > 0)
        {
            threshold = Math.Min(threshold, this.CompactAtTokens);
        }

        // The latest response's count doesn't include the tool results sent since, which can be large.
        long context = this.contextTokens + PendingTokens(this.history);
        if ((!force && context < threshold) || this.history.Count == 0)
        {
            return 0;
        }

        sink.SetStatus(force ? "Summarizing the conversation…" : "Context is getting long · summarizing the conversation…");

        // The summary is asked for after those tool results, so if they'd leave too little room for it they're cut first.
        const long MinSummaryTokens = 4_000;
        if (context > window - MinSummaryTokens - 2_000)
        {
            this.TrimLatestToolResults(this.history, (context - (window - MinSummaryTokens - 2_000)) * 4);
            context = this.contextTokens + PendingTokens(this.history);
        }

        // The reply has to fit in what's left of the window.
        long maxTokens = Math.Max(MinSummaryTokens, Math.Min(16_000, window - context - 2_000));

        var summary = new StringBuilder();
        long newTokens = 0;

        // Reported once, even if stopped part-way, so the cost estimate knows the prompt's length when it prices the output.
        TokenUsage used = default;
        try
        {
            await foreach (RawMessageStreamEvent ev in this.StreamFittingAsync(
                this.history, () => this.BuildRequest(new() { Role = Role.User, Content = CompactInstruction }, maxTokens), cancellationToken))
            {
                if (ev.TryPickStart(out RawMessageStartEvent? start))
                {
                    used = new TokenUsage(
                        start.Message.Usage.InputTokens,
                        start.Message.Usage.CacheCreationInputTokens ?? 0,
                        start.Message.Usage.CacheReadInputTokens ?? 0,
                        0,
                        CacheWrite1h: start.Message.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0);
                }
                else if (ev.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta) && delta.Delta.TryPickText(out TextDelta? text))
                {
                    summary.Append(text.Text);
                }
                else if (ev.TryPickDelta(out RawMessageDeltaEvent? messageDelta))
                {
                    newTokens += messageDelta.Usage.OutputTokens;
                    used += new TokenUsage(0, 0, 0, messageDelta.Usage.OutputTokens);
                }
            }
        }
        finally
        {
            if (used != default)
            {
                this.ReportUsage(sink, used);
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
        this.conversationEffort = this.Effort; // the cache starts again anyway
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

    /// <summary>What a tool result too large for the context window is replaced with. Internal for tests.</summary>
    internal const string RemovedResult = "[result removed: too large for the context window; ask for a narrower range]";

    /// <summary>
    /// Streams a request built from <paramref name="messages"/>. If the API says the prompt is too long, the largest tool
    /// results in the latest round are replaced with <see cref="RemovedResult"/> and the request is sent once more.
    /// </summary>
    private async IAsyncEnumerable<RawMessageStreamEvent> StreamFittingAsync(
        List<MessageParam> messages, Func<MessageCreateParams> request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The API refuses a prompt before streaming anything, so the retry can only come before the first event.
        IAsyncEnumerator<RawMessageStreamEvent> events = this.client.Messages.CreateStreaming(request(), cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool more;
        try
        {
            more = await events.MoveNextAsync();
        }
        catch (AnthropicBadRequestException ex) when (IsPromptTooLong(ex))
        {
            await events.DisposeAsync();
            if (!this.TrimLatestToolResults(messages, ExcessChars(ex)))
            {
                throw;
            }

            events = this.client.Messages.CreateStreaming(request(), cancellationToken).GetAsyncEnumerator(cancellationToken);
            more = await events.MoveNextAsync();
        }

        try
        {
            while (more)
            {
                yield return events.Current;
                more = await events.MoveNextAsync();
            }
        }
        finally
        {
            await events.DisposeAsync();
        }
    }

    /// <summary>
    /// <see cref="ShrinkLatestToolResults"/>, then for the chat's own conversation, forgets the reads whose results were
    /// in that message, so reading those files again sends their text rather than a note pointing back to it.
    /// </summary>
    private bool TrimLatestToolResults(List<MessageParam> messages, long charsToFree)
    {
        if (!ShrinkLatestToolResults(messages, charsToFree))
        {
            return false;
        }

        if (messages == this.history)
        {
            lock (this.reads)
            {
                foreach (string file in this.reads.Where(r => r.Value.Index >= messages.Count - 1).Select(r => r.Key).ToList())
                {
                    this.reads.Remove(file);
                }
            }
        }

        return true;
    }

    private static bool IsPromptTooLong(AnthropicBadRequestException ex) =>
        (ex.ResponseBody ?? string.Empty).IndexOf("prompt is too long", StringComparison.OrdinalIgnoreCase) >= 0
        || ex.Message.IndexOf("prompt is too long", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Roughly how many characters have to go, from the API's "prompt is too long: 215000 tokens &gt; 200000 maximum"
    /// (with some to spare); 0 if it doesn't say, meaning every large result goes.
    /// </summary>
    internal static long ExcessChars(AnthropicBadRequestException ex) => ExcessChars((ex.ResponseBody ?? string.Empty) + " " + ex.Message);

    internal static long ExcessChars(string error)
    {
        Match counts = Regex.Match(error, @"(\d+)\s*tokens\s*>\s*(\d+)");
        return counts.Success && long.TryParse(counts.Groups[1].Value, out long sent) && long.TryParse(counts.Groups[2].Value, out long allowed) && sent > allowed
            ? (sent - allowed + CompactHeadroom) * 4
            : 0;
    }

    /// <summary>
    /// Estimated tokens in the messages after the latest response, which its token count doesn't include: the tool
    /// results (and any text) sent since, at about 4 characters a token. Internal for tests.
    /// </summary>
    internal static long PendingTokens(List<MessageParam> messages)
    {
        long chars = 0;
        for (int i = messages.Count - 1; i >= 0 && RoleOf(messages[i]) != "assistant"; i--)
        {
            chars += messages[i].RawData.TryGetValue("content", out JsonElement content) ? ContentChars(content) : 0;
        }

        return chars / 4;
    }

    private static string RoleOf(MessageParam message) =>
        message.RawData.TryGetValue("role", out JsonElement role) && role.ValueKind == JsonValueKind.String ? role.GetString()! : string.Empty;

    /// <summary>The characters of text and tool results in a message's content; images and the like aren't counted.</summary>
    private static long ContentChars(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!.Length;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        long chars = 0;
        foreach (JsonElement block in content.EnumerateArray())
        {
            string type = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
            if (type == "text" && block.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                chars += text.GetString()!.Length;
            }
            else if (type == "tool_result" && block.TryGetProperty("content", out JsonElement result))
            {
                chars += ContentChars(result);
            }
        }

        return chars;
    }

    /// <summary>
    /// Replaces the largest tool results in the last message (the latest round's) with <see cref="RemovedResult"/>,
    /// biggest first, until about <paramref name="charsToFree"/> characters have gone (all but small ones when it's 0).
    /// Returns false if there was nothing to replace. Internal for tests.
    /// </summary>
    internal static bool ShrinkLatestToolResults(List<MessageParam> messages, long charsToFree)
    {
        const int MinRemovedChars = 1_000;
        if (messages.Count == 0 || RoleOf(messages[messages.Count - 1]) != "user"
            || !messages[messages.Count - 1].RawData.TryGetValue("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array
            || JsonNode.Parse(content.GetRawText()) is not JsonArray blocks)
        {
            return false;
        }

        var largest = blocks
            .Select((block, index) => (Index: index, Block: block, Chars: block?["type"]?.GetValueKind() == JsonValueKind.String && block["type"]!.GetValue<string>() == "tool_result"
                ? ContentChars(JsonSerializer.SerializeToElement(block["content"]))
                : 0))
            .Where(r => r.Chars >= MinRemovedChars)
            .OrderByDescending(r => r.Chars)
            .ToList();
        if (largest.Count == 0)
        {
            return false;
        }

        long freed = 0;
        foreach ((int index, JsonNode? block, long chars) in largest)
        {
            if (charsToFree > 0 && freed >= charsToFree)
            {
                break;
            }

            blocks[index] = new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = block!["tool_use_id"]?.DeepClone(),
                ["content"] = RemovedResult,
                ["is_error"] = true,
            };
            freed += chars;
        }

        var raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> pair in messages[messages.Count - 1].RawData)
        {
            raw[pair.Key] = pair.Value;
        }

        raw["content"] = JsonSerializer.SerializeToElement(blocks);
        messages[messages.Count - 1] = MessageParam.FromRawUnchecked(raw);
        return true;
    }

    /// <summary>
    /// If the effort has changed since the conversation last heard, adds a message to the history that changes it from
    /// here on. Changing the request's own effort would mean re-sending the whole conversation without the cache.
    /// </summary>
    private void NoteEffortChange()
    {
        if (this.history.Count == 0)
        {
            this.conversationEffort = this.Effort; // a new conversation simply starts at it
            return;
        }

        this.conversationEffort ??= this.Effort; // a restored conversation hasn't been sent yet, so it can start at it too
        if (this.EffortInHistory() != this.Effort)
        {
            this.history.Add(EffortMessage(this.Effort));
        }
    }

    /// <summary>The effort the conversation is at: the latest change in it, or the one it started at.</summary>
    private EffortLevel EffortInHistory()
    {
        for (int i = this.history.Count - 1; i >= 0; i--)
        {
            if (EffortOf(this.history[i]) is EffortLevel changed)
            {
                return changed;
            }
        }

        return this.conversationEffort ?? this.Effort;
    }

    /// <summary>A system message with no text that changes the effort from the next user turn on. Internal for tests.</summary>
    internal static MessageParam EffortMessage(EffortLevel effort) => MessageParam.FromRawUnchecked(new Dictionary<string, JsonElement>
    {
        ["role"] = JsonSerializer.SerializeToElement("system"),
        ["content"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
        ["output_config"] = JsonSerializer.SerializeToElement(new { effort = AgentSettings.EffortApiName(effort) }),
    });

    /// <summary>The effort a message from <see cref="EffortMessage"/> sets; null for any other message.</summary>
    private static EffortLevel? EffortOf(MessageParam message) =>
        message.RawData.TryGetValue("output_config", out JsonElement config) && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("effort", out JsonElement effort) && effort.ValueKind == JsonValueKind.String
            ? AgentSettings.ParseEffortApiName(effort.GetString()!)
            : null;

    /// <param name="extra">A message to send after the history without adding it to the history.</param>
    /// <param name="maxTokens">Overrides the output limit; doesn't affect caching.</param>
    /// <param name="messages">A conversation other than this chat's own (a subagent's).</param>
    /// <param name="system">A system prompt other than this chat's own.</param>
    /// <param name="toolList">Tools other than this chat's own.</param>
    /// <param name="model">A model ID other than this chat's own (a subagent's).</param>
    /// <param name="effort">The effort for a conversation other than this chat's own.</param>
    private MessageCreateParams BuildRequest(
        MessageParam? extra = null,
        long maxTokens = MaxOutputTokens,
        List<MessageParam>? messages = null,
        string? system = null,
        List<ToolUnion>? toolList = null,
        string? model = null,
        EffortLevel? effort = null)
    {
        messages ??= this.history;

        var request = new MessageCreateParams
        {
            Model = model ?? this.ModelId,
            MaxTokens = maxTokens,
            Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },

            // The chat stays at the effort it started at; later changes are messages in it (see NoteEffortChange).
            OutputConfig = new OutputConfig { Effort = ToApiEffort(effort ?? this.conversationEffort ?? this.Effort) },

            // Automatic caching: the breakpoint moves forward with the conversation. The system prompt never changes
            // (the mode goes in the conversation instead; see ModeNote), so switching mode keeps the cache. The chat's
            // cache lasts an hour, since the user often takes longer than five minutes between messages and the whole
            // conversation would otherwise be written again; writing it costs 2x rather than 1.25x, but only the new
            // part is written each time. A subagent's conversation is over in minutes, so it keeps the default.
            CacheControl = messages == this.history ? new CacheControlEphemeral { Ttl = Ttl.Ttl1h } : new CacheControlEphemeral(),
            System = system ?? this.systemPrompt,
            Tools = toolList ?? this.tools ?? BuiltInTools(this.permissions, this.Model),
            Messages = extra is null ? [.. messages] : [.. messages, extra],
        };

        // Effort changes in the conversation are a beta.
        return messages.Any(m => EffortOf(m) != null)
            ? MessageCreateParams.FromRawUnchecked(
                new Dictionary<string, JsonElement> { ["anthropic-beta"] = JsonSerializer.SerializeToElement(PerMessageEffortBeta) },
                request.RawQueryData,
                request.RawBodyData)
            : request;
    }

    private const string PerMessageEffortBeta = "mid-conversation-output-config-2026-07-01";

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
}
