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

/// <summary>Streaming one response from the Messages API.</summary>
internal sealed partial class ClaudeAgent
{
    /// <param name="request">The request to stream; null for this chat's own next one.</param>
    private async Task<StreamedTurn> StreamTurnAsync(IChatSink sink, long tokensBefore, CancellationToken cancellationToken, MessageCreateParams? request = null)
    {
        var blocks = new SortedDictionary<long, BlockBuilder>();
        var turn = new StreamedTurn();
        long streamedChars = 0; // output isn't counted until the response ends, so estimate it (~4 chars per token) meanwhile

        await foreach (RawMessageStreamEvent ev in this.client.Messages.CreateStreaming(request ?? this.BuildRequest(), cancellationToken))
        {
            if (ev.TryPickStart(out RawMessageStartEvent? start))
            {
                // The prompt's count is what Claude writes; input (mostly the history and tools sent again) isn't in it.
                turn.CacheReadTokens = start.Message.Usage.CacheReadInputTokens ?? 0;
                turn.CacheWriteTokens = start.Message.Usage.CacheCreationInputTokens ?? 0;
                turn.CacheWrite1hTokens = start.Message.Usage.CacheCreation?.Ephemeral1hInputTokens ?? 0;
                turn.UncachedInputTokens = start.Message.Usage.InputTokens;
                sink.ReportLiveTokens(tokensBefore);

                // The headers and input size are known now, so the strip needn't wait for the reply to finish.
                sink.ReportContext(turn.UncachedInputTokens + turn.CacheWriteTokens + turn.CacheReadTokens);
                if (this.rateLimits.Latest is ApiRateLimits limits)
                {
                    sink.ReportApiRateLimits(limits);
                }
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
                else if (blockStart.ContentBlock.TryPickServerToolUse(out ServerToolUseBlock? serverTool))
                {
                    // Web search and fetch run on Anthropic's side; their results arrive as the next block. The newer
                    // versions also run code there to filter what they found.
                    b.Kind = BlockKind.ServerToolUse;
                    b.Raw = serverTool.RawData;
                    b.ToolId = RawString(serverTool.RawData, "id");
                    b.ToolName = RawString(serverTool.RawData, "name");
                    sink.SetStatus(b.ToolName switch
                    {
                        "web_fetch" => "Fetching a web page…",
                        "web_search" => "Searching the web…",
                        "tool_search_tool_bm25" => "Looking for a tool…",
                        _ => "Going through what it found…",
                    });
                }
                else if (blockStart.ContentBlock.TryPickWebSearchToolResult(out WebSearchToolResultBlock? searchResult))
                {
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = searchResult.RawData;
                    this.ReportWebResult(searchResult.RawData, blocks.Values, sink);
                }
                else if (blockStart.ContentBlock.TryPickWebFetchToolResult(out WebFetchToolResultBlock? fetchResult))
                {
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = fetchResult.RawData;
                    this.ReportWebResult(fetchResult.RawData, blocks.Values, sink);
                }
                else if (blockStart.ContentBlock.TryPickCodeExecutionToolResult(out CodeExecutionToolResultBlock? codeResult))
                {
                    // The code the web tools ran to filter their results; nothing to show, but it goes back with the turn.
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = codeResult.RawData;
                }
                else if (blockStart.ContentBlock.TryPickBashCodeExecutionToolResult(out BashCodeExecutionToolResultBlock? bashResult))
                {
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = bashResult.RawData;
                }
                else if (blockStart.ContentBlock.TryPickTextEditorCodeExecutionToolResult(out TextEditorCodeExecutionToolResultBlock? editorResult))
                {
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = editorResult.RawData;
                }
                else if (blockStart.ContentBlock.TryPickToolSearchToolResult(out ToolSearchToolResultBlock? searchToolResult))
                {
                    // The MCP tools a search turned up; it goes back with the turn so Claude can call them.
                    b.Kind = BlockKind.ServerToolResult;
                    b.Raw = searchToolResult.RawData;
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
                sink.ReportLiveTokens(tokensBefore + (streamedChars / 4));
            }
            else if (ev.TryPickDelta(out RawMessageDeltaEvent? messageDelta))
            {
                turn.StopReason = messageDelta.Delta.StopReason?.Raw();
                turn.OutputTokens = messageDelta.Usage.OutputTokens;
                turn.WebSearches = messageDelta.Usage.ServerToolUse?.WebSearchRequests ?? 0;
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
                case BlockKind.ServerToolUse:
                    // The input streams in like a client tool's; if it didn't, it came whole with the block.
                    JsonElement serverInput = b.Text.Length > 0
                        ? JsonSerializer.SerializeToElement(ParseToolInput(b.Text.ToString()))
                        : b.Raw.TryGetValue("input", out JsonElement given) ? given : JsonSerializer.SerializeToElement(new { });
                    var serverUse = new Dictionary<string, JsonElement>
                    {
                        ["type"] = JsonSerializer.SerializeToElement("server_tool_use"),
                        ["id"] = JsonSerializer.SerializeToElement(b.ToolId),
                        ["name"] = JsonSerializer.SerializeToElement(b.ToolName),
                        ["input"] = serverInput,
                    };
                    CopyCaller(b.Raw, serverUse);
                    turn.Content.Add(ServerToolUseBlockParam.FromRawUnchecked(serverUse));
                    break;
                case BlockKind.ServerToolResult:
                    // Sent back as received (encrypted content included) so Claude can cite the results in later turns.
                    var result = new Dictionary<string, JsonElement>();
                    foreach (string key in new[] { "type", "tool_use_id", "content" })
                    {
                        if (b.Raw.TryGetValue(key, out JsonElement value))
                        {
                            result[key] = value;
                        }
                    }

                    CopyCaller(b.Raw, result);
                    turn.Content.Add(RawString(b.Raw, "type") switch
                    {
                        "web_fetch_tool_result" => WebFetchToolResultBlockParam.FromRawUnchecked(result),
                        "web_search_tool_result" => WebSearchToolResultBlockParam.FromRawUnchecked(result),
                        _ => new ContentBlockParam(JsonSerializer.SerializeToElement(result)),
                    });
                    break;
            }
        }

        return turn;
    }

    /// <summary>Shows a finished web search or fetch in the transcript.</summary>
    private void ReportWebResult(IReadOnlyDictionary<string, JsonElement> result, IEnumerable<BlockBuilder> blocks, IChatSink sink)
    {
        string id = RawString(result, "tool_use_id");
        BlockBuilder? call = blocks.LastOrDefault(b => b.Kind == BlockKind.ServerToolUse && b.ToolId == id);
        JsonElement input = call is null ? default
            : call.Text.Length > 0 ? JsonSerializer.SerializeToElement(ParseToolInput(call.Text.ToString()))
            : call.Raw.TryGetValue("input", out JsonElement given) ? given : default;
        (ActivityKind kind, string title, string detail) = DescribeWebResult(
            RawString(result, "type"), input, result.TryGetValue("content", out JsonElement content) ? content : default);
        sink.ToolActivity(kind, title, detail);
        sink.SetStatus("Waiting for Claude…");
    }

    /// <summary>The transcript row for a web search or fetch: its title (the query or URL) and the results or error.</summary>
    internal static (ActivityKind Kind, string Title, string Detail) DescribeWebResult(string type, JsonElement input, JsonElement content)
    {
        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

        bool fetch = type == "web_fetch_tool_result";
        string title = fetch ? $"Fetched {Str(input, "url")}".TrimEnd() : $"Searched the web: {Str(input, "query")}".TrimEnd(' ', ':');
        string error = Str(content, "error_code");
        if (error.Length > 0)
        {
            return (ActivityKind.Failed, fetch ? $"Couldn't fetch {Str(input, "url")}".TrimEnd() : $"Web search failed: {Str(input, "query")}".TrimEnd(' ', ':'), error.Replace('_', ' '));
        }

        if (fetch)
        {
            // The page is a document block; its title, when known, is the most useful thing to show.
            JsonElement document = content.ValueKind == JsonValueKind.Object && content.TryGetProperty("content", out JsonElement d) ? d : default;
            string pageTitle = Str(document, "title");
            string url = Str(content, "url") is { Length: > 0 } u ? u : Str(input, "url");
            return (ActivityKind.Web, title, pageTitle.Length > 0 ? $"{pageTitle}\n{url}" : url);
        }

        var lines = new List<string>();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement hit in content.EnumerateArray())
            {
                string hitTitle = Str(hit, "title");
                string hitUrl = Str(hit, "url");
                lines.Add(hitTitle.Length > 0 ? $"{hitTitle} — {hitUrl}" : hitUrl);
            }
        }

