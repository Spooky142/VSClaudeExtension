using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClaudeForVS.Agent;

/// <summary>
/// The CLAUDE.md files Claude Code reads, so the Claude API engine follows the same instructions: the user's own
/// (~/.claude/CLAUDE.md), then the repository's (CLAUDE.md, .claude/CLAUDE.md and the uncommitted CLAUDE.local.md).
/// </summary>
internal static class ProjectInstructions
{
    private const int MaxCharsPerFile = 40_000;

    /// <summary>The files' contents, ready to append to the system prompt (empty if there are none), and which files they came from.</summary>
    /// <param name="userDirectory">The user's profile folder; null for the current user's.</param>
    public static (string Text, IReadOnlyList<string> Files) Load(string repoRoot, string? userDirectory = null)
    {
        var loaded = new List<string>();
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = new List<(string Path, string Kind)>
        {
            (Path.Combine(userDirectory, ".claude", "CLAUDE.md"), "the user's instructions for all projects"),
            (Path.Combine(repoRoot, "CLAUDE.md"), "project instructions, checked into the repository"),
            (Path.Combine(repoRoot, ".claude", "CLAUDE.md"), "project instructions, checked into the repository"),
            (Path.Combine(repoRoot, "CLAUDE.local.md"), "the user's private instructions for this project"),
        };

        var sb = new StringBuilder();
        foreach ((string path, string kind) in files)
        {
            string text;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                text = File.ReadAllText(path).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length > MaxCharsPerFile)
            {
                text = text.Substring(0, MaxCharsPerFile) + "\n… (truncated)";
            }

            sb.Append($"\n\nContents of {path} ({kind}):\n\n{text}");
            loaded.Add(path);
        }

        return sb.Length == 0 ? (string.Empty, loaded)
            : ("Instructions from CLAUDE.md files are shown below. Follow them; they override the defaults above." + sb, loaded);
    }
}
