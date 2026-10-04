using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>
/// Feeds recorded Claude Code stream-json lines (<c>claude -p --output-format stream-json --verbose
/// --include-partial-messages</c>) through the engine, so a change in the CLI's output shows up here.
/// </summary>
public sealed class ClaudeCodeStreamTests : IDisposable
{
    private readonly string repo = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClaudeCodeEngine engine;
    private readonly RecordingSink sink = new();

    public ClaudeCodeStreamTests()
    {
        Directory.CreateDirectory(this.repo);
        this.engine = new ClaudeCodeEngine("claude.exe", this.repo, ClaudeModel.Opus, EffortLevel.High, AgentMode.Coding);
    }

    public void Dispose()
    {
        this.engine.Dispose();
        Directory.Delete(this.repo, recursive: true);
    }

    private ClaudeCodeEngine.RunOutcome? Feed(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return this.engine.HandleEvent(doc.RootElement, this.sink);
    }

    [Fact]
    public void InitSetsTheSession()
    {
        this.Feed("""{"type":"system","subtype":"init","session_id":"abc-123","tools":["Bash","Read"],"model":"claude-opus-5-5"}""");
        Assert.Equal("abc-123", this.engine.SessionId);
        Assert.Null(this.engine.Checkpoint); // the session exists, but no message in it is known yet

        this.Feed("""{"type":"assistant","uuid":"msg-1","message":{"content":[{"type":"text","text":"Hi"}]}}""");
        Assert.Equal("abc-123|msg-1", this.engine.Checkpoint);
    }

    [Fact]
    public void StreamsTextAndThinkingFromTheMainConversationOnly()
    {
        this.Feed("""{"type":"stream_event","parent_tool_use_id":null,"event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Hmm. "}}}""");
        this.Feed("""{"type":"stream_event","parent_tool_use_id":null,"event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello "}}}""");
        this.Feed("""{"type":"stream_event","parent_tool_use_id":"toolu_sub","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"subagent"}}}""");
        this.Feed("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"world"}}}""");

        Assert.Equal("Hello world", this.sink.Text.ToString());
        Assert.Equal("Hmm. ", this.sink.Thinking.ToString());
    }

    [Fact]
    public void ToolCallAndResultBecomeOneActivity()
    {
        this.Feed("""{"type":"assistant","uuid":"msg-1","parent_tool_use_id":null,"message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"dotnet build","description":"Build"}}]}}""");
        Assert.Empty(this.sink.Activities);

        this.Feed("""{"type":"user","uuid":"msg-2","parent_tool_use_id":null,"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"Build succeeded."}]}]}}""");

        (ActivityKind kind, string title, string detail) = Assert.Single(this.sink.Activities);
        Assert.Equal(ActivityKind.Shell, kind);
        Assert.Equal("dotnet build", title);
        Assert.Equal("Build succeeded.", detail);
        Assert.Equal("msg-2", this.engine.LastMessageId);
    }

    [Fact]
    public void DeniedToolShowsAsDeclined()
    {
        this.Feed("""{"type":"assistant","uuid":"m1","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rm -rf bin"}}]}}""");
        this.Feed("""{"type":"user","uuid":"m2","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"The user declined this action."}]}}""");

        Assert.Equal(ActivityKind.Declined, Assert.Single(this.sink.Activities).Kind);
    }

    [Fact]
    public void ErrorsThatMentionDenialAreStillFailures()
    {
        this.Feed("""{"type":"assistant","uuid":"m1","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"del locked.dll"}}]}}""");
        this.Feed("""{"type":"user","uuid":"m2","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"Access is denied."}]}}""");

        Assert.Equal(ActivityKind.Failed, Assert.Single(this.sink.Activities).Kind);
    }

    [Fact]
    public void EditToolCarriesTheFileChange()
    {
        string file = Path.Combine(this.repo, "a.txt");
        File.WriteAllText(file, "old");
        this.Feed($$$"""{"type":"assistant","uuid":"m1","message":{"content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":{{{JsonSerializer.Serialize(file)}}},"old_string":"old","new_string":"new"}}]}}""");
        File.WriteAllText(file, "new"); // Claude Code applies the edit between the two events
        this.Feed("""{"type":"user","uuid":"m2","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""");

        Assert.Equal(ActivityKind.Edit, Assert.Single(this.sink.Activities).Kind);
        FileChange change = Assert.Single(this.sink.Changes)!;
        Assert.Equal("old", change.Before);
        Assert.Equal("new", change.After);
    }

