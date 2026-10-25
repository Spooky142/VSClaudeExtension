using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeForVS.Agent;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class McpConfigTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    public McpConfigTests()
    {
        Directory.CreateDirectory(Path.Combine(this.Repo, ".claude"));
        Directory.CreateDirectory(this.Home);
    }

    private string Repo => Path.Combine(this.root, "repo");

    private string Home => Path.Combine(this.root, "home");

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static string? Env(string name) => name switch { "TOKEN" => "secret", "EMPTY" => string.Empty, _ => null };

    [Fact]
    public void ExpandsVariablesWithDefaults()
    {
        Assert.Equal("Bearer secret", McpConfig.Expand("Bearer ${TOKEN}", Env));
        Assert.Equal("x-fallback", McpConfig.Expand("x-${MISSING:-fallback}", Env));
        Assert.Equal("x-", McpConfig.Expand("x-${MISSING}", Env));
        Assert.Equal("dflt", McpConfig.Expand("${EMPTY:-dflt}", Env));
    }

    [Fact]
    public void ReadsUserProjectAndLocalServersWithPrecedence()
    {
        string repoKey = this.Repo.Replace('\\', '/');
        File.WriteAllText(Path.Combine(this.Home, ".claude.json"), $$"""
            {
              "mcpServers": {
                "shared": { "command": "user-cmd" },
                "userOnly": { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ${TOKEN}" } }
              },
              "projects": {
                "{{repoKey}}": { "mcpServers": { "local": { "command": "local-cmd", "args": ["--x"] } }, "enabledMcpjsonServers": ["approved"] }
              }
            }
            """);
        File.WriteAllText(Path.Combine(this.Repo, ".mcp.json"), """
            { "mcpServers": {
                "shared": { "command": "npx", "args": ["-y", "pkg"], "env": { "KEY": "${TOKEN}" } },
                "approved": { "command": "a" },
                "disabledOne": { "command": "d" },
                "broken": { "type": "http" }
            } }
            """);
        File.WriteAllText(Path.Combine(this.Repo, ".claude", "settings.local.json"), """{ "disabledMcpjsonServers": ["disabledOne"] }""");

        McpConfiguration config = McpConfig.Load(this.Repo, this.Home, Env);
        Assert.Equal(["approved", "local", "shared", "userOnly"], config.Servers.Select(s => s.Name));

        McpServerConfig shared = config.Servers.Single(s => s.Name == "shared");
        Assert.Equal(McpScope.Project, shared.Scope); // the project's beats the user's
        Assert.Equal("npx -y pkg", shared.Describe());
        Assert.Equal("secret", shared.Env["KEY"]);
        Assert.False(config.IsPreApproved(shared));
        Assert.True(config.IsPreApproved(config.Servers.Single(s => s.Name == "approved")));

        McpServerConfig http = config.Servers.Single(s => s.Name == "userOnly");
        Assert.Equal("http", http.Type);
        Assert.Equal("Bearer secret", http.Headers["Authorization"]);
        Assert.True(config.IsPreApproved(http)); // user servers don't need approving

        Assert.Equal(McpScope.Local, config.Servers.Single(s => s.Name == "local").Scope);
    }

    [Fact]
    public void TheRepositorysSharedSettingsCantApproveItsOwnServers()
    {
        File.WriteAllText(Path.Combine(this.Repo, ".mcp.json"), """{ "mcpServers": { "evil": { "command": "e" }, "named": { "command": "n" }, "off": { "command": "o" } } }""");
        File.WriteAllText(Path.Combine(this.Repo, ".claude", "settings.json"), """
            { "enableAllProjectMcpServers": true, "enabledMcpjsonServers": ["named"], "disabledMcpjsonServers": ["off"] }
            """);

        McpConfiguration config = McpConfig.Load(this.Repo, this.Home, Env);
        Assert.Equal(["evil", "named"], config.Servers.Select(s => s.Name)); // it can still turn servers off
        Assert.All(config.Servers, s => Assert.False(config.IsPreApproved(s)));

        // The same approval in the user's own local settings counts.
        File.WriteAllText(Path.Combine(this.Repo, ".claude", "settings.local.json"), """{ "enabledMcpjsonServers": ["named"] }""");
        config = McpConfig.Load(this.Repo, this.Home, Env);
        Assert.True(config.IsPreApproved(config.Servers.Single(s => s.Name == "named")));
    }

    [Fact]
    public void FingerprintChangesWithTheConfiguration()
    {
        McpServerConfig a = McpConfig.Parse("s", JsonDocument.Parse("""{"command":"x","args":["1"]}""").RootElement, McpScope.Project, Env)!;
        McpServerConfig b = McpConfig.Parse("s", JsonDocument.Parse("""{"command":"x","args":["2"]}""").RootElement, McpScope.Project, Env)!;
        Assert.Equal(a.Fingerprint(), McpConfig.Parse("s", JsonDocument.Parse("""{"command":"x","args":["1"]}""").RootElement, McpScope.Project, Env)!.Fingerprint());
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void TurnedOffServersAreRememberedPerRepository()
    {
        var store = new McpDisabledServers(Path.Combine(this.root, "mcp-servers.json"));
        string other = Path.Combine(this.root, "other");
        store.Set(this.Repo, "github", enabled: false);
        store.Set(this.Repo, "files", enabled: false);
        store.Set(this.Repo, "files", enabled: true);

        Assert.Equal(["github"], store.For(this.Repo));
        Assert.True(new McpDisabledServers(Path.Combine(this.root, "mcp-servers.json")).IsDisabled(this.Repo.ToUpperInvariant() + "\\", "github"));
        Assert.Empty(store.For(other));

        store.Set(this.Repo, "github", enabled: true);
        Assert.Empty(store.For(this.Repo));
    }

    [Fact]
    public void TurnedOffServersBecomeClaudeCodeDenyRules()
    {
        Assert.Equal("mcp__github", McpDisabledServers.ToolRule("github"));
        Assert.Equal("mcp__my_server_v2", McpDisabledServers.ToolRule("my server.v2"));
    }

    [Fact]
    public void ServerRowsShowWhetherTheyreOnAndApproved()
    {
        McpServerConfig stdio = McpConfig.Parse("s", JsonDocument.Parse("""{"command":"x"}""").RootElement, McpScope.Project, Env)!;
        McpServerConfig sse = McpConfig.Parse("e", JsonDocument.Parse("""{"type":"sse","url":"https://example.com/sse"}""").RootElement, McpScope.User, Env)!;

        var row = new McpServerRow(stdio, isEnabled: true, isApproved: false);
        Assert.Equal(McpServerState.NeedsApproval, row.State);
        int changes = 0;
        row.EnabledChanged += (_, _) => changes++;
        row.IsEnabled = false;
        Assert.Equal((McpServerState.Off, "Off", 1), (row.State, row.StatusText, changes));

        row.SetStatus(McpServerState.Checking, "Checking…");
        Assert.False(row.CanCheck);
        Assert.False(new McpServerRow(sse, isEnabled: true, isApproved: true).CanCheck);
    }

    [Fact]
    public void ToolResultsBecomeText()
    {
        McpCallResult result = McpClient.ToResult(JsonNode.Parse("""
            {"content":[{"type":"text","text":"hello"},{"type":"image","data":"..."},{"type":"resource","resource":{"uri":"file:///x","text":"inside"}}],"isError":true}
            """));
        Assert.True(result.IsError);
        Assert.Equal("hello\n[image content not shown]\ninside", result.Text);

        Assert.Equal("{\"a\":1}", McpClient.ToResult(JsonNode.Parse("""{"content":[],"structuredContent":{"a":1}}""")).Text);
        Assert.Equal("(no output)", McpClient.ToResult(JsonNode.Parse("{}")).Text);
    }
}

