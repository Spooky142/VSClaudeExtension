using System;
using System.IO;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class CustomCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    private string Repo => Path.Combine(this.root, "repo");

    private string Home => Path.Combine(this.root, "home");

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private void Write(string baseDir, string relative, string text)
    {
        string path = Path.Combine(baseDir, ".claude", "commands", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void FrontmatterGivesDescriptionAndHint()
    {
        CustomCommand command = CustomCommands.Parse("/fix", "---\ndescription: Fix an issue\nargument-hint: \"[issue]\"\n---\nFix issue #$ARGUMENTS.\n", "project");
        Assert.Equal("Fix an issue (project)", command.Description);
        Assert.Equal("[issue]", command.ArgumentHint);
        Assert.Equal("Fix issue #$ARGUMENTS.", command.Body);
        Assert.True(command.TakesArguments);
    }

    [Fact]
    public void WithoutFrontmatterTheFirstLineDescribesIt()
    {
        CustomCommand command = CustomCommands.Parse("/tidy", "# Tidy the code\r\nRemove dead code.", "user");
        Assert.Equal("Tidy the code (user)", command.Description);
        Assert.False(command.TakesArguments);
    }

    [Theory]
    [InlineData("Fix #$ARGUMENTS now", "123 urgent", "Fix #123 urgent now")]
    [InlineData("Move $1 to $2", "a.cs \"new folder/b.cs\"", "Move a.cs to new folder/b.cs")]
    [InlineData("Move $1 to $2", "a.cs", "Move a.cs to ")]
    [InlineData("Tidy up", "", "Tidy up")]
    [InlineData("Tidy up", "src", "Tidy up\n\nARGUMENTS: src")]
    public void ArgumentsAreFilledIn(string body, string arguments, string expected) =>
        Assert.Equal(expected, new CustomCommand("/x", string.Empty, string.Empty, body).Expand(arguments));

    [Fact]
    public void ProjectCommandsWinAndSubfoldersOnlyOrganize()
    {
        this.Write(this.Repo, "review-pr.md", "project version");
        this.Write(this.Home, "review-pr.md", "user version");
        this.Write(this.Home, Path.Combine("git", "squash.md"), "Squash commits");
        this.Write(this.Repo, "not a command.md", "spaces aren't allowed");

        var commands = CustomCommands.Load(this.Repo, this.Home);

        Assert.Equal(["/review-pr", "/squash"], System.Linq.Enumerable.Select(commands, c => c.Name));
        Assert.Equal("project version", commands[0].Body);
        Assert.Equal("Squash commits (user:git)", commands[1].Description);
    }

    [Fact]
    public void ParseCustomMatchesByName()
    {
        CustomCommand[] custom = [new("/Deploy", string.Empty, string.Empty, "Deploy $ARGUMENTS")];
        var parsed = SlashCommands.ParseCustom("/deploy Staging", custom);
        Assert.NotNull(parsed);
        Assert.Equal("Staging", parsed!.Value.Argument);
        Assert.Null(SlashCommands.ParseCustom("/other", custom));
    }
}
