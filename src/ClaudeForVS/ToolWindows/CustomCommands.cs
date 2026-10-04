using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeForVS.ToolWindows;

/// <summary>A prompt saved as a Markdown file in a <c>.claude/commands</c> folder, invoked as <c>/name</c>.</summary>
/// <param name="Name">"/" and the file name without .md.</param>
/// <param name="Description">From the frontmatter, or the body's first line.</param>
/// <param name="ArgumentHint">From the frontmatter's <c>argument-hint</c>, e.g. "[issue number]"; may be empty.</param>
/// <param name="Body">The prompt, without its frontmatter.</param>
internal sealed record CustomCommand(string Name, string Description, string ArgumentHint, string Body)
{
    private static readonly Regex Positional = new(@"\$([1-9])", RegexOptions.Compiled);

    public bool TakesArguments => this.ArgumentHint.Length > 0 || this.Body.Contains("$ARGUMENTS") || Positional.IsMatch(this.Body);

    /// <summary>
    /// The prompt with its arguments filled in, as Claude Code does: <c>$ARGUMENTS</c> is everything typed after the
    /// name and <c>$1</c>-<c>$9</c> are its words. Arguments a command doesn't use are appended.
    /// </summary>
    public string Expand(string arguments)
    {
        if (!this.Body.Contains("$ARGUMENTS") && !Positional.IsMatch(this.Body))
        {
            return arguments.Length == 0 ? this.Body : $"{this.Body}\n\nARGUMENTS: {arguments}";
        }

        string[] words = SplitWords(arguments);
        string text = this.Body.Replace("$ARGUMENTS", arguments);
        return Positional.Replace(text, m =>
        {
            int i = m.Groups[1].Value[0] - '0';
            return i <= words.Length ? words[i - 1] : string.Empty;
        });
    }

    /// <summary>Splits arguments on spaces, keeping "quoted phrases" together.</summary>
    private static string[] SplitWords(string arguments) =>
        Regex.Matches(arguments, "\"([^\"]*)\"|(\\S+)").Cast<Match>()
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToArray();
}

/// <summary>
/// Reads custom commands the way Claude Code does: <c>.claude/commands</c> in the repository, then
/// <c>~/.claude/commands</c>; subfolders only organize them. A project command wins over a personal one of the same name.
/// </summary>
internal static class CustomCommands
{
    private const int MaxFileChars = 100_000;

    /// <param name="userDirectory">The user's profile folder; null for the current user's.</param>
    public static IReadOnlyList<CustomCommand> Load(string? repoRoot, string? userDirectory = null)
    {
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var commands = new List<CustomCommand>();
        foreach ((string? root, string scope) in new[] { (repoRoot, "project"), (userDirectory, "user") })
        {
            if (root is null)
            {
                continue;
            }

            string folder = Path.Combine(root, ".claude", "commands");
            foreach (string file in Files(folder))
            {
                string name = "/" + Path.GetFileNameWithoutExtension(file);
                if (name.Contains(' ') || commands.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                string sub = Path.GetDirectoryName(file)!.Length > folder.Length ? Path.GetDirectoryName(file)!.Substring(folder.Length + 1).Replace('\\', '/') : string.Empty;
                commands.Add(Parse(name, text.Length > MaxFileChars ? text.Substring(0, MaxFileChars) : text, sub.Length > 0 ? $"{scope}:{sub}" : scope));
            }
        }

        return commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Splits a command file into its frontmatter fields and body.</summary>
    internal static CustomCommand Parse(string name, string text, string scope)
    {
        string body = text.Replace("\r\n", "\n");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body.StartsWith("---\n"))
        {
            int end = body.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (string line in body.Substring(4, Math.Max(0, end - 4)).Split('\n'))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        fields[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim().Trim('"', '\'');
                    }
                }

                int bodyStart = body.IndexOf('\n', end + 1);
                body = bodyStart < 0 ? string.Empty : body.Substring(bodyStart + 1);
            }
        }

        body = body.Trim();
        string description = fields.TryGetValue("description", out string? d) && d.Length > 0
            ? d
            : body.Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        if (description.Length > 80)
        {
            description = description.Substring(0, 80) + "…";
        }

        return new CustomCommand(name, $"{description} ({scope})".Trim(), fields.TryGetValue("argument-hint", out string? hint) ? hint : string.Empty, body);
    }

    private static IEnumerable<string> Files(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
