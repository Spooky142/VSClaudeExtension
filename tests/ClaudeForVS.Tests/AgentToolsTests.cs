using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class ProjectInstructionsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    public ProjectInstructionsTests()
    {
        Directory.CreateDirectory(Path.Combine(this.root, "repo", ".claude"));
        Directory.CreateDirectory(Path.Combine(this.root, "home", ".claude"));
    }

    private string Repo => Path.Combine(this.root, "repo");

    private string Home => Path.Combine(this.root, "home");

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    [Fact]
    public void NoFilesMeansNoInstructions()
    {
        (string text, var files) = ProjectInstructions.Load(this.Repo, this.Home);
        Assert.Equal(string.Empty, text);
        Assert.Empty(files);
    }

    [Fact]
    public void LoadsUserThenProjectFilesInOrder()
    {
        File.WriteAllText(Path.Combine(this.Home, ".claude", "CLAUDE.md"), "user rule");
        File.WriteAllText(Path.Combine(this.Repo, "CLAUDE.md"), "project rule");
        File.WriteAllText(Path.Combine(this.Repo, ".claude", "CLAUDE.md"), "   ");
        File.WriteAllText(Path.Combine(this.Repo, "CLAUDE.local.md"), "local rule");

        (string text, var files) = ProjectInstructions.Load(this.Repo, this.Home);
        Assert.Equal(3, files.Count);

        int user = text.IndexOf("user rule", StringComparison.Ordinal);
        int project = text.IndexOf("project rule", StringComparison.Ordinal);
        int local = text.IndexOf("local rule", StringComparison.Ordinal);
        Assert.True(user >= 0 && user < project && project < local);
        Assert.DoesNotContain(Path.Combine(this.Repo, ".claude", "CLAUDE.md"), text); // blank files are skipped
    }
}

public class ShellRunnerTests
{
    private static readonly string Temp = Path.GetTempPath();

    [Fact]
    public async Task ReturnsOutputAndExitCode()
    {
        (int? code, string output) = await ShellRunner.RunAsync(Temp, "Write-Output 'héllo'; cmd /c \"echo oops 1>&2\"; exit 3", TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Contains("héllo", output);
        Assert.Contains("oops", output);
    }

    [Fact]
    public async Task FailingLastCommandGivesNonZeroExit()
    {
        (int? code, _) = await ShellRunner.RunAsync(Temp, "cmd /c exit 5", TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(5, code);
    }

    [Fact]
    public async Task SuccessGivesZero()
    {
        (int? code, string output) = await ShellRunner.RunAsync(Temp, "Write-Output ok", TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal("ok", output);
    }

    [Fact]
    public async Task TimesOut()
    {
        (int? code, _) = await ShellRunner.RunAsync(Temp, "Start-Sleep -Seconds 30", TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Null(code);
    }
}
