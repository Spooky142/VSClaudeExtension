using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public async Task StoppingReturnsTheOutputSoFarAndReportsLinesAsTheyArrive()
    {
        using var stop = new CancellationTokenSource();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        (int? code, string output) = await ShellRunner.RunAsync(
            Temp,
            "Write-Output started; Start-Sleep -Seconds 30",
            TimeSpan.FromSeconds(60),
            CancellationToken.None,
            stop.Token,
            line =>
            {
                seen.Enqueue(line);
                stop.Cancel(); // stop as soon as the first line shows
            });

        Assert.Null(code);
        Assert.Equal("started", output);
        Assert.Contains("started", seen);
    }
}

public class UnifiedDiffTests
{
    [Fact]
    public void ShowsAHunkWithContextAroundTheChange()
    {
        string before = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n";
        string after = "1\n2\n3\n4\n5\nsix\n7\n8\n9\n10\n";
        Assert.Equal(
            "@@ -3,7 +3,7 @@\n  3\n  4\n  5\n- 6\n+ six\n  7\n  8\n  9",
            UnifiedDiff.Create(before, after));
    }

    [Fact]
    public void FarApartChangesGetTheirOwnHunks()
    {
        string before = string.Join("\n", Enumerable.Range(1, 30));
        string after = before.Replace("\n2\n", "\ntwo\n").Replace("\n28\n", "\n28\nnew\n");
        string diff = UnifiedDiff.Create(before, after);
        Assert.Equal(2, diff.Split('\n').Count(l => l.StartsWith("@@")));
        Assert.Contains("- 2\n+ two", diff);
        Assert.Contains("@@ -26,5 +26,6 @@", diff);
        Assert.Contains("+ new", diff);
    }

    [Fact]
    public void LineEndingsDontCountAsChanges()
    {
        Assert.Equal("(no changes)", UnifiedDiff.Create("a\r\nb\r\n", "a\nb\n"));
        Assert.Equal("@@ -0,0 +1,1 @@\n+ new", UnifiedDiff.Create(string.Empty, "new"));
    }
}
