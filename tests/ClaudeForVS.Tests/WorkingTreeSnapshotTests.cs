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
    public async Task LargeUntrackedFilesAreLeftOutOfSnapshotsAsync()
    {
        await this.GitAsync("init");
        this.Write("small.txt", "hello");
        this.Write("big [1].bin", new string('x', (int)WorkingTreeSnapshot.MaxUntrackedFileBytes + 1)); // brackets: matched literally
        string? tree = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);
        Assert.NotNull(tree);

        (_, string listed) = await GitRunner.RunAsync(this.repo, ["ls-tree", "-r", "--name-only", tree!], CancellationToken.None, includeStderr: false);
        Assert.Equal(["small.txt"], listed.Split(['\n'], StringSplitOptions.RemoveEmptyEntries));
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

    [Fact]
    public async Task RestoringMoreFilesThanACommandLineHoldsWorksAsync()
    {
        await this.GitAsync("init");

        // About 1,200 paths of 60-odd characters: well past Windows' 32K command line.
        string[] paths = [.. Enumerable.Range(0, 1200).Select(i => $"some/fairly/deep/folder/a-file-with-a-longish-name-{i:D4}.txt"), "odd [1] name.txt"]; // brackets: matched literally
        foreach (string path in paths)
        {
            this.Write(path, "before");
        }

        string? before = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);
        foreach (string path in paths)
        {
            this.Write(path, "after");
        }

        string? after = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);
        var changes = await WorkingTreeSnapshot.ChangesAsync(this.repo, before!, after!, CancellationToken.None);
        Assert.Equal(paths.Length, changes.Count);

        Assert.Null(await WorkingTreeSnapshot.RestoreAsync(this.repo, before!, changes, CancellationToken.None));
        Assert.All(paths, path => Assert.Equal("before", this.Read(path)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Path.GetTempPath(), "ClaudeForVS"), "restore-*.pathspec"));
    }

    [Fact]
    public async Task HugeOutputIsCutShortWhenShownButKeptWholeWhenParsedAsync()
    {
        await this.GitAsync("init");
        string big = string.Concat(Enumerable.Range(0, 40_000).Select(i => $"line {i:D6} of a big file\n")); // about 1 MB
        this.Write("big.txt", big);
        string? tree = await WorkingTreeSnapshot.CaptureAsync(this.repo, CancellationToken.None);

        (int code, string shown) = await GitRunner.RunAsync(this.repo, ["show", $"{tree}:big.txt"], CancellationToken.None);
        Assert.Equal(0, code);
        Assert.True(shown.Length < 31_000, $"{shown.Length} characters");
        Assert.Contains("[output truncated: at least", shown);

        (code, string parsed) = await GitRunner.RunAsync(this.repo, ["show", $"{tree}:big.txt"], CancellationToken.None, includeStderr: false);
        Assert.Equal(0, code);
        Assert.Equal(big.Length, parsed.Replace("\r\n", "\n").Length);
    }

    [Fact]
    public async Task RepositoryFilesLeaveOutThoseDeletedFromDiskAsync()
    {
        await this.GitAsync("init");
        this.Write("a.txt", "a");
        this.Write("b.txt", "b");
        await this.GitAsync("add", "a.txt", "b.txt");
        File.Delete(Path.Combine(this.repo, "b.txt"));
        this.Write("c.txt", "c");

        Assert.Equal(["a.txt", "c.txt"], await ClaudeForVS.Agent.RepoSearch.ListFilesAsync(this.repo, CancellationToken.None));
    }

    [Fact]
    public async Task ShownOutputIsReadInChunksAndCappedAsync()
    {
        int calls = 0;
        (string text, long total) = await GitRunner.ReadCappedAsync(new StringReader(new string('x', 200_000)), 30_000, () => calls++);
        Assert.Equal(30_000, text.Length);
        Assert.Equal(200_000, total);
        Assert.True(calls > 0);

        calls = 0;
        (text, total) = await GitRunner.ReadCappedAsync(new StringReader(new string('x', 200_000)), int.MaxValue, () => calls++);
        Assert.Equal(200_000, text.Length);
        Assert.Equal(0, calls); // parsed output is never stopped early
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
