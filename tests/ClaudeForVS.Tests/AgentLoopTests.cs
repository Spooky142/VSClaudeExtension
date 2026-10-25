using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>The Claude API engine's tool loop, against a fake API on localhost.</summary>
public sealed class AgentLoopTests : IDisposable
{
    private readonly string repo = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAnthropic api = new();
    private readonly RecordingSink sink = new();

    public AgentLoopTests()
    {
        Directory.CreateDirectory(this.repo);
        File.WriteAllText(Path.Combine(this.repo, "hello.txt"), "line one\nline two\n");
    }

    public void Dispose()
    {
        this.api.Dispose();
        Directory.Delete(this.repo, recursive: true);
    }

    private ClaudeAgent NewAgent(AgentMode mode = AgentMode.Auto) =>
        new("test-key", this.repo, ClaudeModel.Opus, EffortLevel.Low, mode, this.api.Url);

    private Task SendAsync(ClaudeAgent agent, string text) => agent.SendAsync(new ChatPrompt(text), this.sink, CancellationToken.None);

    /// <summary>The text of the tool_result the agent sent back in a given request.</summary>
    private string ToolResult(int request)
    {
        JsonElement messages = this.api.Requests[request].RootElement.GetProperty("messages");
        JsonElement last = messages[messages.GetArrayLength() - 1];
        JsonElement result = last.GetProperty("content").EnumerateArray().First(b => b.GetProperty("type").GetString() == "tool_result");
        JsonElement content = result.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
    }

    private static string ToolTurn(string id, string name, object input) =>
        Sse.Turn(Sse.ToolUse(id, name, input), stopReason: "tool_use");

