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
        // 500K input tokens is $2 at Opus prices, past a $1 limit but short of compacting.
        this.api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "read_file", new { path = "hello.txt" }), stopReason: "tool_use", inputTokens: 500_000))
            .Reply(Sse.Turn(Sse.Text("never sent")));
        this.sink.Decide = _ => Approval.Deny;
        using ClaudeAgent agent = this.NewAgent();
        agent.SpendLimitUsd = 1;

        await this.SendAsync(agent, "Read it");

        Assert.Single(this.api.Requests);
        Assert.Single(this.sink.Approvals);
        Assert.StartsWith("Spending limit reached", this.sink.Approvals[0]);
        Assert.True(agent.Spent > 1.9);
        Assert.Contains("spending limit", this.sink.Text.ToString());
    }

    [Fact]
    public async Task CarriesOnPastTheLimitWhenTheUserSaysYes()
    {
        this.api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "read_file", new { path = "hello.txt" }), stopReason: "tool_use", inputTokens: 500_000))
            .Reply(Sse.Turn(Sse.Text("Finished.")));
        using ClaudeAgent agent = this.NewAgent();
        agent.SpendLimitUsd = 1;

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

        // The subagent starts from the brief alone, with read-only tools.
        JsonElement first = this.api.Requests[1].RootElement;
        Assert.Equal(1, first.GetProperty("messages").GetArrayLength());
        Assert.Contains("research subagent", first.GetProperty("system").GetString());
        string[] names = first.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("read_file", names);
        Assert.DoesNotContain("edit_file", names);
        Assert.DoesNotContain("run_command", names);
        Assert.DoesNotContain("task", names);

        // Only its report comes back to the main conversation, not the file it read.
        Assert.Equal("Report: hello.txt has two lines.", this.ToolResult(3));
        Assert.DoesNotContain("line two", this.api.Requests[3].RootElement.GetRawText());
        Assert.Empty(this.sink.Approvals);
        Assert.Equal("It has two lines.", this.sink.Text.ToString());
        Assert.Contains(this.sink.Activities, a => a.Title.StartsWith("↳ ")); // its tool use is shown, marked as the subagent's
        Assert.Contains(this.sink.Activities, a => a.Title == "Subagent: Read the notes");
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
        agent.SpendLimitUsd = 1;

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
        this.api.Reply(Sse.Turn(Sse.Text("Fine."), inputTokens: 500_000));
        using ClaudeAgent agent = this.NewAgent();

        await this.SendAsync(agent, "Hi");
        await this.SendAsync(agent, "Hi again");

        Assert.Empty(this.sink.Approvals);
    }
}
