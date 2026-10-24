using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class DebugAndContextTests : IDisposable
{
    private const string Repo = @"C:\work\repo";

    private static readonly DebugState Paused = new(
        DebugMode.Paused,
        "unhandled exception",
        "System.NullReferenceException",
        "Object reference not set to an instance of an object.",
        [
            new DebugFrame("App.Orders.Total(Order order)", @"C:\work\repo\src\Orders.cs", 42, "App.dll"),
            new DebugFrame("App.Program.Main()", @"C:\work\repo\src\Program.cs", 9, "App.dll"),
            new DebugFrame("System.Threading.ThreadHelper.ThreadStart()", null, 0, "mscorlib.dll"),
        ],
        [new DebugValue("order", "App.Order", "null")],
        [new DebugValue("sum", "decimal", "0"), new DebugValue("text", "string", "\"line one\nline two\"")],
        3,
        "App.exe");

    private readonly FakeHost host = new();

    public DebugAndContextTests() => VisualStudioHost.Current = this.host;

    public void Dispose() => VisualStudioHost.Current = null;

    [Fact]
    public void FormatsAPausedProgram()
    {
        string text = DebugReport.Format(Paused, Repo);

        Assert.StartsWith("Paused in the debugger (App.exe): unhandled exception · 3 threads.", text);
        Assert.Contains("Exception: System.NullReferenceException: Object reference not set", text);
        Assert.Contains("#0 App.Orders.Total(Order order) at src/Orders.cs:42", text);
        Assert.Contains("#1 App.Program.Main() at src/Program.cs:9", text);
        Assert.Contains("#2 System.Threading.ThreadHelper.ThreadStart() [mscorlib.dll]", text);
        Assert.Contains("order (App.Order) = null", text);
        Assert.Contains("sum (decimal) = 0", text);
        Assert.Contains("text (string) = \"line one line two\"", text); // values stay on one line
        Assert.DoesNotContain(@"C:\work", text);
    }

    [Fact]
    public void LimitsTheStack()
    {
        string text = DebugReport.Format(Paused, Repo, frames: 1);

        Assert.Contains("#0 ", text);
        Assert.DoesNotContain("#1 ", text);
        Assert.Contains("… 2 more frames", text);
    }

    [Fact]
    public void SaysWhenThereIsNothingToRead()
    {
        Assert.Contains("isn't debugging", DebugReport.Format(DebugState.NotDebugging, Repo));
        Assert.Contains("is running", DebugReport.Format(new DebugState(DebugMode.Running, null, null, null, [], [], [], null, "App.exe"), Repo));
    }

    [Fact]
    public void ALongValueIsCut()
    {
        var state = Paused with { Locals = [new DebugValue("big", "string", new string('x', 1000))] };
        string text = DebugReport.Format(state, Repo);
        Assert.Contains(new string('x', 300) + "…", text);
        Assert.DoesNotContain(new string('x', 301), text);
    }

    private sealed class FakeHost : IVisualStudioHost
    {
        public DebugState State { get; set; } = Paused;

        public Task<string?> GetSolutionDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<ErrorListItem>> GetDiagnosticsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ErrorListItem>>([]);

        public Task<BuildOutcome> BuildSolutionAsync(CancellationToken cancellationToken) => Task.FromResult(new BuildOutcome(true, "ok"));

        public Task<DebugState> GetDebugStateAsync(CancellationToken cancellationToken) => Task.FromResult(this.State);

        public ActiveDocument? Active { get; set; }

        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken cancellationToken) => Task.FromResult(this.Active);
    }

    [Fact]
    public void FormatsTheActiveDocument()
    {
        var document = new ActiveDocument(
            @"C:\work\repo\src\Orders.cs", true, 42, 13, 20, 68, 40, 41, "decimal sum = 0;\r\nreturn sum;\r\n",
            [(@"C:\work\repo\src\Orders.cs", true), (@"C:\work\repo\src\Program.cs", false), (@"C:\elsewhere\Notes.txt", true)]);

        string text = ActiveDocumentReport.Format(document, Repo, _ => true);

        Assert.StartsWith("Active document: src/Orders.cs (unsaved changes", text);
        Assert.Contains("Caret: line 42, column 13", text);
        Assert.Contains("Visible in the editor: lines 20-68", text);
        Assert.Contains("Selection: lines 40-41:\n```\ndecimal sum = 0;\nreturn sum;\n```", text);
        Assert.Contains("Also open: src/Program.cs, C:/elsewhere/Notes.txt (unsaved)", text);

        string hidden = ActiveDocumentReport.Format(document, Repo, _ => false);
        Assert.DoesNotContain("decimal sum", hidden);
        Assert.Contains("restricted by your permission settings", hidden);

        Assert.Contains("Nothing is selected", ActiveDocumentReport.Format(document with { SelectedText = string.Empty }, Repo, _ => true));
        Assert.StartsWith("No text document is open", ActiveDocumentReport.Format(null, Repo, _ => true));
    }

    [Fact]
    public async Task TheApiEngineCanReadTheActiveDocument()
    {
        string repo = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            this.host.Active = new ActiveDocument(Path.Combine(repo, "src", "Orders.cs"), false, 7, 1, 1, 30, 7, 7, string.Empty, []);
            using var api = new FakeAnthropic();
            api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "get_active_document", new { }), stopReason: "tool_use")).Reply(Sse.Turn(Sse.Text("Line 7.")));
            var sink = new RecordingSink();
            using var agent = new ClaudeAgent("test-key", repo, ClaudeModel.Opus, EffortLevel.Low, AgentMode.Plan, api.Url);

            await agent.SendAsync(new ChatPrompt("What am I looking at?"), sink, CancellationToken.None);

            JsonElement messages = api.Requests[1].RootElement.GetProperty("messages");
            JsonElement result = messages[messages.GetArrayLength() - 1].GetProperty("content").EnumerateArray().First(b => b.GetProperty("type").GetString() == "tool_result");
            Assert.StartsWith("Active document: src/Orders.cs", result.GetProperty("content").GetString());
            Assert.Empty(sink.Approvals);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task TheApiEngineCanReadTheDebugger()
    {
        string repo = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            this.host.State = Paused with { Stack = [new DebugFrame("App.Orders.Total()", Path.Combine(repo, "src", "Orders.cs"), 42, null)] };
            using var api = new FakeAnthropic();
            api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "get_debug_state", new { }), stopReason: "tool_use")).Reply(Sse.Turn(Sse.Text("A null order.")));
            var sink = new RecordingSink();
            using var agent = new ClaudeAgent("test-key", repo, ClaudeModel.Opus, EffortLevel.Low, AgentMode.Plan, api.Url);

            await agent.SendAsync(new ChatPrompt("Why did it crash?"), sink, CancellationToken.None);

            JsonElement messages = api.Requests[1].RootElement.GetProperty("messages");
            JsonElement result = messages[messages.GetArrayLength() - 1].GetProperty("content").EnumerateArray().First(b => b.GetProperty("type").GetString() == "tool_result");
            string text = result.GetProperty("content").GetString()!;
            Assert.Contains("System.NullReferenceException", text);
            Assert.Contains("at src/Orders.cs:42", text); // paths are made relative to the repository
            Assert.Empty(sink.Approvals); // reading the debugger never needs approval, even in Plan mode
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    // ===== Attachments =====

    [Fact]
    public void ADebugStateAttachmentCarriesTheReport()
    {
        Attachment attachment = Attachment.ForDebugState(Paused, Repo);

        Assert.Equal(AttachmentKind.Debug, attachment.Kind);
        Assert.Equal("NullReferenceException", attachment.Label);
        Assert.Contains("src/Orders.cs:42", attachment.Context);
        Assert.Contains("3 stack frames, 2 locals", attachment.ToolTip);
    }

    [Fact]
    public void ADiffAttachmentCountsFilesAndListsUntrackedOnes()
    {
        const string Diff = "diff --git a/A.cs b/A.cs\n--- a/A.cs\n+++ b/A.cs\n@@ -1 +1 @@\n-a\n+b\ndiff --git a/B.cs b/B.cs\n--- a/B.cs\n+++ b/B.cs\n@@ -1 +1 @@\n-c\n+d\n";
        Attachment attachment = Attachment.ForDiff(Diff, ["New.cs"]);

        Assert.Equal(AttachmentKind.Diff, attachment.Kind);
        Assert.Equal("3 changed files", attachment.Label);
        Assert.Contains("+b", attachment.Context);
        Assert.Contains("Untracked files", attachment.Context);
        Assert.Contains("New.cs", attachment.Context);
    }

    [Fact]
    public void AnOutputAttachmentKeepsTheEnd()
    {
        string text = "first line\n" + new string('x', 50_000) + "\nBuild FAILED.";
        Attachment attachment = Attachment.ForOutput("Build", text);

        Assert.Equal("Build", attachment.Label);
        Assert.Contains("Build FAILED.", attachment.Context);
        Assert.DoesNotContain("first line", attachment.Context);
        Assert.Contains("earlier output left out", attachment.Context);
    }

    [Fact]
    public void AnOpenFilesAttachmentMarksUnsavedOnes()
    {
        Attachment attachment = Attachment.ForOpenFiles([(@"C:\work\repo\src\A.cs", false), (@"C:\work\repo\src\B.cs", true)], Repo);

        Assert.Equal("2 open files", attachment.Label);
        Assert.Contains("- src/A.cs\n", attachment.Context);
        Assert.Contains("- src/B.cs (unsaved changes)", attachment.Context);
    }

    [Theory]
    [InlineData("hooks:0123abcd", "Run this repository's hooks (trusted until they change)")]
    [InlineData("build_solution", "Build the solution in Visual Studio")]
    [InlineData("run_tests", "Run tests")]
    [InlineData("mcp__github__create_issue", "MCP tool: github · create_issue")]
    [InlineData("dotnet build", "dotnet build")]
    public void SettingsLabelsToolRulesPlainly(string rule, string label) => Assert.Equal(label, AllowedRule.Label(rule));

    [Fact]
    public void TheNewCommandsAreRecognised()
    {
        foreach (string name in new[] { "/diff", "/output", "/openfiles" })
        {
            Assert.Equal(name, SlashCommands.Parse(name)?.Name);
        }
    }
}
