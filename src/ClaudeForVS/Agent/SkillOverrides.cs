using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeForVS.Agent;

/// <summary>How a settings file's skillOverrides entry sets a skill, and whether the user can change it.</summary>
/// <param name="Value">"on", "name-only" (listed without its description), "user-invocable-only" (hidden from Claude) or "off".</param>
/// <param name="LockedBy">Set when managed (policy) settings decide it, so changing settings.local.json would do nothing.</param>
internal sealed record SkillOverride(string Value, string? LockedBy)
{
    /// <summary>Whether Claude can use the skill: everything but "off" and "user-invocable-only".</summary>
    public bool ClaudeCanUse => this.Value is not ("off" or "user-invocable-only");
}

/// <summary>
/// Claude Code's skillOverrides setting, which its /skills command writes: a skill turned off there isn't listed to
/// Claude at all, and can't run. Read from managed settings (which win and can't be changed), then the repository's
/// .claude/settings.local.json, .claude/settings.json and ~/.claude/settings.json; written, as /skills does, to
/// .claude/settings.local.json.
/// </summary>
internal static class SkillOverrides
{
    private static readonly string[] Values = ["on", "name-only", "user-invocable-only", "off"];

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Managed settings, which an administrator sets for the whole machine.</summary>
    public static readonly string ManagedSettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json");

    /// <summary>Every skill some settings file overrides, with the override that applies.</summary>
    /// <param name="userDirectory">The user's profile folder; null for the current user's.</param>
    /// <param name="managedFile">The managed settings file; null for the machine's.</param>
    public static IReadOnlyDictionary<string, SkillOverride> Load(string repoRoot, string? userDirectory = null, string? managedFile = null)
    {
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new Dictionary<string, SkillOverride>(StringComparer.Ordinal);

        // Lowest precedence first, so later files replace earlier entries.
        foreach (string file in PermissionRules.SettingsFiles(repoRoot, userDirectory))
        {
            foreach (KeyValuePair<string, string> entry in Read(file))
            {
                result[entry.Key] = new SkillOverride(entry.Value, null);
            }
        }

        foreach (KeyValuePair<string, string> entry in Read(managedFile ?? ManagedSettingsFile))
        {
            result[entry.Key] = new SkillOverride(entry.Value, "your organization's managed settings");
        }

        return result;
    }

    /// <summary>
    /// Turns a skill on or off for the repository in .claude/settings.local.json, keeping the rest of the file. Turning
    /// one on removes the entry, unless the repository's or the user's settings turn it off, which an explicit "on" beats.
    /// </summary>
    public static void Set(string repoRoot, string skill, bool enabled, string? userDirectory = null)
    {
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = LocalFile(repoRoot);
        JsonObject root = ReadObject(local) ?? [];
        if (root["skillOverrides"] is not JsonObject overrides)
        {
            root["skillOverrides"] = overrides = [];
        }

        if (!enabled)
        {
            overrides[skill] = "off";
        }
        else
        {
            overrides.Remove(skill);
            bool lowerTurnsOff = PermissionRules.SettingsFiles(repoRoot, userDirectory)
                .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(local), StringComparison.OrdinalIgnoreCase))
                .Select(f => Read(f).TryGetValue(skill, out string? v) ? v : null)
                .LastOrDefault(v => v != null) is string lower && new SkillOverride(lower, null) is { ClaudeCanUse: false };
            if (lowerTurnsOff)
            {
                overrides[skill] = "on";
            }
        }

        if (overrides.Count == 0)
        {
            root.Remove("skillOverrides");
        }

        AtomicFile.WriteAllText(local, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>
    /// The overrides in settings.local.json, as one string that changes when they do (so Claude Code can be restarted
    /// to pick them up).
    /// </summary>
    public static string Signature(string repoRoot) =>
        string.Join(",", Read(LocalFile(repoRoot)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    private static string LocalFile(string repoRoot) => Path.Combine(repoRoot, ".claude", "settings.local.json");

    private static Dictionary<string, string> Read(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ClaudeSettingsFile.Read(file) is JsonElement root && root.TryGetProperty("skillOverrides", out JsonElement overrides) && overrides.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in overrides.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && Values.Contains(p.Value.GetString()))
                {
                    map[p.Name] = p.Value.GetString()!;
                }
            }
        }

        return map;
    }

    /// <summary>The file's root object; null if it doesn't exist. A file that isn't valid JSON isn't overwritten.</summary>
    private static JsonObject? ReadObject(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        string text = File.ReadAllText(file);
        return text.Trim().Length == 0 ? null
            : JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
                ?? throw new InvalidOperationException($"{file} doesn't hold a JSON object, so it was left alone.");
    }
}
