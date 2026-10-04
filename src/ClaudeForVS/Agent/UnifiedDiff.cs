using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeForVS.Agent;

/// <summary>
/// Line diffs in the style of <c>git diff</c> for edit previews: "@@ -12,7 +12,8 @@" hunks with a few lines of context,
/// unchanged lines prefixed "  ", removed "- " and added "+ ".
/// </summary>
internal static class UnifiedDiff
{
    public const int DefaultContext = 3;

    // Past this many lines the preview stops; the whole change can still be opened in the diff viewer.
    private const int MaxOutputLines = 400;

    // Largest table worth filling to line up the changed middle of the files (edits change a small part, so it's
    // usually tiny); beyond it, that part is shown as all removed and then all added.
    private const long MaxTableCells = 1_000_000;

    public static string Create(string before, string after, int context = DefaultContext)
    {
        List<(char Op, string Text)> ops = Diff(Lines(before), Lines(after));
        List<int> changes = Enumerable.Range(0, ops.Count).Where(i => ops[i].Op != ' ').ToList();
        if (changes.Count == 0)
        {
            return "(no changes)";
        }

        // Line numbers in the old and new file at each op (1-based).
        var oldLine = new int[ops.Count];
        var newLine = new int[ops.Count];
        for (int i = 0, o = 1, n = 1; i < ops.Count; i++)
        {
            oldLine[i] = o;
            newLine[i] = n;
            o += ops[i].Op == '+' ? 0 : 1;
            n += ops[i].Op == '-' ? 0 : 1;
        }

        var lines = new List<string>();
        int c = 0;
        while (c < changes.Count)
        {
            // A hunk runs until the next change is too far away to share context with it.
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

            // As in git, an empty side is numbered by the line before it.
            lines.Add($"@@ -{oldLine[start] - (oldCount == 0 ? 1 : 0)},{oldCount} +{newLine[start] - (newCount == 0 ? 1 : 0)},{newCount} @@");
            for (int i = start; i <= end; i++)
            {
                lines.Add((ops[i].Op == ' ' ? "  " : ops[i].Op + " ") + ops[i].Text);
            }
        }

        var sb = new StringBuilder();
        foreach (string line in lines.Take(MaxOutputLines))
        {
            sb.Append(line).Append('\n');
        }

        if (lines.Count > MaxOutputLines)
        {
            sb.Append($"… ({lines.Count - MaxOutputLines} more lines; open the diff to see all of it)\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        string lf = text.Replace("\r\n", "\n");
        return (lf.EndsWith("\n", StringComparison.Ordinal) ? lf.Substring(0, lf.Length - 1) : lf).Split('\n');
    }

    /// <summary>The lines of both files as unchanged (' '), removed ('-') or added ('+'), in order.</summary>
    internal static List<(char Op, string Text)> Diff(string[] a, string[] b)
    {
        // Edits are local, so match the common start and end directly and only line up what's between.
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        var ops = new List<(char, string)>();
        ops.AddRange(a.Take(prefix).Select(l => (' ', l)));

        string[] oldMiddle = a.Skip(prefix).Take(a.Length - prefix - suffix).ToArray();
        string[] newMiddle = b.Skip(prefix).Take(b.Length - prefix - suffix).ToArray();
        if ((long)oldMiddle.Length * newMiddle.Length > MaxTableCells)
        {
            ops.AddRange(oldMiddle.Select(l => ('-', l)));
            ops.AddRange(newMiddle.Select(l => ('+', l)));
        }
        else
        {
            ops.AddRange(LongestCommonSubsequence(oldMiddle, newMiddle));
        }

        ops.AddRange(a.Skip(a.Length - suffix).Select(l => (' ', l)));
        return ops;
    }

    private static IEnumerable<(char, string)> LongestCommonSubsequence(string[] a, string[] b)
    {
        // lengths[i, j]: longest common subsequence of a[i..] and b[j..].
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
        {
            for (int j = b.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = a[i] == b[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                yield return (' ', a[x]);
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                yield return ('-', a[x++]);
            }
            else
            {
                yield return ('+', b[y++]);
            }
        }

        while (x < a.Length)
        {
            yield return ('-', a[x++]);
        }

        while (y < b.Length)
        {
            yield return ('+', b[y++]);
        }
    }
}
