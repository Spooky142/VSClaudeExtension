using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;

namespace ClaudeForVS.Tests;

/// <summary>
/// A stand-in for the Messages API on localhost that answers each request with the next scripted streaming response
/// and keeps what it was sent, so the agent's tool loop can be tested without a network or a key.
/// </summary>
internal sealed class FakeAnthropic : IDisposable
{
    private readonly HttpListener listener;
    private readonly Queue<Func<JsonElement, string>> script = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public FakeAnthropic()
    {
        // Free ports are found by trying; other tests are doing the same at the same time, so retry if one is taken.
        var random = new Random();
        for (int attempt = 0; ; attempt++)
        {
            string url = $"http://localhost:{random.Next(20000, 48000)}";
            var candidate = new HttpListener();
            try
            {
                candidate.Prefixes.Add(url + "/");
                candidate.Start();
                this.listener = candidate;
                this.Url = url;
                break;
            }
            catch (HttpListenerException) when (attempt < 50)
            {
                candidate.Close();
            }
        }

        this.loop = Task.Run(this.ServeAsync);
    }

    public string Url { get; }

    /// <summary>The JSON body of every request received, in order.</summary>
    public List<JsonDocument> Requests { get; } = [];

    /// <summary>Queues the next response: a function of the request, returning the response's server-sent events.</summary>
    public FakeAnthropic Reply(Func<JsonElement, string> response)
    {
        this.script.Enqueue(response);
        return this;
    }

    public FakeAnthropic Reply(string events) => this.Reply(_ => events);

    public void Dispose()
    {
        this.stop.Cancel();
        this.listener.Close();
        try
        {
            this.loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task ServeAsync()
    {
        while (!this.stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            JsonDocument request = JsonDocument.Parse(body);
            string events;
            lock (this.Requests)
            {
                this.Requests.Add(request);
                events = this.script.Count > 0
                    ? this.script.Dequeue()(request.RootElement)
                    : Sse.Turn(Sse.Text("(the fake API had nothing more scripted)"));
            }

            byte[] bytes = new UTF8Encoding(false).GetBytes(events);
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
        }
    }
}

/// <summary>Builds the server-sent events of a streamed Messages API response.</summary>
internal static class Sse
{
    /// <summary>One content block: its start, its deltas and its end.</summary>
    public static string Text(string text, int index = 0) =>
        Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "text", text = string.Empty } })
        + Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "text_delta", text } })
        + Event("content_block_stop", new { type = "content_block_stop", index });

    public static string ToolUse(string id, string name, object input, int index = 0) =>
        Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "tool_use", id, name, input = new { } } })
        + Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(input) } })
        + Event("content_block_stop", new { type = "content_block_stop", index });

    /// <summary>A whole response around its blocks.</summary>
    /// <param name="stopReason">"end_turn", or "tool_use" if the blocks include tool calls.</param>
    public static string Turn(string blocks, string stopReason = "end_turn", int inputTokens = 100, int outputTokens = 20) =>
        Event("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                type = "message",
                role = "assistant",
                model = "claude-opus-5-5",
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = inputTokens, output_tokens = 1, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 },
            },
        })
        + blocks
        + Event("message_delta", new { type = "message_delta", delta = new { stop_reason = stopReason, stop_sequence = (string?)null }, usage = new { output_tokens = outputTokens } })
        + Event("message_stop", new { type = "message_stop" });

    private static string Event(string name, object data) => $"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n";
}

/// <summary>An <see cref="IChatSink"/> that records what happened and answers approvals as told.</summary>
internal sealed class RecordingSink : IChatSink
{
    public StringBuilder Text { get; } = new();

    public List<(ActivityKind Kind, string Title, string Detail)> Activities { get; } = [];

    public List<string> Approvals { get; } = [];

    public List<TokenUsage> Usages { get; } = [];

    public List<string> Statuses { get; } = [];

    /// <summary>What to answer when asked to approve something.</summary>
    public Func<string, Approval> Decide { get; set; } = _ => Approval.Allow;

    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];

    public void AppendText(string text) => this.Text.Append(text);

    public void AppendThinking(string text)
    {
    }

    public void ToolActivity(ActivityKind kind, string title, string detail, FileChange? change = null) => this.Activities.Add((kind, title, detail));

    public ILiveActivity StartLiveActivity(ActivityKind kind, string title, Action stop) => new Live();

    public void SetStatus(string? status) => this.Statuses.Add(status ?? string.Empty);

    public void ReportTokenUsage(ClaudeModel model, TokenUsage usage) => this.Usages.Add(usage);

    public void ReportPromptUsage(TokenUsage usage)
    {
    }

    public void ReportLiveTokens(long totalTokens)
    {
    }

    public void ReportContext(long tokens)
    {
    }

    public void ReportCompacted(long tokensBefore)
    {
    }

    public void ReportApiRateLimits(ApiRateLimits limits)
    {
    }

    public void ReportPlanLimit(PlanLimit limit)
    {
    }

    public Task<IReadOnlyDictionary<string, string>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Task<ApprovalResult> RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change = null, bool canKeepSome = false)
    {
        this.Approvals.Add(title);
        return Task.FromResult<ApprovalResult>(this.Decide(title));
    }

    public void ReportTasks(IReadOnlyList<TaskItem> tasks) => this.Tasks = tasks;

    private sealed class Live : ILiveActivity
    {
        public void SetOutput(string output)
        {
        }

        public void Dispose()
        {
        }
    }
}
