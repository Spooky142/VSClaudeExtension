using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeForVS.Agent;

/// <summary>Where a skill comes from, as shown in the Skills window.</summary>
internal enum SkillSource
{
    /// <summary>~/.claude/skills: the user's, for every repository.</summary>
    Personal,

    /// <summary>The repository's .claude/skills, checked in.</summary>
    Project,

    /// <summary>A plugin's, named "plugin:skill".</summary>
    Plugin,

    /// <summary>Built into Claude Code, or from somewhere this extension doesn't read.</summary>
    ClaudeCode,
}

/// <summary>One of Claude Code's skills.</summary>
internal sealed record SkillInfo(string Name, string? Description, SkillSource Source);

/// <summary>
/// The skills Claude Code can use in a repository: the SKILL.md files in ~/.claude/skills and the repository's
/// .claude/skills, plus the ones Claude Code listed when it last started there (built-in and plugin skills, which
/// aren't all on disk where this extension can find them).
/// </summary>
internal static class SkillCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers the skills Claude Code's init event listed for a repository.</summary>
    public static void Remember(string repoRoot, IReadOnlyList<string> names) => Reported[Key(repoRoot)] = names;

    /// <summary>True once Claude Code has started in the repository since Visual Studio did.</summary>
    public static bool HasReported(string repoRoot) => Reported.ContainsKey(Key(repoRoot));

    /// <param name="claudeDirectory">Claude Code's folder (~/.claude); null for the current user's.</param>
    /// <param name="reported">The names Claude Code listed; null for the ones remembered for the repository.</param>
    /// <param name="alsoInclude">Names to list even if nothing else mentions them (skills the user turned off).</param>
    public static IReadOnlyList<SkillInfo> List(string repoRoot, string? claudeDirectory = null, IEnumerable<string>? reported = null, IEnumerable<string>? alsoInclude = null)
    {
        claudeDirectory ??= ClaudeDirectory();
        var skills = new Dictionary<string, SkillInfo>(StringComparer.Ordinal);
        foreach (string name in (reported ?? (Reported.TryGetValue(Key(repoRoot), out IReadOnlyList<string>? r) ? r : [])).Concat(alsoInclude ?? []))
        {
            if (name.Trim().Length > 0)
            {
                skills[name] = new SkillInfo(name, null, name.Contains(':') ? SkillSource.Plugin : SkillSource.ClaudeCode);
            }
        }

        // A project skill beats a personal one with the same name, as in Claude Code.
        AddFolder(Path.Combine(claudeDirectory, "skills"), SkillSource.Personal, skills);
        AddFolder(Path.Combine(repoRoot, ".claude", "skills"), SkillSource.Project, skills);
        return skills.Values.OrderBy(s => s.Source).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The name and description from a SKILL.md file's YAML front matter; either is null if missing.</summary>
    internal static (string? Name, string? Description) ParseFrontMatter(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (null, null);
        }

        string? name = null, description = null;
        for (int i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(lines[i][0]))
            {
                continue;
            }

            string key = lines[i].Substring(0, colon).Trim();
            string value = lines[i].Substring(colon + 1).Trim();

            // A folded or literal block ("description: >") carries on in the indented lines below.
            if (value is ">" or "|" or ">-" or "|-")
            {
                var block = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    block.Add(lines[++i].Trim());
                }

                value = string.Join(" ", block.Where(l => l.Length > 0));
            }
            else if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
            {
                value = value.Substring(1, value.Length - 2);
            }

            if (key == "name")
            {
                name = value.Length > 0 ? value : null;
            }
            else if (key == "description")
            {
                description = value.Length > 0 ? value : null;
            }
        }

        return (name, description);
    }

    private static void AddFolder(string folder, SkillSource source, Dictionary<string, SkillInfo> skills)
    {
        string[] dirs;
        try
        {
            dirs = Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string dir in dirs)
        {
            string file = Path.Combine(dir, "SKILL.md");
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                (string? name, string? description) = ParseFrontMatter(File.ReadAllText(file));
                name ??= Path.GetFileName(dir);
                skills[name] = new SkillInfo(name, description, source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ClaudeDirectory() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir
            ? configDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static string Key(string repoRoot) => repoRoot.Trim().TrimEnd('\\', '/');
}
