using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeForVS.Agent;

/// <summary>One line of a <see cref="ChangeBlocks"/> preview.</summary>
/// <param name="Op">'@' for a hunk header, ' ' for context, '-' removed, '+' added.</param>
/// <param name="Block">The change block the line belongs to, or -1 for headers and context.</param>
internal readonly record struct ReviewLine(char Op, string Text, int Block)
{
    /// <summary>As in <see cref="UnifiedDiff"/>: "@@ … @@", "  context", "- removed", "+ added".</summary>
    public string Display => this.Op == '@' ? this.Text : (this.Op == ' ' ? "  " : this.Op + " ") + this.Text;
}

/// <summary>
/// A file change split into blocks of consecutive changed lines, so the user can keep some and leave the rest out.
/// Lines keep their line endings, so keeping every block gives exactly the new text and keeping none the old.
/// </summary>
internal sealed class ChangeBlocks
{
    // Past this many preview lines the change is too big to review block by block; it's approved whole instead.
    private const int MaxReviewLines = 3000;

    private readonly List<(char Op, string Text)> ops;

    private ChangeBlocks(List<(char Op, string Text)> ops, int count, IReadOnlyList<ReviewLine> lines)
    {
        this.ops = ops;
        this.Count = count;
        this.Lines = lines;
    }

    /// <summary>How many blocks of changes there are.</summary>
    public int Count { get; }

    /// <summary>The preview: hunks with <paramref name="context"/> lines around each block, as in git diff.</summary>
    public IReadOnlyList<ReviewLine> Lines { get; }

    /// <summary>Splits a change into blocks; null if it's too large to review that way.</summary>
    public static ChangeBlocks? Compute(string before, string after, int context = UnifiedDiff.DefaultContext)
    {
        List<(char Op, string Text)> ops = UnifiedDiff.Diff(SplitKeepingEndings(before), SplitKeepingEndings(after));

        // Block number of each changed op.
        var block = new int[ops.Count];
        int count = 0;
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Op == ' ')
            {
                block[i] = -1;
                continue;
            }

            if (i == 0 || ops[i - 1].Op == ' ')
            {
                count++;
            }

            block[i] = count - 1;
        }

        var oldLine = new int[ops.Count];
        var newLine = new int[ops.Count];
        for (int i = 0, o = 1, n = 1; i < ops.Count; i++)
        {
            oldLine[i] = o;
            newLine[i] = n;
            o += ops[i].Op == '+' ? 0 : 1;
            n += ops[i].Op == '-' ? 0 : 1;
        }

        List<int> changes = Enumerable.Range(0, ops.Count).Where(i => ops[i].Op != ' ').ToList();
        var lines = new List<ReviewLine>();
        int c = 0;
        while (c < changes.Count)
        {
            int first = changes[c];
            while (c + 1 < changes.Count && changes[c + 1] - changes[c] <= (2 * context) + 1)
            {
                c++;
            }

            int start = Math.Max(0, first - context);
            int end = Math.Min(ops.Count - 1, changes[c] + context);
            c++;

            int oldCount = 0, newCount = 0;
            for (int i = start; i <= end; i++)
            {
                oldCount += ops[i].Op == '+' ? 0 : 1;
                newCount += ops[i].Op == '-' ? 0 : 1;
            }

            lines.Add(new ReviewLine('@', $"@@ -{oldLine[start] - (oldCount == 0 ? 1 : 0)},{oldCount} +{newLine[start] - (newCount == 0 ? 1 : 0)},{newCount} @@", -1));
            for (int i = start; i <= end; i++)
            {
                lines.Add(new ReviewLine(ops[i].Op, ops[i].Text.TrimEnd('\r', '\n'), block[i]));
            }

            if (lines.Count > MaxReviewLines)
            {
                return null;
            }
        }

        return new ChangeBlocks(ops, count, lines);
    }

    /// <summary>The new text with only the given blocks applied.</summary>
    public string Apply(ICollection<int> kept)
    {
        var sb = new StringBuilder();
        int current = -1;
        for (int i = 0; i < this.ops.Count; i++)
        {
            (char op, string text) = this.ops[i];
            if (op == ' ')
            {
                sb.Append(text);
                continue;
            }

            if (i == 0 || this.ops[i - 1].Op == ' ')
            {
                current++;
            }

            if ((op == '+') == kept.Contains(current))
            {
                sb.Append(text);
            }
        }

        return sb.ToString();
    }

    /// <summary>Lines with their endings ("a\r\n", "b\n", "c"), so the text can be put back together exactly.</summary>
    private static string[] SplitKeepingEndings(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text.Substring(start, i - start + 1));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text.Substring(start));
        }

        return lines.ToArray();
    }
}
