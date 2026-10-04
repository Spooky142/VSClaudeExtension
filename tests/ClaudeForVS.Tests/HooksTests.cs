using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class HooksTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    public HooksTests()
    {
        Directory.CreateDirectory(Path.Combine(this.Repo, ".claude"));
        Directory.CreateDirectory(Path.Combine(this.Home, ".claude"));
    }

    private string Repo => Path.Combine(this.root, "repo");

    private string Home => Path.Combine(this.root, "home");

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private HookRules Load(string? repoJson, string? userJson = null)
    {
        if (repoJson != null)
        {
            File.WriteAllText(Path.Combine(this.Repo, ".claude", "settings.json"), repoJson);
        }

        if (userJson != null)
        {
            File.WriteAllText(Path.Combine(this.Home, ".claude", "settings.json"), userJson);
        }

        return HookRules.Load(this.Repo, this.Home);
    }

    private const string FormatterHook = """
        { "hooks": { "PostToolUse": [ { "matcher": "Edit|Write", "hooks": [ { "type": "command", "command": "dotnet format", "timeout": 30 } ] } ] } }
        """;

    [Fact]
    public void NoSettingsMeansNoHooks()
    {
        HookRules rules = HookRules.Load(this.Repo, this.Home);
        Assert.Equal(0, rules.Count);
        Assert.False(rules.NeedsTrust);
    }

    [Fact]
    public void ReadsHooksFromSettingsAndMarksTheRepositorysOwn()
    {
        HookRules rules = this.Load(FormatterHook, """
            { "hooks": { "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "echo hi" } ] } ] } }
            """);

        Assert.Equal(2, rules.Count);
        HookCommand user = Assert.Single(rules.Commands, c => c.Event == HookEvent.UserPromptSubmit);
        Assert.False(user.FromRepository);
        Assert.Equal(60, user.TimeoutSeconds);
        HookCommand repo = Assert.Single(rules.Commands, c => c.Event == HookEvent.PostToolUse);
        Assert.True(repo.FromRepository);
        Assert.Equal(30, repo.TimeoutSeconds);
        Assert.True(rules.NeedsTrust);
        Assert.Equal(2, rules.Files.Count);
        Assert.Single(rules.WithoutRepository().Commands);
    }

    [Fact]
    public void IgnoresMalformedHooks()
    {
        HookRules rules = this.Load("""
            { "hooks": { "PreToolUse": [ { "hooks": [ { "type": "prompt", "command": "x" }, { "type": "command" }, { "type": "command", "command": "  " } ] }, 5 ], "Nonsense": [] } }
            """);
        Assert.Equal(0, rules.Count);
    }

    [Fact]
    public void AHookCanAskForBash()
    {
        HookRules rules = this.Load("""
            { "hooks": { "PreToolUse": [ { "hooks": [
              { "type": "command", "command": "echo a" },
              { "type": "command", "command": "echo b", "shell": "bash" },
              { "type": "command", "command": "echo c", "shell": "powershell" } ] } ] } }
            """);

        Assert.Equal([false, true, false], rules.Commands.Select(c => c.Bash).ToArray());

        // The shell is part of what the user trusts.
        string trust = rules.TrustKey();
        Assert.NotEqual(trust, this.Load("""
            { "hooks": { "PreToolUse": [ { "hooks": [
              { "type": "command", "command": "echo a" },
              { "type": "command", "command": "echo b" },
              { "type": "command", "command": "echo c", "shell": "powershell" } ] } ] } }
            """).TrustKey());
    }

    [Fact]
    public async Task ABashHookGetsItsInputAndExitCode()
    {
        if (ShellRunner.FindBash() is null)
        {
            return; // Git for Windows' bash isn't installed here; nothing to run
        }

        ShellResult result = await ShellRunner.RunSeparatelyAsync(
            this.Repo,
            "read -r line; echo \"got:$line:$HOOK_TEST\"; echo oops >&2; exit 2",
            TimeSpan.FromSeconds(60),
            CancellationToken.None,
            stdin: "hello\n",
            environment: new Dictionary<string, string> { ["HOOK_TEST"] = "yes" },
            bash: true);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("got:hello:yes", result.Stdout);
        Assert.Contains("oops", result.Stderr);
    }

    [Fact]
    public async Task AHookThatWritesALotBeforeReadingDoesntHang()
    {
        // More than a pipe buffer in, and more than one out before the input is read.
        string big = new string('x', 300_000);
        ShellResult result = await ShellRunner.RunSeparatelyAsync(
            this.Repo,
            "1..2000 | ForEach-Object { 'y' * 100 }; $null = [Console]::In.ReadToEnd(); 'done'",
            TimeSpan.FromSeconds(30),
            CancellationToken.None,
            stdin: big);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("done", result.Stdout.TrimEnd());
    }

    [Fact]
    public void TrustKeyChangesWhenARepositoryHookChanges()
    {
        string before = this.Load(FormatterHook).TrustKey();
        Assert.Equal(before, this.Load(FormatterHook).TrustKey());
        Assert.StartsWith("hooks:", before);
        Assert.NotEqual(before, this.Load(FormatterHook.Replace("dotnet format", "dotnet format --verify")).TrustKey());
    }

    [Theory]
    [InlineData(null, "Bash", true)]
    [InlineData("", "Edit", true)]
    [InlineData("*", "Edit", true)]
    [InlineData("Edit|Write", "Write", true)]
    [InlineData("Edit|Write", "Read", false)]
    [InlineData("Edit", "MultiEdit", false)]
    [InlineData("mcp__github__.*", "mcp__github__create_issue", true)]
    [InlineData("(", "Bash", false)]
    public void MatchersAreAnchoredRegularExpressions(string? matcher, string tool, bool expected) =>
        Assert.Equal(expected, HookRules.Matches(matcher, [tool]));

    [Fact]
    public void MatchesEitherToolName()
    {
        HookRules rules = this.Load(FormatterHook);
        Assert.Single(rules.For(HookEvent.PostToolUse, HookRules.ClaudeCodeName("edit_file"), "edit_file"));
        Assert.Empty(rules.For(HookEvent.PostToolUse, HookRules.ClaudeCodeName("read_file"), "read_file"));
        Assert.Empty(rules.For(HookEvent.PreToolUse, "Edit", "edit_file"));
    }

    [Fact]
    public void HookInputUsesClaudeCodesFieldNames()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["path"] = JsonSerializer.SerializeToElement("src/A.cs"),
            ["args"] = JsonSerializer.SerializeToElement(new[] { "status", "-s" }),
        };
        Dictionary<string, JsonElement> mapped = HookRules.HookInput("git", input);
        Assert.Equal("src/A.cs", mapped["file_path"].GetString());
        Assert.Equal("git status -s", mapped["command"].GetString());
    }

    [Fact]
    public void ExitCodeTwoBlocksWithStderr()
    {
        HookOutcome outcome = HookRules.Interpret(HookEvent.PreToolUse, 2, "ignored", "  Don't touch secrets.\n");
        Assert.True(outcome.Blocked);
        Assert.Equal("Don't touch secrets.", outcome.Message);
    }

    [Fact]
    public void OtherFailuresDontBlock()
    {
        Assert.False(HookRules.Interpret(HookEvent.PreToolUse, 1, string.Empty, "boom").Blocked);
        Assert.False(HookRules.Interpret(HookEvent.PreToolUse, 0, string.Empty, string.Empty).Blocked);
    }

    [Fact]
    public void JsonOutputCanBlockOrAddContext()
    {
        HookOutcome block = HookRules.Interpret(HookEvent.PostToolUse, 0, """{"decision":"block","reason":"Tests fail."}""", string.Empty);
        Assert.True(block.Blocked);
        Assert.Equal("Tests fail.", block.Message);

        HookOutcome deny = HookRules.Interpret(HookEvent.PreToolUse, 0,
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"No force pushes."}}""", string.Empty);
        Assert.True(deny.Blocked);
        Assert.Equal("No force pushes.", deny.Message);

        HookOutcome stop = HookRules.Interpret(HookEvent.PreToolUse, 0, """{"continue":false,"stopReason":"Frozen."}""", string.Empty);
        Assert.True(stop.Blocked);
        Assert.Equal("Frozen.", stop.Message);

        HookOutcome context = HookRules.Interpret(HookEvent.UserPromptSubmit, 0, """{"hookSpecificOutput":{"additionalContext":"On branch main."}}""", string.Empty);
        Assert.False(context.Blocked);
        Assert.Equal("On branch main.", context.Context);
    }

    [Fact]
    public void PlainStdoutIsContextForPromptHooksOnly()
    {
        Assert.Equal("Today is Monday.", HookRules.Interpret(HookEvent.UserPromptSubmit, 0, "Today is Monday.\n", string.Empty).Context);
        Assert.Equal(string.Empty, HookRules.Interpret(HookEvent.PreToolUse, 0, "noise", string.Empty).Context);
    }

    [Fact]
    public void NotJsonThatLooksLikeJsonIsIgnored() =>
        Assert.False(HookRules.Interpret(HookEvent.PreToolUse, 0, "{ not json", string.Empty).Blocked);

    // The rest start powershell.exe, as run_command does.

    [Fact]
    public async Task ShellRunnerFeedsStdinAndEnvironmentAndSeparatesStreams()
    {
        ShellResult result = await ShellRunner.RunSeparatelyAsync(
            this.Repo,
            "$text = [Console]::In.ReadToEnd(); Write-Output \"in=$text env=$env:HOOK_TEST\"; [Console]::Error.WriteLine('oops'); exit 3",
            TimeSpan.FromSeconds(60),
            CancellationToken.None,
            stdin: "héllo",
            environment: new Dictionary<string, string> { ["HOOK_TEST"] = "yes" });

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("env=yes", result.Stdout);
        Assert.Contains("oops", result.Stderr);
        Assert.DoesNotContain("oops", result.Stdout);
        Assert.Contains("oops", result.Combined);
    }

    [Fact]
    public async Task RunnerBlocksOnExitCodeTwoAndPassesTheEventAsJson()
    {
        string log = Path.Combine(this.root, "event.json");
        string command = $"[Console]::In.ReadToEnd() | Set-Content -LiteralPath '{log}' -Encoding UTF8; [Console]::Error.WriteLine('Not today.'); exit 2";
        HookRules rules = this.Load($$"""
            { "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": {{JsonSerializer.Serialize(command)}} } ] } ] } }
            """);
        var runner = new HookRunner(rules, this.Repo, "abc");
        var reports = new List<string>();

        HookOutcome outcome = await runner.RunAsync(
            HookEvent.PreToolUse,
            new Dictionary<string, object?> { ["tool_name"] = "Bash", ["tool_input"] = new { command = "dotnet build" } },
            (title, _) => reports.Add(title),
            CancellationToken.None,
            "Bash", "run_command");

        Assert.True(outcome.Blocked);
        Assert.Equal("Not today.", outcome.Message);
        Assert.Empty(reports);

        using JsonDocument sent = JsonDocument.Parse(File.ReadAllText(log).TrimStart('﻿'));
        Assert.Equal("PreToolUse", sent.RootElement.GetProperty("hook_event_name").GetString());
        Assert.Equal("abc", sent.RootElement.GetProperty("session_id").GetString());
        Assert.Equal("dotnet build", sent.RootElement.GetProperty("tool_input").GetProperty("command").GetString());
        Assert.Equal(this.Repo, sent.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task RunnerReportsAFailingHookAndCarriesOn()
    {
        HookRules rules = this.Load("""
            { "hooks": { "PostToolUse": [ { "hooks": [ { "type": "command", "command": "exit 7" } ] } ] } }
            """);
        var reports = new List<string>();

        HookOutcome outcome = await new HookRunner(rules, this.Repo, "abc").RunAsync(
            HookEvent.PostToolUse, new Dictionary<string, object?>(), (title, _) => reports.Add(title), CancellationToken.None, "Edit");

        Assert.False(outcome.Blocked);
        Assert.Single(reports);
        Assert.StartsWith("Hook failed", reports.Single());
    }
}
