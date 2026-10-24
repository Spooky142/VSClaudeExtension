using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeForVS.Agent;

/// <summary>What a permission rule says about an action. Deny beats ask, which beats allow.</summary>
internal enum PermissionVerdict
{
    /// <summary>No rule mentions it; the mode decides.</summary>
    None,

    /// <summary>Runs without asking (in Coding mode; Plan mode still blocks changes).</summary>
    Allow,

    /// <summary>Always asks, even in Auto mode.</summary>
    Ask,

    /// <summary>Never runs, in any mode.</summary>
    Deny,
}

/// <summary>
/// The <c>permissions</c> rules from Claude Code's settings files, so the Claude API engine honours them too:
/// <c>~/.claude/settings.json</c>, then the repository's <c>.claude/settings.json</c> and <c>.claude/settings.local.json</c>.
/// Rules look like <c>Bash(npm run test:*)</c>, <c>Read(./secrets/**)</c>, <c>Edit(src/**)</c>, <c>WebSearch</c> or
/// <c>mcp__github</c>; see Claude Code's documentation.
/// </summary>
internal sealed class PermissionRules
{
    // Characters that chain, pipe, redirect or substitute commands; an allow rule never covers a command containing them.
    private static readonly char[] ShellMetacharacters = ['&', '|', ';', '<', '>', '`', '$', '\n', '\r', '(', ')', '{', '}'];

    private static readonly string[] EditTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    private readonly List<Rule> rules;
    private readonly string repoRoot;
    private readonly string userDirectory;

    // Path rules are matched against every file glob and grep look at, so their regexes are built once.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> globs = new(StringComparer.Ordinal);

    private PermissionRules(List<Rule> rules, IReadOnlyList<string> files, string repoRoot, string userDirectory)
    {
        this.rules = rules;
        this.Files = files;
        this.repoRoot = repoRoot.TrimEnd('\\', '/');
        this.userDirectory = userDirectory.TrimEnd('\\', '/');
    }

    /// <summary>No rules at all.</summary>
    public static PermissionRules Empty { get; } = new([], [], string.Empty, string.Empty);

    /// <summary>The settings files that had permission rules.</summary>
    public IReadOnlyList<string> Files { get; }

    public int Count => this.rules.Count;

    /// <summary>True if some rule denies reading files, so file listings need filtering.</summary>
    public bool HasReadDenials => this.rules.Any(r => r.Verdict == PermissionVerdict.Deny && r.Tool == "Read");

    /// <param name="userDirectory">The user's profile folder; null for the current user's.</param>
    public static PermissionRules Load(string repoRoot, string? userDirectory = null)
    {
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rules = new List<Rule>();
        var files = new List<string>();
        foreach (string path in SettingsFiles(repoRoot, userDirectory))
        {
            if (ClaudeSettingsFile.Read(path) is not JsonElement root
                || !root.TryGetProperty("permissions", out JsonElement permissions)
                || permissions.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // A cloned repository could otherwise allow itself anything, so its shared settings can only take permission away.
            bool shared = IsSharedRepoSettings(path, repoRoot);
            int before = rules.Count;
            foreach ((string name, PermissionVerdict verdict) in new[] { ("allow", PermissionVerdict.Allow), ("ask", PermissionVerdict.Ask), ("deny", PermissionVerdict.Deny) })
            {
                if (!(shared && verdict == PermissionVerdict.Allow) && permissions.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && Rule.Parse(item.GetString()!, verdict) is Rule rule)
                        {
                            rules.Add(rule);
                        }
                    }
                }
            }

            if (rules.Count > before)
            {
                files.Add(path);
            }
        }

