using System;
using System.IO;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class PermissionRulesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    public PermissionRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(this.Repo, ".claude"));
        Directory.CreateDirectory(Path.Combine(this.Home, ".claude"));
    }

    private string Repo => Path.Combine(this.root, "repo");

    private string Home => Path.Combine(this.root, "home");

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private PermissionRules Load(string projectJson, string? userJson = null, string? localJson = null)
    {
        File.WriteAllText(Path.Combine(this.Repo, ".claude", "settings.json"), projectJson);
        if (userJson != null)
        {
            File.WriteAllText(Path.Combine(this.Home, ".claude", "settings.json"), userJson);
        }

        if (localJson != null)
        {
            File.WriteAllText(Path.Combine(this.Repo, ".claude", "settings.local.json"), localJson);
        }

        return PermissionRules.Load(this.Repo, this.Home);
    }

    private string InRepo(string relative) => Path.Combine(this.Repo, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void NoSettingsMeansNoRules()
    {
        PermissionRules rules = PermissionRules.Load(this.Repo, this.Home);
        Assert.Equal(0, rules.Count);
        Assert.Empty(rules.Files);
        Assert.Equal(PermissionVerdict.None, rules.ForCommand("dotnet build"));
    }

    [Fact]
    public void CommandRulesMatchPrefixesAndWildcards()
    {
        PermissionRules rules = this.Load("{}", localJson: """
            {
              // comments and trailing commas are allowed, as in Claude Code
              "permissions": {
                "allow": ["Bash(dotnet build:*)", "Bash(npm run test *)", "PowerShell(Get-ChildItem)",],
                "deny": ["Bash(git push:*)"],
                "ask": ["Bash(git commit:*)"]
              }
            }
            """);

        Assert.Equal(PermissionVerdict.Allow, rules.ForCommand("dotnet build"));
        Assert.Equal(PermissionVerdict.Allow, rules.ForCommand("dotnet  build src -c Release"));
        Assert.Equal(PermissionVerdict.None, rules.ForCommand("dotnet buildx")); // a prefix stops at a word boundary
        Assert.Equal(PermissionVerdict.Allow, rules.ForCommand("npm run test unit"));
        Assert.Equal(PermissionVerdict.Allow, rules.ForCommand("Get-ChildItem"));
        Assert.Equal(PermissionVerdict.None, rules.ForCommand("Get-ChildItem -Recurse"));
        Assert.Equal(PermissionVerdict.Deny, rules.ForCommand("git push origin main"));
        Assert.Equal(PermissionVerdict.Ask, rules.ForCommand("git commit -m x"));
    }

    [Fact]
    public void AllowNeverCoversAChainButDenyCatchesAnyPartOfOne()
    {
        PermissionRules rules = this.Load("{}", localJson: """{"permissions":{"allow":["Bash(dotnet build:*)"],"deny":["Bash(rm:*)"]}}""");
        Assert.Equal(PermissionVerdict.None, rules.ForCommand("dotnet build && curl evil"));
        Assert.Equal(PermissionVerdict.None, rules.ForCommand("dotnet build; echo hi"));
        Assert.Equal(PermissionVerdict.Deny, rules.ForCommand("dotnet build && rm -rf bin"));
        Assert.Equal(PermissionVerdict.Deny, rules.ForCommand("echo hi | rm x"));
    }

    [Fact]
    public void ABareToolNameCoversEverything()
    {
        PermissionRules rules = this.Load("""{"permissions":{"deny":["Bash","WebSearch","Read(*)"]}}""");
        Assert.Equal(PermissionVerdict.Deny, rules.ForCommand("anything at all"));
        Assert.Equal(PermissionVerdict.Deny, rules.ForTool("WebSearch"));
        Assert.Equal(PermissionVerdict.None, rules.ForTool("WebFetch"));
        Assert.Equal(PermissionVerdict.Deny, rules.ForRead(this.InRepo("src/a.cs")));
    }

    [Fact]
    public void PathRulesFollowGitignoreStyle()
    {
        PermissionRules rules = this.Load("{}", localJson: """
            {"permissions":{
              "deny":["Read(./secrets/**)", "Read(.env)", "Edit(/build)"],
              "allow":["Edit(src/**/*.cs)"],
              "ask":["Edit(*.csproj)"]
            }}
            """);

        Assert.Equal(PermissionVerdict.Deny, rules.ForRead(this.InRepo("secrets/key.txt")));
        Assert.Equal(PermissionVerdict.Deny, rules.ForRead(this.InRepo("secrets/deep/key.txt")));
        Assert.Equal(PermissionVerdict.Deny, rules.ForRead(this.InRepo(".env")));
        Assert.Equal(PermissionVerdict.Deny, rules.ForRead(this.InRepo("config/.env")));
        Assert.Equal(PermissionVerdict.None, rules.ForRead(this.InRepo("src/env.cs")));
        Assert.True(rules.HasReadDenials);

        Assert.Equal(PermissionVerdict.Deny, rules.ForEdit(this.InRepo("build/out.txt"))); // a folder covers what's in it
        Assert.Equal(PermissionVerdict.Allow, rules.ForEdit(this.InRepo("src/Agent/Foo.cs")));
        Assert.Equal(PermissionVerdict.None, rules.ForEdit(this.InRepo("tests/Foo.cs")));
        Assert.Equal(PermissionVerdict.Ask, rules.ForEdit(this.InRepo("src/App/App.csproj")));
    }

    [Fact]
    public void HomeRulesUseTheUsersFolder()
    {
        PermissionRules rules = this.Load("{}", userJson: """{"permissions":{"deny":["Read(~/.ssh/**)"]}}""");
        Assert.Equal(PermissionVerdict.Deny, rules.ForRead(Path.Combine(this.Home, ".ssh", "id_rsa")));
        Assert.Equal(PermissionVerdict.None, rules.ForRead(this.InRepo(".ssh/id_rsa")));
        Assert.Single(rules.Files);
    }

    [Fact]
    public void DenyBeatsAskBeatsAllowAcrossFiles()
    {
        PermissionRules rules = this.Load(
            """{"permissions":{"deny":["Bash(npm publish)"]}}""",
            userJson: """{"permissions":{"ask":["Bash(npm test)"]}}""",
            localJson: """{"permissions":{"allow":["Bash(npm test)","Bash(npm publish)"]}}""");
        Assert.Equal(PermissionVerdict.Ask, rules.ForCommand("npm test"));
        Assert.Equal(PermissionVerdict.Deny, rules.ForCommand("npm publish"));
        Assert.Equal(3, rules.Files.Count);
    }

    [Fact]
    public void TheRepositorysSharedSettingsCantAllowAnything()
    {
        PermissionRules rules = this.Load("""{"permissions":{"allow":["PowerShell","Bash","mcp__evil","Edit"],"deny":["Bash(rm:*)"]}}""");
        Assert.Equal(PermissionVerdict.None, rules.ForCommand("Invoke-WebRequest evil.test"));
        Assert.Equal(PermissionVerdict.None, rules.ForTool("mcp__evil__run"));
        Assert.Equal(PermissionVerdict.None, rules.ForEdit(this.InRepo("src/a.cs")));
        Assert.Equal(PermissionVerdict.Deny, rules.ForCommand("rm -rf bin")); // it can still take permission away
    }

    [Fact]
    public void McpRulesCoverAServerOrOneTool()
    {
        PermissionRules rules = this.Load("{}", localJson: """{"permissions":{"allow":["mcp__github"],"deny":["mcp__db__drop_table"],"ask":["mcp__fs__*"]}}""");
        Assert.Equal(PermissionVerdict.Allow, rules.ForTool("mcp__github__create_issue"));
        Assert.Equal(PermissionVerdict.Deny, rules.ForTool("mcp__db__drop_table"));
        Assert.Equal(PermissionVerdict.None, rules.ForTool("mcp__db__query"));
        Assert.Equal(PermissionVerdict.Ask, rules.ForTool("mcp__fs__write"));
        Assert.Equal(PermissionVerdict.None, rules.ForTool("mcp__githubber__x"));
    }

    [Fact]
    public void WebFetchDomainDenialsAreCollected()
    {
        PermissionRules rules = this.Load("""{"permissions":{"deny":["WebFetch(domain:example.com)","WebFetch(domain:evil.test)","WebFetch(domain:example.com)"]}}""");
        Assert.Equal(["example.com", "evil.test"], rules.DeniedWebDomains());
        Assert.Equal(PermissionVerdict.None, rules.ForTool("WebFetch")); // only some domains are denied
    }

    [Fact]
    public void BrokenSettingsAreIgnored()
    {
        PermissionRules rules = this.Load("{ not json");
        Assert.Equal(0, rules.Count);
    }
}