    [Fact]
    public void RateLimitEventBecomesAPlanLimit()
    {
        this.Feed("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","utilization":0.82,"resetsAt":1790000000}}""");

        PlanLimit limit = Assert.Single(this.sink.PlanLimits);
        Assert.Equal("five_hour", limit.Window);
        Assert.Equal(82, limit.Utilization!.Value, 3);
        Assert.Equal("allowed_warning", limit.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), limit.ResetsAt);
    }

    [Fact]
    public void CompactBoundaryIsReported()
    {
        this.Feed("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":950000}}""");
        Assert.Equal(950000, Assert.Single(this.sink.Compactions));
    }

    [Fact]
    public void LiveTokensAndContextFollowTheStream()
    {
        this.Feed("""{"type":"stream_event","event":{"type":"message_start","message":{"usage":{"input_tokens":100,"cache_creation_input_tokens":50,"cache_read_input_tokens":1000,"output_tokens":1}}}}""");
        Assert.Equal(1150, this.sink.Contexts.Last());
        Assert.Equal(150, this.sink.LiveTokens.Last());

        this.Feed("""{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":40}}}""");
        this.Feed("""{"type":"stream_event","event":{"type":"message_stop"}}""");
        Assert.Equal(1190, this.sink.Contexts.Last());
        Assert.Equal(190, this.sink.LiveTokens.Last());
    }

    [Fact]
    public void SuccessfulResultReportsUsage()
    {
        ClaudeCodeEngine.RunOutcome? outcome = this.Feed("""{"type":"result","subtype":"success","is_error":false,"duration_ms":4200,"num_turns":3,"result":"Done.","usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":300,"output_tokens":40}}""");

        Assert.NotNull(outcome);
        Assert.Null(outcome!.Value.Error);
        Assert.Equal(new TokenUsage(10, 20, 300, 40), Assert.Single(this.sink.PromptUsages));
    }

    [Fact]
    public void ErrorResultCarriesTheMessage()
    {
        ClaudeCodeEngine.RunOutcome? outcome = this.Feed("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Credit balance too low"}""");
        Assert.Equal("Credit balance too low", outcome!.Value.Error);

        outcome = this.Feed("""{"type":"result","subtype":"error_max_turns","is_error":true}""");
        Assert.Contains("error_max_turns", outcome!.Value.Error);
    }

    [Fact]
    public void TodoWriteUpdatesTheTaskList()
    {
        this.Feed("""{"type":"assistant","uuid":"m1","message":{"content":[{"type":"tool_use","id":"toolu_t","name":"TodoWrite","input":{"todos":[{"content":"Fix the bug","status":"completed","activeForm":"Fixing the bug"},{"content":"Run the tests","status":"in_progress","activeForm":"Running the tests"},{"content":"Update docs","status":"pending","activeForm":"Updating docs"}]}}]}}""");

        IReadOnlyList<TaskItem> tasks = Assert.Single(this.sink.TaskLists);
        Assert.Equal(["completed", "in_progress", "pending"], tasks.Select(t => t.Status));
        Assert.Equal("Running the tests", tasks[1].Display);
        Assert.Equal("Update docs", tasks[2].Display);

        this.Feed("""{"type":"user","uuid":"m2","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_t","content":"ok"}]}}""");
        (ActivityKind kind, string title, string detail) = Assert.Single(this.sink.Activities);
        Assert.Equal(ActivityKind.Other, kind);
        Assert.Equal("Updated the task list", title);
        Assert.Equal("[x] Fix the bug\n[~] Run the tests\n[ ] Update docs", detail);
    }

    [Fact]
    public void VisualStudioToolResultsAreShown()
    {
        this.Feed("""{"type":"assistant","uuid":"m1","message":{"content":[{"type":"tool_use","id":"toolu_d","name":"mcp__visualstudio__get_diagnostics","input":{}}]}}""");
        this.Feed("""{"type":"user","uuid":"m2","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_d","content":[{"type":"text","text":"No errors or warnings."}]}]}}""");
        (ActivityKind kind, string title, string detail) = Assert.Single(this.sink.Activities);
        Assert.Equal(ActivityKind.Search, kind);
        Assert.Equal("Checked the Error List", title);
        Assert.Equal("No errors or warnings.", detail);
    }

    [Fact]
    public void UnknownEventsAreIgnored()
    {
        Assert.Null(this.Feed("""{"type":"something_new","data":1}"""));
        Assert.Null(this.Feed("""{"type":"system","subtype":"hook_response"}"""));
        Assert.Empty(this.sink.Activities);
    }

    private sealed class RecordingSink : IChatSink
    {
        public StringBuilder Text { get; } = new();

        public StringBuilder Thinking { get; } = new();

        public List<(ActivityKind Kind, string Title, string Detail)> Activities { get; } = [];

        public List<FileChange?> Changes { get; } = [];

        public List<PlanLimit> PlanLimits { get; } = [];

        public List<TokenUsage> PromptUsages { get; } = [];

        public List<long> LiveTokens { get; } = [];

        public List<long> Contexts { get; } = [];

        public List<long> Compactions { get; } = [];

        public void AppendText(string text) => this.Text.Append(text);

        public void AppendThinking(string text) => this.Thinking.Append(text);

        public void ToolActivity(ActivityKind kind, string title, string detail, FileChange? change = null)
        {
            this.Activities.Add((kind, title, detail));
            this.Changes.Add(change);
        }

        public void SetStatus(string? status)
        {
        }

        public ILiveActivity StartLiveActivity(ActivityKind kind, string title, Action stop) => throw new NotSupportedException();

        public void ReportTokenUsage(ClaudeModel model, TokenUsage usage)
        {
        }

        public void ReportPromptUsage(TokenUsage usage) => this.PromptUsages.Add(usage);

        public void ReportLiveTokens(long totalTokens) => this.LiveTokens.Add(totalTokens);

        public void ReportContext(long tokens) => this.Contexts.Add(tokens);

        public void ReportCompacted(long tokensBefore) => this.Compactions.Add(tokensBefore);

        public void ReportApiRateLimits(ApiRateLimits limits)
        {
        }

        public void ReportPlanLimit(PlanLimit limit) => this.PlanLimits.Add(limit);

        public Task<IReadOnlyDictionary<string, string>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ApprovalResult> RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change = null, bool canKeepSome = false) =>
            Task.FromResult<ApprovalResult>(Approval.Deny);

        public List<IReadOnlyList<TaskItem>> TaskLists { get; } = [];

        public void ReportTasks(IReadOnlyList<TaskItem> tasks) => this.TaskLists.Add(tasks);
    }
}
