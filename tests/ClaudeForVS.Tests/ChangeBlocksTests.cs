using System.Linq;
using ClaudeForVS.Agent;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

public class ChangeBlocksTests
{
    private const string Before = "one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\neight\r\nnine\r\nten\r\n";

    // Changes line 2 and line 9, far enough apart to be separate blocks.
    private const string After = "one\r\nTWO\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\neight\r\nNINE\r\nnine-and-a-half\r\nten\r\n";

    [Fact]
    public void SplitsIntoBlocksOfConsecutiveChanges()
    {
        ChangeBlocks blocks = ChangeBlocks.Compute(Before, After)!;
        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks.Lines, l => l.Op == '-' && l.Text == "two" && l.Block == 0);
        Assert.Contains(blocks.Lines, l => l.Op == '+' && l.Text == "TWO" && l.Block == 0);
        Assert.Contains(blocks.Lines, l => l.Op == '+' && l.Text == "nine-and-a-half" && l.Block == 1);
        Assert.All(blocks.Lines.Where(l => l.Op is ' ' or '@'), l => Assert.Equal(-1, l.Block));
        Assert.StartsWith("@@ -1,", blocks.Lines[0].Text);
    }

    [Fact]
    public void KeepingEverythingOrNothingGivesTheOriginalTexts()
    {
        ChangeBlocks blocks = ChangeBlocks.Compute(Before, After)!;
        Assert.Equal(After, blocks.Apply([0, 1]));
        Assert.Equal(Before, blocks.Apply([]));
    }

    [Fact]
    public void KeepingSomeAppliesOnlyThose()
    {
        ChangeBlocks blocks = ChangeBlocks.Compute(Before, After)!;
        Assert.Equal(Before.Replace("two", "TWO"), blocks.Apply([0]));
        Assert.Equal(Before.Replace("nine\r\n", "NINE\r\nnine-and-a-half\r\n"), blocks.Apply([1]));
    }

    [Fact]
    public void KeepsLineEndingsAndAMissingFinalNewline()
    {
        string before = "a\nb\nc\nd\ne\nf\ng\nh";
        string after = "A\nb\nc\nd\ne\nf\ng\nH";
        ChangeBlocks blocks = ChangeBlocks.Compute(before, after)!;
        Assert.Equal(2, blocks.Count);
        Assert.Equal("A\nb\nc\nd\ne\nf\ng\nh", blocks.Apply([0]));
        Assert.Equal("a\nb\nc\nd\ne\nf\ng\nH", blocks.Apply([1]));
    }

    [Fact]
    public void ReviewNeedsAtLeastTwoBlocks()
    {
        Assert.Null(ChangeReview.For(new FileChange("x.txt", "a\nb\n", "a\nB\n"), () => { }));

        int changes = 0;
        ChangeReview review = ChangeReview.For(new FileChange("x.txt", Before, After), () => changes++)!;
        Assert.Equal(2, review.Toggles.Count);
        Assert.Null(review.PartialAfter()); // all kept: the change as proposed

        review.Toggles[1].IsKept = false;
        Assert.Equal(1, changes);
        Assert.Equal(1, review.KeptCount);
        Assert.Equal("1 of 2 changes selected", review.Summary);
        Assert.Equal(Before.Replace("two", "TWO"), review.PartialAfter());

        // Each block's checkbox is on its first line only.
        Assert.Equal(2, review.Lines.Count(l => l.ShowToggle));
    }
}