public class DiagnosticsTests
{
    private const string Root = @"C:\repo";

    private static readonly List<ErrorListItem> Items =
    [
        new("warning", "CS0168", "unused variable", @"C:\repo\src\B.cs", 4, "App", "Build"),
        new("error", "CS1002", "; expected", @"C:\repo\src\A.cs", 12, "App", "IntelliSense"),
        new("message", "IDE0001", "simplify", @"C:\repo\src\A.cs", 1, "App"),
        new("error", "CS0103", "name does not exist", @"C:\repo\tests\T.cs", 7, "Tests", "Build"),
        new("error", "MSB3073", "command exited", null, 0, null, "Build"),
    ];

    [Fact]
    public void ListsErrorsThenWarningsWithRelativePaths()
    {
        string text = Diagnostics.Format(Items, Root, string.Empty, includeWarnings: true);
        string[] lines = text.Split('\n');
        Assert.Equal("3 errors, 1 warning:", lines[0]);
        Assert.Equal("(no file): error MSB3073: command exited (Build)", lines[1]);
        Assert.Equal("src/A.cs:12: error CS1002: ; expected [App] (IntelliSense)", lines[2]);
        Assert.Equal("tests/T.cs:7: error CS0103: name does not exist [Tests] (Build)", lines[3]);
        Assert.Equal("src/B.cs:4: warning CS0168: unused variable [App] (Build)", lines[4]);
        Assert.DoesNotContain("IDE0001", text); // messages are left out
    }

