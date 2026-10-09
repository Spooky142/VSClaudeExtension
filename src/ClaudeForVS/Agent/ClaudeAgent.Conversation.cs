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

/// <summary>Compaction, mode notes and building the Messages API request.</summary>
internal sealed partial class ClaudeAgent
{
    /// <summary>
    /// Once the context passes <see cref="CompactAt"/> (or a full-length reply would no longer fit), has Claude summarize the conversation and restarts it from that
    /// summary. The request reuses the normal request with the instruction appended, so the history is read from the
    /// cache. Returns the new tokens it used (counted towards the prompt's total), or 0 if it didn't compact.
    /// </summary>
    private async Task<long> CompactIfNeededAsync(IChatSink sink, bool midTask, CancellationToken cancellationToken, bool force = false)
    {
        long window = AgentSettings.ContextWindow;
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

        // Reported once, even if stopped part-way, so the cost estimate knows the prompt's length when it prices the output.
        TokenUsage used = default;
        try
        {
            await foreach (RawMessageStreamEvent ev in this.client.Messages.CreateStreaming(request, cancellationToken))
            {
                if (ev.TryPickStart(out RawMessageStartEvent? start))
                {
                    newTokens += start.Message.Usage.InputTokens + (start.Message.Usage.CacheCreationInputTokens ?? 0);
                    used = new TokenUsage(
                        start.Message.Usage.InputTokens,
                        start.Message.Usage.CacheCreationInputTokens ?? 0,
                        start.Message.Usage.CacheReadInputTokens ?? 0,
                        0);
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
    /// <param name="messages">A conversation other than this chat's own (a subagent's).</param>
    /// <param name="system">A system prompt other than this chat's own.</param>
    /// <param name="toolList">Tools other than this chat's own.</param>
    private MessageCreateParams BuildRequest(
        MessageParam? extra = null,
        long maxTokens = MaxOutputTokens,
        List<MessageParam>? messages = null,
        string? system = null,
        List<ToolUnion>? toolList = null)
    {
        messages ??= this.history;

        return new MessageCreateParams
        {
            Model = AgentSettings.ModelId(this.Model),
            MaxTokens = maxTokens,
            Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
            OutputConfig = new OutputConfig { Effort = ToApiEffort(this.Effort) },

            // Automatic caching: the breakpoint moves forward with the conversation. The system prompt never changes
            // (the mode goes in the conversation instead; see ModeNote), so switching mode keeps the cache.
            CacheControl = new CacheControlEphemeral(),
            System = system ?? this.systemPrompt,
            Tools = toolList ?? this.tools ?? BuiltInTools(this.permissions),
            Messages = extra is null ? [.. messages] : [.. messages, extra],
        };
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
}
