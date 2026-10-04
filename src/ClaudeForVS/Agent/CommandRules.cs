using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeForVS.Agent;

/// <summary>
/// Commands the user chose to always allow in a repository, e.g. "dotnet build" or "git commit", saved to
/// %LOCALAPPDATA%\ClaudeForVS\allowed-commands.json. A rule is a command's leading words; it matches that command
/// with any arguments, but never a shell command line that chains, pipes, redirects or substitutes.
/// </summary>
internal static class CommandRules
{
    private const int MaxRuleWords = 3;

    private static readonly string RulesFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "allowed-commands.json");

    // Anything that could run a second command or write somewhere unexpected in cmd, PowerShell or bash.
    private static readonly char[] ShellMetacharacters = ['&', '|', ';', '<', '>', '`', '$', '\n', '\r', '(', ')', '{', '}'];

    // Programs that run whatever command line or script text they're given; no rule can make them safe.
    private static readonly HashSet<string> NeverRuled = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "bash", "sh", "wsl", "iex", "Invoke-Expression", "icm", "Invoke-Command",
        "saps", "start", "Start-Process", "Start-Job", "call", "env", "sudo", "runas", "cscript", "wscript", "mshta",
        "rundll32", "regsvr32",
    };

    // Programs whose name alone would allow anything (any git command, any script); a rule needs a subcommand too,
    // e.g. "git commit" or "dotnet build".
    private static readonly HashSet<string> NeedsSubcommand = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "python", "python3", "py", "node", "deno", "bun", "dotnet", "npx", "pnpx", "bunx", "uv", "uvx",
        "ruby", "perl", "php", "java",
    };

    private static readonly object Gate = new();

    /// <summary>
    /// The rule that would allow this command, e.g. "dotnet build" for "dotnet build src -c Release", or null if it
    /// can't be allowed by a rule.
    /// </summary>
    /// <param name="viaShell">True for shell command lines; false for argument lists that never reach a shell (git).</param>
    public static string? RuleFor(string command, bool viaShell)
    {
        command = command.Trim();
        if (command.Length == 0 || (viaShell && command.IndexOfAny(ShellMetacharacters) >= 0))
        {
            return null;
        }

        var words = new List<string>();
        foreach (string word in command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Stop at the first option, path, quoted or otherwise variable-looking argument.
            if (words.Count == MaxRuleWords || word.StartsWith("-") || word.IndexOfAny(['/', '\\', '"', '\'', '.', '=', ':', '*', '?']) >= 0)
            {
                break;
            }

            words.Add(word);
        }

        return words.Count == 0 || !IsSafe(words) ? null : string.Join(" ", words);
    }

    /// <summary>False for rules that would allow arbitrary code, including ones saved by earlier versions.</summary>
    private static bool IsSafe(IReadOnlyList<string> ruleWords) =>
        !NeverRuled.Contains(ruleWords[0]) && !(ruleWords.Count == 1 && NeedsSubcommand.Contains(ruleWords[0]));

    /// <summary>True if a saved rule for this repository allows the command.</summary>
    public static bool IsAllowed(string repoRoot, string command, bool viaShell)
    {
        if (RuleFor(command, viaShell) is null)
        {
            return false; // chained or redirected command lines always ask
        }

        string normalized = string.Join(" ", command.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return List(repoRoot).Any(rule =>
            (normalized.Equals(rule, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(rule + " ", StringComparison.OrdinalIgnoreCase))
            && rule.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } words
            && IsSafe(words));
    }

    public static IReadOnlyList<string> List(string repoRoot)
    {
        lock (Gate)
        {
            return Load().TryGetValue(Key(repoRoot), out List<string>? rules) ? rules.ToList() : [];
        }
    }

    public static void Allow(string repoRoot, string rule)
    {
        lock (Gate)
        {
            Dictionary<string, List<string>> all = Load();
            if (!all.TryGetValue(Key(repoRoot), out List<string>? rules))
            {
                all[Key(repoRoot)] = rules = [];
            }

            if (!rules.Contains(rule, StringComparer.OrdinalIgnoreCase))
            {
                rules.Add(rule);
                Save(all);
            }
        }
    }

    public static void Remove(string repoRoot, string rule)
    {
        lock (Gate)
        {
            Dictionary<string, List<string>> all = Load();
            if (all.TryGetValue(Key(repoRoot), out List<string>? rules) && rules.RemoveAll(r => r.Equals(rule, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save(all);
            }
        }
    }

    private static string Key(string repoRoot) => repoRoot.Trim().TrimEnd('\\', '/').ToLowerInvariant();

    private static Dictionary<string, List<string>> Load()
    {
        try
        {
            if (File.Exists(RulesFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(RulesFile)) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return [];
    }

    private static void Save(Dictionary<string, List<string>> all)
    {
        try
        {
            AtomicFile.WriteAllText(RulesFile,JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
