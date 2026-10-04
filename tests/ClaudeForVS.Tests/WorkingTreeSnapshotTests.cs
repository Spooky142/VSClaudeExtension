using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Git;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>Runs real git against a throwaway repository in the temp folder.</summary>
public sealed class WorkingTreeSnapshotTests : IDisposable
{
    private readonly string repo = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    public WorkingTreeSnapshotTests()
    {
        Directory.CreateDirectory(this.repo);
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(this.repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git makes its objects read-only
            }

            Directory.Delete(this.repo, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void NameStatusOutputIsParsed()
    {
        var changes = WorkingTreeSnapshot.ParseNameStatus("M\0a.txt\0A\0dir/c.txt\0D\0b.txt\0T\0link\0");
        Assert.Equal(
            [new SnapshotChange('M', "a.txt"), new SnapshotChange('A', "dir/c.txt"), new SnapshotChange('D', "b.txt"), new SnapshotChange('M', "link")],
            changes);
    }

    [Fact]
    public async Task ChangesSinceASnapshotCanBeListedAndUndoneAsync()
    {
        Assert.NotNull(GitRunner.GitPath);
        await this.GitAsync("init");
        this.Write("a.txt", "one");
        this.Write("b.txt", "keep");
        this.Write(".gitignore", "ignored.txt");
        await this.GitAsync("add", "a.txt");

        string? before = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);
        Assert.NotNull(before);

        this.Write("a.txt", "two");
        this.Write("dir/c.txt", "new");
        this.Write("ignored.txt", "never snapshotted");
        File.Delete(Path.Combine(this.repo, "b.txt"));

        string? after = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);
        var changes = await WorkingTreeSnapshot.ChangesAsync(this.repo, before!, after!, CancellationToken.None);
        Assert.Equal(
            [new SnapshotChange('M', "a.txt"), new SnapshotChange('D', "b.txt"), new SnapshotChange('A', "dir/c.txt")],
            changes.OrderBy(c => c.Path));

        Assert.Null(await WorkingTreeSnapshot.RestoreAsync(this.repo, before!, changes, CancellationToken.None));
        Assert.Equal("one", this.Read("a.txt"));
        Assert.Equal("keep", this.Read("b.txt"));
        Assert.False(File.Exists(Path.Combine(this.repo, "dir", "c.txt")));
        Assert.Equal("never snapshotted", this.Read("ignored.txt"));

        // The real index still only has what the user staged.
        (_, string staged) = await GitRunner.RunAsync(this.repo, ["diff", "--cached", "--name-only"], CancellationToken.None, includeStderr: false);
        Assert.Equal("a.txt", staged.Trim());
    }

    [Fact]
    public async Task AnUnchangedTreeGivesTheSameSnapshotAsync()
    {
        await this.GitAsync("init");
        this.Write("a.txt", "one");
        string? first = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);
        string? second = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);
        Assert.Equal(first, second);
        Assert.Empty(await WorkingTreeSnapshot.ChangesAsync(this.repo, first!, second!, CancellationToken.None));
    }

    [Fact]
    public void NumstatOutputIsParsed()
    {
        var counts = WorkingTreeSnapshot.ParseNumstat("3\t1\ta.txt\0-\t-\timg.png\0" + "0\t5\tdir/with space.txt\0");
        Assert.Equal((3, 1, false), counts["a.txt"]);
        Assert.Equal((0, 0, true), counts["img.png"]);
        Assert.Equal((0, 5, false), counts["dir/with space.txt"]);
    }

    [Fact]
    public async Task StatsCountLinesAndOldTextCanBeReadBackAsync()
    {
        await this.GitAsync("init");
        this.Write("a.txt", "one\ntwo\nthree\n");
        this.Write("gone.txt", "x\ny\n");
        string? before = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);

        this.Write("a.txt", "one\n2\nthree\nfour\n");
        this.Write("new.txt", "hello\n");
        File.Delete(Path.Combine(this.repo, "gone.txt"));
        string? after = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);

        var stats = await WorkingTreeSnapshot.StatsAsync(this.repo, before!, after!, CancellationToken.None);
        Assert.Equal(
            [new FileStat('M', "a.txt", 2, 1, false), new FileStat('D', "gone.txt", 0, 2, false), new FileStat('A', "new.txt", 1, 0, false)],
            stats.OrderBy(s => s.Path));

        Assert.Equal("x\ny\n", (await WorkingTreeSnapshot.ReadFileAsync(this.repo, before!, "gone.txt", CancellationToken.None))?.Replace("\r\n", "\n"));
        Assert.Null(await WorkingTreeSnapshot.ReadFileAsync(this.repo, before!, "new.txt", CancellationToken.None));
    }

    private void Write(string path, string text)
    {
        string full = Path.Combine(this.repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(this.repo, path));

    private async Task GitAsync(params string[] args)
    {
        (int code, string output) = await GitRunner.RunAsync(this.repo, args, CancellationToken.None);
        Assert.True(code == 0, output);
    }
}