        return (ActivityKind.Web, title, lines.Count == 0 ? "No results." : string.Join("\n", lines));
    }

    /// <summary>
    /// Keeps which tool made a server tool call, when one did: the filtering web tools call search and fetch from the
    /// code they run, and the call has to go back the same way.
    /// </summary>
    private static void CopyCaller(IReadOnlyDictionary<string, JsonElement> raw, Dictionary<string, JsonElement> block)
    {
        if (raw.TryGetValue("caller", out JsonElement caller) && caller.ValueKind == JsonValueKind.Object)
        {
            block["caller"] = caller;
        }
    }

    private static string RawString(IReadOnlyDictionary<string, JsonElement> raw, string name) =>
        raw.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

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

    private enum BlockKind { Other, Text, Thinking, RedactedThinking, ToolUse, ServerToolUse, ServerToolResult }

    private sealed class BlockBuilder
    {
        public BlockKind Kind;
        public StringBuilder Text = new();
        public string Signature = string.Empty;
        public string Data = string.Empty;
        public string ToolId = string.Empty;
        public string ToolName = string.Empty;

        // Server tool blocks as received.
        public IReadOnlyDictionary<string, JsonElement> Raw = new Dictionary<string, JsonElement>();
    }

    private sealed class StreamedTurn
    {
        public List<ContentBlockParam> Content = [];
        public List<ToolUseBlockParam> ToolUses = [];
        public string? StopReason;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long CacheWrite1hTokens;
        public long UncachedInputTokens;
        public long OutputTokens;
        public long WebSearches;

        public TokenUsage Usage => new(this.UncachedInputTokens, this.CacheWriteTokens, this.CacheReadTokens, this.OutputTokens, this.WebSearches, this.CacheWrite1hTokens);
    }
}
