using System.Collections.Generic;
using System.Linq;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>The margin's marks for Claude's changes, and editing in place (Ctrl+Alt+/).</summary>
public sealed class EditorFeatureTests
{
    private const string Before = "one\ntwo\nthree\nfour\nfive\n";

    // ===== Margin marks =====

    [Fact]
    public void FindsChangedAddedAndRemovedLines()
    {
        IReadOnlyList<ChangedRegion> regions = ChatChanges.Regions(Before, "one\nTWO\nthree\nnew\nfour\n");

        Assert.Equal(
            [new ChangedRegion(1, 1, 1, 1, "two"), new ChangedRegion(3, 0, 3, 1, string.Empty), new ChangedRegion(4, 1, 5, 0, "five")],
            regions);
        Assert.True(regions[2].IsRemoval);
    }

    [Fact]
    public void OnlyMarksThePlacesClaudeChanged()
    {
        const string Claude = "one\nTWO\nthree\nfour\nfive\n";
        const string ThenTheUser = "one\nTWO\nthree\nfour\nFIVE\n"; // the user edited line 5 afterwards

        IReadOnlyList<ChangedRegion> regions = ChatChanges.Regions(Before, ThenTheUser, Claude);

        ChangedRegion region = Assert.Single(regions);
        Assert.Equal(1, region.NewStart);
    }

    [Fact]
    public void LineEndingsAreNotChanges()
    {
        Assert.Empty(ChatChanges.Regions(Before, Before.Replace("\n", "\r\n")));
    }

    [Theory]
    [InlineData("one\nTWO\nthree\nfour\nfive\n")] // a changed line
    [InlineData("one\ntwo\nthree\nfour\nFIVE")] // the last line, without a final line break
    [InlineData("one\ntwo\nadded\nthree\nfour\nfive\n")] // an added line
    [InlineData("one\ntwo\nthree\nfour\nfive\nadded")] // added at the very end
    [InlineData("one\nthree\nfour\nfive\n")] // a removed line
    [InlineData("one\ntwo\nthree\nfour\n")] // the last line removed
    [InlineData("")] // everything removed
    public void UndoingEveryRegionGivesBackTheOriginal(string now)
    {
        string text = now;
        while (ChatChanges.Regions(Before, text) is { Count: > 0 } regions)
        {
            (int start, int length, string replacement) = ChatChanges.RevertEdit(text, regions[regions.Count - 1]);
            text = text.Remove(start, length).Insert(start, replacement);
        }

        Assert.Equal(Before.TrimEnd('\n'), text.TrimEnd('\n'));
    }

    [Fact]
    public void UndoKeepsTheFilesLineEndings()
    {
        string now = "one\r\nTWO\r\nthree\r\n";
        ChangedRegion region = Assert.Single(ChatChanges.Regions("one\ntwo\nthree\n", now));

        (int start, int length, string text) = ChatChanges.RevertEdit(now, region);

        Assert.Equal("one\r\ntwo\r\nthree\r\n", now.Remove(start, length).Insert(start, text));
    }

    [Fact]
    public void RemembersTheFirstVersionAndForgetsWithTheChat()
    {
        object chat = new();
        const string Path = @"C:\work\repo\src\Margin.cs";
        var changed = new List<string>();
        void OnChanged(object? sender, string path) => changed.Add(path);
        ChatChanges.Changed += OnChanged;
        try
        {
            ChatChanges.Record(chat, new FileChange(Path, Before, "one\nTWO\nthree\nfour\nfive\n"));
            ChatChanges.Record(chat, new FileChange(Path, "one\nTWO\nthree\nfour\nfive\n", "one\nTWO\nthree\nFOUR\nfive\n"));

            Assert.Equal(Before, ChatChanges.Before(Path.ToUpperInvariant())); // paths compare as Windows does
            Assert.Equal([1, 3], ChatChanges.RegionsIn(Path, "one\nTWO\nthree\nFOUR\nfive\n").Select(r => r.NewStart));

            ChatChanges.Forget(new object()); // another chat's
            Assert.True(ChatChanges.IsTracked(Path));

            ChatChanges.Forget(chat);
            Assert.False(ChatChanges.IsTracked(Path));
            Assert.Empty(ChatChanges.RegionsIn(Path, "anything"));
            Assert.Equal([Path, Path, Path], changed);
        }
        finally
        {
            ChatChanges.Changed -= OnChanged;
            ChatChanges.Forget(chat);
        }
    }

    // ===== Editing in place =====

    [Fact]
    public void ThePromptMarksTheLinesToRewrite()
    {
        string prompt = InlineEdit.BuildPrompt("src/Order.cs", "a\r\nb\r\nc\r\nd\r\n", 2, 3, "  make it async  ");

        Assert.Contains("<instruction>\nmake it async\n</instruction>", prompt);
        Assert.Contains("<file path=\"src/Order.cs\">\na\n<rewrite>\nb\nc\n</rewrite>\nd\n", prompt);
        Assert.DoesNotContain("\r", prompt);
    }

    [Fact]
    public void ALargeFileIsCutToTheLinesAroundTheEdit()
    {
        string file = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"line {i} {new string('x', 30)}"));

        string prompt = InlineEdit.BuildPrompt("big.cs", file, 1500, 1500, "fix");

        Assert.Contains("lines=\"1350-1650\"", prompt);
        Assert.DoesNotContain("line 1349 ", prompt);
        Assert.Contains("<rewrite>\nline 1500 ", prompt);
    }

    [Theory]
    [InlineData("```csharp\nint x = 1;\nint y = 2;\n```", "int x = 1;\nint y = 2;")]
    [InlineData("Here you go:\n\n```\n    return 1;\n```\nDone.", "    return 1;")]
    [InlineData("int x = 1;\n", "int x = 1;")]
    [InlineData("```\n<rewrite>\nint x;\n</rewrite>\n```", "int x;")]
    [InlineData("````md\nhas ``` inside\n````", "has ``` inside")]
    public void TakesTheCodeFromTheReply(string reply, string code) => Assert.Equal(code, InlineEdit.ExtractCode(reply));

    [Fact]
    public void AnEmptyFencedBlockDeletesTheLines()
    {
        Assert.Equal(string.Empty, InlineEdit.ExtractCode("```\n```"));
        Assert.Null(InlineEdit.ExtractCode("   \n"));
    }

    [Fact]
    public void TheToolServerWaitsLongerThanClaudeCodesDefault()
    {
        Assert.True(VisualStudioToolServer.CallTimeoutMilliseconds >= 60 * 60 * 1000); // Claude Code gives up after 5 minutes otherwise
    }
}