        return new PermissionRules(rules, files, repoRoot, userDirectory);
    }

    /// <summary>The settings files Claude Code reads, lowest precedence first.</summary>
    public static IEnumerable<string> SettingsFiles(string repoRoot, string userDirectory) =>
    [
        Path.Combine(userDirectory, ".claude", "settings.json"),
        Path.Combine(repoRoot, ".claude", "settings.json"),
        Path.Combine(repoRoot, ".claude", "settings.local.json"),
    ];

    /// <summary>
    /// True for the repository's committed .claude/settings.json, which comes with whatever was cloned. Unlike the user's
    /// own settings and settings.local.json (where Claude Code saves the user's "don't ask again" answers), nothing it
    /// says may grant permission: allow rules and MCP server approvals in it are ignored.
    /// </summary>
    public static bool IsSharedRepoSettings(string path, string repoRoot) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(repoRoot, ".claude", "settings.json")), StringComparison.OrdinalIgnoreCase);

    /// <summary>A shell command line (run_command, or a git command written as "git …").</summary>
    public PermissionVerdict ForCommand(string command)
    {
        string normalized = Normalize(command);
        bool chained = normalized.IndexOfAny(ShellMetacharacters) >= 0;

        // Deny and ask rules also catch a matching command anywhere in a chain.
        List<string> parts = Regex.Split(normalized, @"&&|\|\||[;|\n]").Select(Normalize).Where(p => p.Length > 0).ToList();
        parts.Add(normalized);

        return this.Decide(rule => rule.Tool is "Bash" or "PowerShell", (rule, verdict) =>
            rule.Specifier is null
                ? true
                : verdict == PermissionVerdict.Allow
                    ? !chained && MatchesCommand(rule.Specifier, normalized)
                    : parts.Any(p => MatchesCommand(rule.Specifier, p)));
    }

    /// <summary>Reading a file (read_file, or a file glob or grep would show).</summary>
    public PermissionVerdict ForRead(string fullPath) =>
        this.Decide(rule => rule.Tool == "Read", (rule, _) => rule.Specifier is null || this.MatchesPath(rule.Specifier, fullPath));

    /// <summary>Changing a file (edit_file, write_file, delete_file, move_file).</summary>
    public PermissionVerdict ForEdit(string fullPath) =>
        this.Decide(rule => EditTools.Contains(rule.Tool), (rule, _) => rule.Specifier is null || this.MatchesPath(rule.Specifier, fullPath));

    /// <summary>A tool as a whole, e.g. "WebSearch", "WebFetch" or "mcp__server__tool".</summary>
    public PermissionVerdict ForTool(string toolName) =>
        this.Decide(rule => rule.Specifier is null && MatchesToolName(rule.Tool, toolName), (_, _) => true);

    /// <summary>Domains that "WebFetch(domain:…)" deny rules block.</summary>
    public IReadOnlyList<string> DeniedWebDomains() => this.rules
        .Where(r => r.Verdict == PermissionVerdict.Deny && r.Tool == "WebFetch" && r.Specifier?.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) == true)
        .Select(r => r.Specifier!.Substring("domain:".Length).Trim())
        .Where(d => d.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private PermissionVerdict Decide(Func<Rule, bool> applies, Func<Rule, PermissionVerdict, bool> matches)
    {
        foreach (PermissionVerdict verdict in new[] { PermissionVerdict.Deny, PermissionVerdict.Ask, PermissionVerdict.Allow })
        {
            if (this.rules.Any(r => r.Verdict == verdict && applies(r) && matches(r, verdict)))
            {
                return verdict;
            }
        }

        return PermissionVerdict.None;
    }

    private static bool MatchesToolName(string ruleTool, string toolName)
    {
        if (ruleTool.Equals(toolName, StringComparison.Ordinal))
        {
            return true;
        }

        // "mcp__server" and "mcp__server__*" cover every tool from that server.
        if (ruleTool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            string server = ruleTool.EndsWith("__*", StringComparison.Ordinal) ? ruleTool.Substring(0, ruleTool.Length - 3) : ruleTool;
            return toolName.StartsWith(server + "__", StringComparison.Ordinal) && server.IndexOf("__", 5, StringComparison.Ordinal) < 0;
        }

        return false;
    }

    /// <summary>"npm run test:*" is a prefix (at a word boundary); otherwise "*" matches anything and the rest is exact.</summary>
    internal static bool MatchesCommand(string specifier, string command)
    {
        string spec = Normalize(specifier);
        if (spec.EndsWith(":*", StringComparison.Ordinal))
        {
            string prefix = spec.Substring(0, spec.Length - 2).TrimEnd();
            return command.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || command.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase);
        }

        if (!spec.Contains('*'))
        {
            return command.Equals(spec, StringComparison.OrdinalIgnoreCase);
        }

        string pattern = "^" + string.Join(".*", spec.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>
    /// Matches a file against a gitignore-style path rule: "//abs/path", "~/in/home", "/from/repo/root", "./relative" or
    /// "relative"; a pattern without a slash matches the file name (or a folder name) at any depth. A rule naming a
    /// folder covers everything in it.
    /// </summary>
    internal bool MatchesPath(string specifier, string fullPath)
    {
        string spec = specifier.Trim().Replace('\\', '/');
        string path = fullPath.Replace('\\', '/');
        if (spec.Length == 0)
        {
            return false;
        }

        string pattern;
        if (spec.StartsWith("//", StringComparison.Ordinal))
        {
            // "//c/Users/x" or "//c:/Users/x"
            string rest = spec.Substring(2);
            pattern = rest.Length >= 2 && char.IsLetter(rest[0]) && rest[1] == '/' ? rest[0] + ":" + rest.Substring(1) : rest;
        }
        else if (spec.StartsWith("~/", StringComparison.Ordinal))
        {
            pattern = this.userDirectory.Replace('\\', '/') + spec.Substring(1);
        }
        else if (spec.StartsWith("/", StringComparison.Ordinal))
        {
            pattern = this.repoRoot.Replace('\\', '/') + spec;
        }
        else
        {
            string relative = spec.StartsWith("./", StringComparison.Ordinal) ? spec.Substring(2) : spec;
            if (!relative.TrimEnd('/').Contains('/'))
            {
                // A bare name, like ".env" or "*.pem": any file or folder with that name.
                Regex name = this.Glob(relative.TrimEnd('/'));
                return path.Split('/').Any(segment => name.IsMatch(segment));
            }

            pattern = this.repoRoot.Replace('\\', '/') + "/" + relative;
        }

        Regex regex = this.Glob(pattern.TrimEnd('/'));
        for (string? candidate = path; !string.IsNullOrEmpty(candidate); candidate = ParentOf(candidate!))
        {
            if (regex.IsMatch(candidate!))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A rule's glob as a regex; a pattern that isn't a valid glob matches nothing.</summary>
    private Regex Glob(string pattern) => this.globs.GetOrAdd(pattern, p =>
    {
        try
        {
            return RepoSearch.GlobToRegex(p);
        }
        catch (ToolInputException)
        {
            return new Regex("(?!)");
        }
    });

    private static string? ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash > 0 ? path.Substring(0, slash) : null;
    }

    private static string Normalize(string command) => string.Join(" ", command.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private sealed record Rule(string Tool, string? Specifier, PermissionVerdict Verdict)
    {
        public static Rule? Parse(string text, PermissionVerdict verdict)
        {
            text = text.Trim();
            int open = text.IndexOf('(');
            if (open < 0)
            {
                return text.Length == 0 ? null : new Rule(text, null, verdict);
            }

            if (!text.EndsWith(")", StringComparison.Ordinal) || open == 0)
            {
                return null;
            }

            string specifier = text.Substring(open + 1, text.Length - open - 2).Trim();
            return new Rule(text.Substring(0, open).Trim(), specifier.Length == 0 || specifier == "*" ? null : specifier, verdict);
        }
    }
}

/// <summary>Reads Claude Code's JSON settings files, which may contain comments and trailing commas.</summary>
internal static class ClaudeSettingsFile
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The file's root object, or null if it doesn't exist or isn't valid JSON.</summary>
    public static JsonElement? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), Options);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