    [Fact]
    public void FiltersByPathAndSeverity()
    {
        Assert.Equal("1 error in src:\nsrc/A.cs:12: error CS1002: ; expected [App] (IntelliSense)", Diagnostics.Format(Items, Root, "src", includeWarnings: false));
        Assert.Equal("No errors or warnings in docs.", Diagnostics.Format(Items, Root, "docs", includeWarnings: true));
        Assert.Equal("No errors.", Diagnostics.Format([], Root, string.Empty, includeWarnings: false));
    }
}

public class TaskListTests
{
    [Fact]
    public void ParsesAndFormatsTodos()
    {
        IReadOnlyList<TaskItem> tasks = TaskList.Parse(JsonDocument.Parse("""
            {"todos":[{"content":"A","status":"completed","activeForm":"Doing A"},{"content":" ","status":"pending"},{"content":"B","status":"weird"}]}
            """).RootElement);
        Assert.Equal(2, tasks.Count);
        Assert.Equal("pending", tasks[1].Status); // unknown statuses count as to do
        Assert.Equal("[x] A\n[ ] B", TaskList.Format(tasks));
        Assert.Empty(TaskList.Parse(JsonDocument.Parse("""{"todos":"no"}""").RootElement));
    }
}

public class CommitMessageTests
{
    [Fact]
    public void PromptIncludesStyleInstructionsAndDiff()
    {
        string prompt = CommitMessageWriter.BuildPrompt("diff --git a/x b/x", staged: true, "Added X.\nFixed Y.", "Use past tense.");
        Assert.Contains("staged changes", prompt);
        Assert.Contains("bullet points", prompt);
        Assert.Contains("<recent commit subjects>\nAdded X.\nFixed Y.\n</recent commit subjects>", prompt);
        Assert.Contains("<project instructions>\nUse past tense.\n</project instructions>", prompt);
        Assert.Contains("<diff>\ndiff --git a/x b/x\n</diff>", prompt);

        Assert.DoesNotContain("<project instructions>", CommitMessageWriter.BuildPrompt("d", staged: false, string.Empty, null));
        Assert.Contains("[diff truncated]", CommitMessageWriter.BuildPrompt(new string('x', 100_000), staged: false, string.Empty, null));
    }

    [Fact]
    public void CleansUpWhatModelsAdd()
    {
        string nl = Environment.NewLine;
        Assert.Equal("Fixed the bug.", CommitMessageWriter.Clean("```\nFixed the bug.\n```"));
        Assert.Equal("Fixed the bug.", CommitMessageWriter.Clean("Commit message: \"Fixed the bug.\""));
        Assert.Equal($"Added X.{nl}{nl}Because Y.", CommitMessageWriter.Clean("  Added X.\r\n\r\nBecause Y.  "));
    }
}

public class TestExplorerTextTests
{
    private const string Copied = """
        Test Name:	MyApp.Tests.MathTests.Adds(a: 1, b: 2)
        Test FullName:	MyApp.Tests.MathTests.Adds
        Test Outcome:	Failed
        Result Message:	Assert.Equal() Failure
        Test Name:	MyApp.Tests.MathTests.Divides
        Test Outcome:	Failed
        """;

    [Fact]
    public void CountsAndNamesTests()
    {
        Assert.Equal(2, TestExplorerText.CountTests(Copied));
        Assert.Equal(["MyApp.Tests.MathTests.Adds(a: 1, b: 2)", "MyApp.Tests.MathTests.Divides"], TestExplorerText.Names(Copied));
        Assert.Equal(1, TestExplorerText.CountTests("some details without the usual labels"));
        Assert.Equal(0, TestExplorerText.CountTests("  "));
    }

    [Fact]
    public void AttachmentShowsTheTests()
    {
        Attachment one = Attachment.ForTests("Test Name:\tNs.Class.Method(x: 1)\nTest Outcome:\tFailed");
        Assert.Equal(AttachmentKind.Tests, one.Kind);
        Assert.Equal("Method(x: 1)", one.Label);
        Assert.Contains("Test Explorer", one.Context);

        Assert.Equal("2 tests", Attachment.ForTests(Copied).Label);
    }
}
