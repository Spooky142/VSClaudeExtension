using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Git;

namespace ClaudeForVS.Agent;

/// <summary>
/// Writes a commit message for a repository's changes (staged ones, or everything uncommitted if nothing is staged),
/// in the style of its recent history, with whichever engine is selected.
/// </summary>
internal static class CommitMessageWriter
{
    private const int MaxDiffChars = 80_000;

    // Summarizing a diff doesn't need the chat's model: Haiku at low effort does it for a fraction of the cost.
    private const ClaudeModel Model = ClaudeModel.Haiku;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static async Task<string> GenerateAsync(AgentSettings settings, string repoRoot, CancellationToken cancellationToken)
    {
        (string diff, bool staged) = await ChangesAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        if (diff.Trim().Length == 0)
        {
            throw new InvalidOperationException("There are no changes to describe.");
        }

        (int _, string log) = await GitRunner.RunAsync(repoRoot, ["log", "-n", "15", "--format=%s"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        string reply = await OneShot.AskAsync(
            settings, repoRoot, Model, BuildPrompt(diff, staged, log, instructions: null), () => ProjectInstructions.Load(repoRoot).Text, timeout.Token).ConfigureAwait(false);
        string message = Clean(reply);
        return message.Length > 0 ? message : throw new InvalidOperationException("Claude didn't return a message.");
    }

    /// <summary>The changes to describe: the staged diff, or if nothing is staged, every uncommitted change plus new files.</summary>
    private static async Task<(string Diff, bool Staged)> ChangesAsync(string repoRoot, CancellationToken cancellationToken)
    {
        (int _, string staged) = await GitRunner.RunAsync(repoRoot, ["diff", "--staged", "--stat", "--patch", "--no-ext-diff"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        if (staged.Trim().Length > 0)
        {
            return (staged, true);
        }

        (int _, string unstaged) = await GitRunner.RunAsync(repoRoot, ["diff", "HEAD", "--stat", "--patch", "--no-ext-diff"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        (int _, string untracked) = await GitRunner.RunAsync(repoRoot, ["ls-files", "--others", "--exclude-standard"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        string files = untracked.Trim();
        return (unstaged + (files.Length > 0 ? "\nNew files (not shown above):\n" + files : string.Empty), false);
    }

    /// <summary>The request: the diff, recent subjects for style, and (for the API) the project's instructions. Internal for tests.</summary>
    internal static string BuildPrompt(string diff, bool staged, string recentSubjects, string? instructions)
    {
        if (diff.Length > MaxDiffChars)
        {
            diff = diff.Substring(0, MaxDiffChars) + "\n[diff truncated]";
        }

        var sb = new StringBuilder();
        sb.Append("Write a git commit message for the ").Append(staged ? "staged changes" : "uncommitted changes").Append(" below.\n\n");
        sb.Append("Match the style of the repository's recent commit subjects (tense, length, punctuation, prefixes) and follow any ");
        sb.Append("commit message conventions in the project instructions, but don't bump versions or run anything. ");
        sb.Append("Summarize what changed in a short subject line, then a blank line, then short bullet points starting with \"- \", ");
        sb.Append("one per change, only as many as the changes need. ");
        sb.Append("Reply with the commit message only: no preamble, no quotes, no code fences and no other Markdown.\n");
        if (recentSubjects.Trim().Length > 0)
        {
            sb.Append("\n<recent commit subjects>\n").Append(recentSubjects.Trim()).Append("\n</recent commit subjects>\n");
        }

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.Append("\n<project instructions>\n").Append(instructions!.Trim()).Append("\n</project instructions>\n");
        }

        sb.Append("\n<diff>\n").Append(diff.TrimEnd()).Append("\n</diff>\n");
        return sb.ToString();
    }

    /// <summary>Strips what models sometimes add anyway: code fences, surrounding quotes, a "Commit message:" label. Internal for tests.</summary>
    internal static string Clean(string reply)
    {
        string text = reply.Replace("\r\n", "\n").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            int closing = text.LastIndexOf("```", StringComparison.Ordinal);
            text = firstNewline >= 0 && closing > firstNewline ? text.Substring(firstNewline + 1, closing - firstNewline - 1).Trim() : text.Trim('`').Trim();
        }

        foreach (string label in new[] { "Commit message:", "commit message:" })
        {
            if (text.StartsWith(label, StringComparison.Ordinal))
            {
                text = text.Substring(label.Length).Trim();
            }
        }

        if (text.Length > 1 && ((text[0] == '"' && text[text.Length - 1] == '"') || (text[0] == '\'' && text[text.Length - 1] == '\'')))
        {
            text = text.Substring(1, text.Length - 2).Trim();
        }

        return text.Replace("\n", Environment.NewLine);
    }
}
