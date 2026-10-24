using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeForVS.Agent;

/// <summary>A run of lines where a file differs from how it was before the chat changed it.</summary>
/// <param name="OldStart">The first line (0-based) in the file as it was.</param>
/// <param name="NewStart">The first line (0-based) in the file as it is; for lines that were only removed, the line they were before.</param>
/// <param name="OldText">The lines as they were, with \n line endings and no final line break.</param>
internal sealed record ChangedRegion(int OldStart, int OldCount, int NewStart, int NewCount, string OldText)
{
    /// <summary>True if lines were only removed here, so there's nothing in the file now to mark but the place.</summary>
    public bool IsRemoval => this.NewCount == 0;
}

/// <summary>
/// The files Claude changed in each chat, as they were before it first changed them, for the editor's margin to mark
/// and undo Claude's changes in. Shared by every chat window; each chat's files are forgotten when it's cleared.
/// </summary>
internal static class ChatChanges
{
    private sealed record Entry(object Chat, string Before, string ClaudeAfter);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on whichever thread recorded it) with the full path of a file whose tracked changes changed.</summary>
    public static event EventHandler<string>? Changed;

    /// <summary>Remembers a change Claude made. The file's first change in a chat keeps the text it started from.</summary>
    public static void Record(object chat, FileChange change)
    {
        lock (Gate)
        {
            Files[change.FullPath] = Files.TryGetValue(change.FullPath, out Entry? entry)
                ? entry with { ClaudeAfter = change.After }
                : new Entry(chat, change.Before, change.After);
        }

        Changed?.Invoke(null, change.FullPath);
    }

    /// <summary>Forgets the files a chat changed, e.g. when it's cleared or another chat is opened in its place.</summary>
    public static void Forget(object chat)
    {
        List<string> forgotten;
        lock (Gate)
        {
            forgotten = Files.Where(f => ReferenceEquals(f.Value.Chat, chat)).Select(f => f.Key).ToList();
            foreach (string path in forgotten)
            {
                Files.Remove(path);
            }
        }

        foreach (string path in forgotten)
        {
            Changed?.Invoke(null, path);
        }
    }

    /// <summary>Whether a chat has changed the file.</summary>
    public static bool IsTracked(string fullPath)
    {
        lock (Gate)
        {
            return Files.ContainsKey(fullPath);
        }
    }

    /// <summary>The file as it was before the chat changed it; null if no chat has.</summary>
    public static string? Before(string fullPath)
    {
        lock (Gate)
        {
            return Files.TryGetValue(fullPath, out Entry? entry) ? entry.Before : null;
        }
    }

    /// <summary>Claude's changes in a file whose text is now <paramref name="now"/>; empty if no chat changed it.</summary>
    public static IReadOnlyList<ChangedRegion> RegionsIn(string fullPath, string now)
    {
        Entry? entry;
        lock (Gate)
        {
            Files.TryGetValue(fullPath, out entry);
        }

        return entry is null ? [] : Regions(entry.Before, now, entry.ClaudeAfter);
    }

    /// <summary>
    /// Where <paramref name="now"/> differs from <paramref name="before"/>, keeping only the places Claude changed
    /// (where <paramref name="claudeAfter"/>, its last version, differs from <paramref name="before"/> too), so the
    /// user's own edits elsewhere in the file aren't marked. Line endings don't count as changes.
    /// </summary>
    internal static IReadOnlyList<ChangedRegion> Regions(string before, string now, string? claudeAfter = null)
    {
        string[] old = Lines(before);
        List<ChangedRegion> regions = Compare(old, Lines(now));
        if (claudeAfter is null)
        {
            return regions;
        }

        List<ChangedRegion> claude = Compare(old, Lines(claudeAfter));
        return regions.Where(r => claude.Any(c => r.OldStart <= c.OldStart + c.OldCount && c.OldStart <= r.OldStart + r.OldCount)).ToList();
    }

    /// <summary>
    /// The edit that puts one region of <paramref name="now"/> back as it was: replace <c>Length</c> characters at
    /// <c>Start</c> with <c>Text</c>, in the file's own line endings.
    /// </summary>
    internal static (int Start, int Length, string Text) RevertEdit(string now, ChangedRegion region)
    {
        string newline = now.Contains("\r\n") ? "\r\n" : "\n";
        List<int> starts = LineStarts(now);
        int lineCount = Lines(now).Length;
        string old = region.OldText.Replace("\n", newline);

        if (region.IsRemoval)
        {
            // Lines that were removed go back in before the line that's there now, or after the last line.
            if (region.NewStart < lineCount)
            {
                return (starts[region.NewStart], 0, old + newline);
            }

            bool endsWithBreak = now.Length == 0 || now.EndsWith("\n", StringComparison.Ordinal);
            return (now.Length, 0, (endsWithBreak ? string.Empty : newline) + old + (endsWithBreak && now.Length > 0 ? newline : string.Empty));
        }

        int start = starts[region.NewStart];
        int lastLine = region.NewStart + region.NewCount - 1;
        bool lastHasBreak = lastLine + 1 < starts.Count;
        int end = lastHasBreak ? starts[lastLine + 1] : now.Length;
        if (region.OldCount == 0)
        {
            // Lines Claude added: take them out with their line breaks. At the very end, the break before them goes too.
            if (!lastHasBreak && start > 0)
            {
                int breakLength = start >= 2 && now[start - 2] == '\r' ? 2 : 1;
                return (start - breakLength, end - start + breakLength, string.Empty);
            }

            return (start, end - start, string.Empty);
        }

        return (start, end - start, old + (lastHasBreak ? newline : string.Empty));
    }

    private static List<ChangedRegion> Compare(string[] old, string[] now)
    {
        List<(char Op, string Text)> ops = UnifiedDiff.Diff(old, now);
        var regions = new List<ChangedRegion>();
        int o = 0, n = 0;
        for (int i = 0; i < ops.Count;)
        {
            if (ops[i].Op == ' ')
            {
                o++;
                n++;
                i++;
                continue;
            }

            int oldStart = o, newStart = n;
            for (; i < ops.Count && ops[i].Op != ' '; i++)
            {
                if (ops[i].Op == '-')
                {
                    o++;
                }
                else
                {
                    n++;
                }
            }

            regions.Add(new ChangedRegion(oldStart, o - oldStart, newStart, n - newStart, string.Join("\n", old.Skip(oldStart).Take(o - oldStart))));
        }

        return regions;
    }

    /// <summary>The lines of a text without their endings; a final line break doesn't start another line.</summary>
    private static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        string lf = text.Replace("\r\n", "\n");
        return (lf.EndsWith("\n", StringComparison.Ordinal) ? lf.Substring(0, lf.Length - 1) : lf).Split('\n');
    }

    /// <summary>Where each line of a text starts; one more entry than lines when it ends with a line break.</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }
}