    [Fact]
    public async Task AnswersAPlainMessageAndKeepsTheHistory()
    {
        this.api.Reply(Sse.Turn(Sse.Text("Hello there."))).Reply(Sse.Turn(Sse.Text("Second answer.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Hi");
        await this.SendAsync(agent, "Again");

        Assert.Equal("Hello there.Second answer.", this.sink.Text.ToString());
        Assert.Equal(2, this.sink.Usages.Count);
        Assert.Equal(100, this.sink.Usages[0].Input);
        Assert.Equal(3, this.api.Requests[1].RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task RunsAToolAndSendsItsResultBack()
    {
        this.api.Reply(ToolTurn("toolu_1", "read_file", new { path = "hello.txt" })).Reply(Sse.Turn(Sse.Text("It says line one.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "What's in hello.txt?");

        Assert.Equal(2, this.api.Requests.Count);
        Assert.Contains("line two", this.ToolResult(1));
        Assert.Equal("It says line one.", this.sink.Text.ToString());
    }

    [Fact]
    public async Task ADeclinedEditLeavesTheFileAlone()
    {
        this.api.Reply(ToolTurn("toolu_1", "edit_file", new { path = "hello.txt", old_string = "line one", new_string = "LINE ONE" }))
            .Reply(Sse.Turn(Sse.Text("Okay.")));
        this.sink.Decide = _ => Approval.Deny;
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "Shout line one");

        Assert.Equal("line one\nline two\n", File.ReadAllText(Path.Combine(this.repo, "hello.txt")));
        Assert.Single(this.sink.Approvals);
        Assert.Contains("declined", this.ToolResult(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnApprovedEditIsWritten()
    {
        this.api.Reply(ToolTurn("toolu_1", "edit_file", new { path = "hello.txt", old_string = "line one", new_string = "LINE ONE" }))
            .Reply(Sse.Turn(Sse.Text("Done.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "Shout line one");

        Assert.Equal("LINE ONE\nline two\n", File.ReadAllText(Path.Combine(this.repo, "hello.txt")));
    }

    [Fact]
    public async Task PlanModeRefusesCommandsWithoutAsking()
    {
        this.api.Reply(ToolTurn("toolu_1", "run_command", new { command = "Get-Date" })).Reply(Sse.Turn(Sse.Text("Can't.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Plan);

        await this.SendAsync(agent, "What day is it?");

        Assert.Empty(this.sink.Approvals);
        Assert.Contains("Plan mode", this.ToolResult(1));
    }

    [Fact]
    public async Task AToolFailureBecomesAnErrorResultForClaude()
    {
        this.api.Reply(ToolTurn("toolu_1", "read_file", new { path = "..\\outside.txt" })).Reply(Sse.Turn(Sse.Text("Sorry.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Read outside");

        JsonElement messages = this.api.Requests[1].RootElement.GetProperty("messages");
        JsonElement result = messages[messages.GetArrayLength() - 1].GetProperty("content").EnumerateArray().First(b => b.GetProperty("type").GetString() == "tool_result");
        Assert.True(result.GetProperty("is_error").GetBoolean());
        Assert.Contains(this.sink.Activities, a => a.Kind == ActivityKind.Failed);
    }

    private void WriteRepoHooks(string json)
    {
        Directory.CreateDirectory(Path.Combine(this.repo, ".claude"));
        File.WriteAllText(Path.Combine(this.repo, ".claude", "settings.json"), json);
    }

    [Fact]
    public async Task APreToolHookCanBlockATool()
    {
        this.WriteRepoHooks("""
            { "hooks": { "PreToolUse": [ { "matcher": "Read", "hooks": [ { "type": "command", "command": "[Console]::Error.WriteLine('Reading is off today.'); exit 2" } ] } ] } }
            """);
        this.api.Reply(ToolTurn("toolu_1", "read_file", new { path = "hello.txt" })).Reply(Sse.Turn(Sse.Text("Blocked.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.HooksEnabled = true;

        await this.SendAsync(agent, "Read it");

        Assert.Contains(this.sink.Approvals, a => a.IndexOf("hooks", StringComparison.OrdinalIgnoreCase) >= 0); // repository hooks ask for trust first
        Assert.Contains("Reading is off today.", this.ToolResult(1));
        Assert.DoesNotContain("line two", this.ToolResult(1));
    }

    [Fact]
    public async Task RepositoryHooksDontRunUntilTrusted()
    {
        this.WriteRepoHooks("""
            { "hooks": { "PreToolUse": [ { "hooks": [ { "type": "command", "command": "exit 2" } ] } ] } }
            """);
        this.sink.Decide = _ => Approval.Deny;
        this.api.Reply(ToolTurn("toolu_1", "read_file", new { path = "hello.txt" })).Reply(Sse.Turn(Sse.Text("Read it.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.HooksEnabled = true;

        await this.SendAsync(agent, "Read it");

        Assert.Contains("line two", this.ToolResult(1));
    }

    [Fact]
    public async Task HooksDontRunUnlessEnabled()
    {
        this.WriteRepoHooks("""
            { "hooks": { "PreToolUse": [ { "hooks": [ { "type": "command", "command": "exit 2" } ] } ] } }
            """);
        this.api.Reply(ToolTurn("toolu_1", "read_file", new { path = "hello.txt" })).Reply(Sse.Turn(Sse.Text("Read it.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Read it");

        Assert.Empty(this.sink.Approvals);
        Assert.Contains("line two", this.ToolResult(1));
    }

    [Fact]
    public async Task AStopHookCanMakeClaudeCarryOn()
    {
        string marker = Path.Combine(this.repo, "stopped-once");
        string command = $"if (-not (Test-Path -LiteralPath '{marker}')) {{ New-Item -ItemType File -Path '{marker}' | Out-Null; [Console]::Error.WriteLine('Run the tests first.'); exit 2 }}";
        this.WriteRepoHooks($$"""
            { "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": {{JsonSerializer.Serialize(command)}} } ] } ] } }
            """);
        this.api.Reply(Sse.Turn(Sse.Text("All done."))).Reply(Sse.Turn(Sse.Text("Tests run.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.HooksEnabled = true;

        await this.SendAsync(agent, "Finish up");

        Assert.Equal(2, this.api.Requests.Count);
        JsonElement messages = this.api.Requests[1].RootElement.GetProperty("messages");
        Assert.Contains("Run the tests first.", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.Equal("All done.Tests run.", this.sink.Text.ToString());
    }

    [Fact]
    public async Task APromptHookCanBlockTheMessage()
    {
        this.WriteRepoHooks("""
            { "hooks": { "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "[Console]::Error.WriteLine('No secrets.'); exit 2" } ] } ] } }
            """);
        using ClaudeAgent agent = this.NewAgent();
        agent.HooksEnabled = true;

        await this.SendAsync(agent, "Here is my password");

        Assert.Empty(this.api.Requests);
        Assert.Contains("No secrets.", this.sink.Text.ToString());
    }

    [Fact]
    public async Task StopsAtTheSpendingLimitWhenTheUserSaysNo()
    {
        // 200K input tokens is $0.80 at Opus prices, past a $0.50 limit but short of compacting.
        this.api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "read_file", new { path = "hello.txt" }), stopReason: "tool_use", inputTokens: 200_000))
            .Reply(Sse.Turn(Sse.Text("never sent")));
        this.sink.Decide = _ => Approval.Deny;
        using ClaudeAgent agent = this.NewAgent();
        agent.SpendLimitUsd = 0.5;

        await this.SendAsync(agent, "Read it");

        Assert.Single(this.api.Requests);
        Assert.Single(this.sink.Approvals);
        Assert.StartsWith("Spending limit reached", this.sink.Approvals[0]);
        Assert.True(agent.Spent > 0.79);
        Assert.Contains("spending limit", this.sink.Text.ToString());
    }

    [Fact]
    public async Task CarriesOnPastTheLimitWhenTheUserSaysYes()
    {
        this.api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "read_file", new { path = "hello.txt" }), stopReason: "tool_use", inputTokens: 200_000))
            .Reply(Sse.Turn(Sse.Text("Finished.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.SpendLimitUsd = 0.5;

        await this.SendAsync(agent, "Read it");

        Assert.Equal(2, this.api.Requests.Count);
        Assert.Single(this.sink.Approvals);
        Assert.Contains("Finished.", this.sink.Text.ToString());
    }

    [Fact]
    public async Task ASubagentResearchesInItsOwnConversationAndReturnsAReport()
    {
        this.api
            .Reply(ToolTurn("toolu_main", "task", new { description = "Read the notes", prompt = "Find out what hello.txt says." }))
            .Reply(ToolTurn("toolu_sub", "read_file", new { path = "hello.txt" }))
            .Reply(Sse.Turn(Sse.Text("Report: hello.txt has two lines.")))
            .Reply(Sse.Turn(Sse.Text("It has two lines.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "What's in hello.txt?");

        Assert.Equal(4, this.api.Requests.Count);

        // The subagent starts from the brief alone, with read-only tools, on Haiku at low effort.
        JsonElement first = this.api.Requests[1].RootElement;
        Assert.Equal(1, first.GetProperty("messages").GetArrayLength());
        Assert.Contains("research subagent", first.GetProperty("system").GetString());
        Assert.Equal("claude-haiku-5-5", first.GetProperty("model").GetString());
        Assert.Equal("low", first.GetProperty("output_config").GetProperty("effort").GetString());
        string[] names = first.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("read_file", names);
        Assert.DoesNotContain("edit_file", names);
        Assert.DoesNotContain("run_command", names);
        Assert.DoesNotContain("task", names);

        // Haiku gets the basic web tools; the Opus chat gets the ones that filter results.
        static string[] Types(JsonElement request) => request.GetProperty("tools").EnumerateArray()
            .Select(t => t.TryGetProperty("type", out JsonElement type) ? type.GetString()! : string.Empty).ToArray();
        Assert.Contains("web_search_20250305", Types(first));
        Assert.Contains("web_fetch_20250910", Types(first));
        Assert.Contains("web_search_20260209", Types(this.api.Requests[0].RootElement));
        Assert.Contains("web_fetch_20260209", Types(this.api.Requests[0].RootElement));

        // Only its report comes back to the main conversation, not the file it read.
        Assert.Equal("Report: hello.txt has two lines.", this.ToolResult(3));
        Assert.DoesNotContain("line two", this.api.Requests[3].RootElement.GetRawText());
        Assert.Empty(this.sink.Approvals);
        Assert.Equal("It has two lines.", this.sink.Text.ToString());
        // Its tool use shows in its own row while it works, which then gives way to one with its report.
        (string liveTitle, RecordingSink.Live row) = Assert.Single(this.sink.LiveRows);
        Assert.Equal("Subagent: Read the notes", liveTitle);
        Assert.Contains("hello.txt", row.Output);
        Assert.True(row.Disposed);
        (_, string title, string detail) = Assert.Single(this.sink.Activities);
        Assert.Equal("Subagent: Read the notes", title);
        Assert.Contains("hello.txt", detail.Split('\n')[1]); // the step it took
        Assert.EndsWith("Report: hello.txt has two lines.", detail);
        Assert.Equal(4, this.sink.Usages.Count); // its tokens count too
    }

    [Fact]
    public async Task ASubagentCantChangeAnything()
    {
        this.api
            .Reply(ToolTurn("toolu_main", "task", new { description = "Tidy up", prompt = "Fix hello.txt." }))
            .Reply(ToolTurn("toolu_sub", "edit_file", new { path = "hello.txt", old_string = "line one", new_string = "LINE ONE" }))
            .Reply(Sse.Turn(Sse.Text("I couldn't edit it.")))
            .Reply(Sse.Turn(Sse.Text("Done.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Tidy hello.txt");

        Assert.Equal("line one\nline two\n", File.ReadAllText(Path.Combine(this.repo, "hello.txt")));
        Assert.Contains("isn't available to a subagent", this.ToolResult(2));
    }

    [Fact]
    public async Task ASubagentStopsItselfAtTheSpendingLimitAndTheMainChatAsks()
    {
        this.api
            .Reply(ToolTurn("toolu_main", "task", new { description = "Look around", prompt = "Read hello.txt, then keep digging." }))
            .Reply(Sse.Turn(Sse.ToolUse("toolu_sub", "read_file", new { path = "hello.txt" }), stopReason: "tool_use", inputTokens: 500_000))
            .Reply(Sse.Turn(Sse.Text("Carrying on.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.SpendLimitUsd = 0.1; // the subagent runs on Haiku: 500K tokens is $0.25

        await this.SendAsync(agent, "Dig around");

        // The subagent made one request and then stopped; it never asked the user itself.
        Assert.Equal(3, this.api.Requests.Count);
        Assert.Contains("stopped early because this chat reached its spending limit", this.ToolResult(2));

        // The main conversation, which can ask, did.
        Assert.Single(this.sink.Approvals);
        Assert.StartsWith("Spending limit reached", this.sink.Approvals[0]);
        Assert.Contains("Carrying on.", this.sink.Text.ToString());
    }

    [Fact]
    public async Task NoLimitNeverAsks()
    {
        this.api.Reply(Sse.Turn(Sse.Text("Fine."), inputTokens: 200_000));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Hi");
        await this.SendAsync(agent, "Hi again");

        Assert.Empty(this.sink.Approvals);
    }

    [Fact]
    public async Task ChangingEffortAddsAMessageInsteadOfChangingTheRequest()
    {
        this.api.Reply(Sse.Turn(Sse.Text("One."))).Reply(Sse.Turn(Sse.Text("Two."))).Reply(Sse.Turn(Sse.Text("Three.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "First");
        agent.Effort = EffortLevel.XHigh;
        await this.SendAsync(agent, "Second");
        await this.SendAsync(agent, "Third");

        // The first request has no beta and nothing extra.
        Assert.Null(this.api.Betas[0]);
        Assert.Equal(1, this.api.Requests[0].RootElement.GetProperty("messages").GetArrayLength());

        // The request stays at the effort the chat started at, so the cache holds; the change is a system message
        // just before the next user message.
        JsonElement second = this.api.Requests[1].RootElement;
        Assert.Equal("low", second.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("mid-conversation-output-config-2026-07-01", this.api.Betas[1]);
        JsonElement messages = second.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        JsonElement change = messages[2];
        Assert.Equal("system", change.GetProperty("role").GetString());
        Assert.Equal(0, change.GetProperty("content").GetArrayLength());
        Assert.Equal("xhigh", change.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("user", messages[3].GetProperty("role").GetString());

        // It's only added once.
        Assert.Equal(6, this.api.Requests[2].RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal("low", this.api.Requests[2].RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task ANewChatStartsAtTheCurrentEffort()
    {
        this.api.Reply(Sse.Turn(Sse.Text("One.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.Effort = EffortLevel.High;

        await this.SendAsync(agent, "First");

        Assert.Equal("high", this.api.Requests[0].RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Null(this.api.Betas[0]);
    }

    [Fact]
    public async Task SummarizesAtTheChosenContextSize()
    {
        this.api.Reply(Sse.Turn(Sse.Text("Long answer."), inputTokens: 5_000))
            .Reply(Sse.Turn(Sse.Text("Summary of it all.")))
            .Reply(Sse.Turn(Sse.Text("Short answer.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.CompactAtTokens = 4_000;

        await this.SendAsync(agent, "First");
        await this.SendAsync(agent, "Second");

        Assert.Equal(3, this.api.Requests.Count);
        JsonElement compact = this.api.Requests[1].RootElement.GetProperty("messages");
        Assert.Contains("replaced by a summary", compact[compact.GetArrayLength() - 1].GetProperty("content").GetString());

        // The next request starts from the summary.
        JsonElement after = this.api.Requests[2].RootElement.GetProperty("messages");
        Assert.Equal(2, after.GetArrayLength());
        Assert.Contains("Summary of it all.", after[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TheChatIsCachedForAnHourAndASubagentForFiveMinutes()
    {
        this.api
            .Reply(ToolTurn("toolu_main", "task", new { description = "Look", prompt = "Look around." }))
            .Reply(Sse.Turn(Sse.Text("Report.")))
            .Reply(Sse.Turn(Sse.Text("Done.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Go");

        JsonElement chat = this.api.Requests[0].RootElement.GetProperty("cache_control");
        Assert.Equal("1h", chat.GetProperty("ttl").GetString());
        JsonElement subagent = this.api.Requests[1].RootElement.GetProperty("cache_control");
        Assert.False(subagent.TryGetProperty("ttl", out _));
    }

    [Fact]
    public async Task RereadingAnUnchangedFileGetsANoteInsteadOfTheTextAgain()
    {
        string big = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i} of a longer file"));
        File.WriteAllText(Path.Combine(this.repo, "big.txt"), big);
        this.api
            .Reply(ToolTurn("toolu_1", "read_file", new { path = "big.txt" }))
            .Reply(ToolTurn("toolu_2", "read_file", new { path = "big.txt" }))
            .Reply(ToolTurn("toolu_3", "read_file", new { path = "big.txt", start_line = 1, end_line = 50 }))
            .Reply(Sse.Turn(Sse.Text("Read it.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Read big.txt twice");

        Assert.Contains("line 200 of a longer file", this.ToolResult(1));
        Assert.Contains("hasn't changed", this.ToolResult(2));
        Assert.Contains("line 50 of a longer file", this.ToolResult(3)); // other lines are a different read
    }

    [Fact]
    public async Task RereadingAChangedFileGetsTheNewText()
    {
        string path = Path.Combine(this.repo, "big.txt");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i} of a longer file")));
        this.api
            .Reply(ToolTurn("toolu_1", "read_file", new { path = "big.txt" }))
            .Reply(request =>
            {
                File.AppendAllText(path, "\na new last line");
                return ToolTurn("toolu_2", "read_file", new { path = "big.txt" });
            })
            .Reply(Sse.Turn(Sse.Text("Read it.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Read big.txt twice");

        Assert.Contains("a new last line", this.ToolResult(2));
    }

    [Fact]
    public async Task AReadThatWasRewoundIsSentAgain()
    {
        File.WriteAllText(Path.Combine(this.repo, "big.txt"), string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i} of a longer file")));
        this.api
            .Reply(ToolTurn("toolu_1", "read_file", new { path = "big.txt" }))
            .Reply(Sse.Turn(Sse.Text("Read it.")))
            .Reply(ToolTurn("toolu_2", "read_file", new { path = "big.txt" }))
            .Reply(Sse.Turn(Sse.Text("Read it again.")));
        using ClaudeAgent agent = this.NewAgent();

        string checkpoint = agent.Checkpoint!;
        await this.SendAsync(agent, "Read big.txt");
        Assert.True(agent.Rewind(checkpoint));
        await this.SendAsync(agent, "Read big.txt");

        Assert.Contains("line 200 of a longer file", this.ToolResult(3));
    }

    [Fact]
    public async Task AMinifiedFileIsReadAPartAtATime()
    {
        // One line of half a megabyte, then ordinary lines.
        File.WriteAllText(Path.Combine(this.repo, "app.min.js"), new string('x', 500_000) + "\n" + string.Join("\n", Enumerable.Range(1, 5_000).Select(i => $"line {i}")));
        this.api.Reply(ToolTurn("toolu_1", "read_file", new { path = "app.min.js" })).Reply(Sse.Turn(Sse.Text("Read it.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Read app.min.js");

        string result = this.ToolResult(1);
        Assert.True(result.Length < 41_000, $"{result.Length} characters");
        Assert.Contains("[line truncated: 500,000 characters", result);
        Assert.Contains("line 1\n", result);
        Assert.Contains("use start_line/end_line to read more", result);
    }

    private static string BigFile() => string.Join("\n", Enumerable.Range(1, 2_000).Select(i => $"line {i} of a file that's big enough to matter"));

    [Fact]
    public async Task APromptTooLongForTheWindowLosesItsLargestToolResultAndIsSentAgain()
    {
        File.WriteAllText(Path.Combine(this.repo, "big.txt"), BigFile());
        this.api
            .Reply(Sse.Turn(Sse.ToolUse("toolu_1", "read_file", new { path = "big.txt" }) + Sse.ToolUse("toolu_2", "read_file", new { path = "hello.txt" }, 1), stopReason: "tool_use"))
            .ReplyError(400, "invalid_request_error", "prompt is too long: 201000 tokens > 200000 maximum")
            .Reply(ToolTurn("toolu_3", "read_file", new { path = "big.txt" }))
            .Reply(Sse.Turn(Sse.Text("Done.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Read big.txt and hello.txt");

        Assert.Equal(4, this.api.Requests.Count);
        JsonElement messages = this.api.Requests[2].RootElement.GetProperty("messages");
        JsonElement[] results = messages[messages.GetArrayLength() - 1].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(ClaudeAgent.RemovedResult, results[0].GetProperty("content").GetString());
        Assert.Contains("line two", results[1].GetProperty("content").GetString()); // the small one stays
        Assert.Contains("line 100 of a file", this.ToolResult(3)); // read again in full, not "hasn't changed"
        Assert.Equal("Done.", this.sink.Text.ToString());
    }

    [Fact]
    public async Task ToolResultsSentSinceTheLastResponseCountTowardsCompacting()
    {
        File.WriteAllText(Path.Combine(this.repo, "big.txt"), BigFile());
        this.api
            .Reply(ToolTurn("toolu_1", "read_file", new { path = "big.txt" })) // 100 input tokens, as far as the API said
            .Reply(Sse.Turn(Sse.Text("Summary of it all.")))
            .Reply(Sse.Turn(Sse.Text("Carried on.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.CompactAtTokens = 4_000; // the read is about 10K tokens

        await this.SendAsync(agent, "Read big.txt");

        Assert.Equal(3, this.api.Requests.Count);
        JsonElement compact = this.api.Requests[1].RootElement.GetProperty("messages");
        Assert.Contains("replaced by a summary", compact[compact.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.Contains("Summary of it all.", this.api.Requests[2].RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("prompt is too long: 215000 tokens > 200000 maximum", (15_000 + 8_000) * 4)]
    [InlineData("prompt is too long", 0)]
    public void TheExcessIsReadFromTheError(string error, long chars) => Assert.Equal(chars, ClaudeAgent.ExcessChars(error));

    [Fact]
    public async Task CodeTheWebToolsRanGoesBackWithTheTurn()
    {
        this.api.Reply(Sse.Turn(
                Sse.Whole(new { type = "server_tool_use", id = "srvtoolu_1", name = "bash_code_execution", input = new { command = "python filter.py" } }, 0)
                + Sse.Whole(new { type = "bash_code_execution_tool_result", tool_use_id = "srvtoolu_1", content = new { type = "bash_code_execution_result", stdout = "3 hits", stderr = "", return_code = 0, content = Array.Empty<object>() } }, 1)
                + Sse.Text("Found three.", 2)))
            .Reply(Sse.Turn(Sse.Text("Sure.")));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Search for it");
        await this.SendAsync(agent, "Thanks");

        JsonElement assistant = this.api.Requests[1].RootElement.GetProperty("messages")[1];
        string[] types = assistant.GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()!).ToArray();
        Assert.Equal(["server_tool_use", "bash_code_execution_tool_result", "text"], types);
        JsonElement result = assistant.GetProperty("content")[1];
        Assert.Equal("srvtoolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.Equal("3 hits", result.GetProperty("content").GetProperty("stdout").GetString());
    }
}
