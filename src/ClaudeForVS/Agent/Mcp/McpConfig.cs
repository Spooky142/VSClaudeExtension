using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeForVS.Agent.Mcp;

/// <summary>Where an MCP server was configured, as in Claude Code.</summary>
internal enum McpScope
{
    /// <summary>~/.claude.json's top-level mcpServers: the user's, for every project.</summary>
    User,

    /// <summary>The repository's .mcp.json, checked in; needs the user's approval before it runs.</summary>
    Project,

    /// <summary>~/.claude.json's entry for this project: the user's own, for this project only.</summary>
    Local,
}

/// <summary>One MCP server from Claude Code's configuration.</summary>
/// <param name="Type">"stdio", "http" or "sse".</param>
internal sealed record McpServerConfig(
    string Name,
    McpScope Scope,
    string Type,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>What it runs or connects to, e.g. "npx -y @modelcontextprotocol/server-github" or a URL.</summary>
    public string Describe() => this.Type == "stdio"
        ? string.Join(" ", new[] { this.Command ?? string.Empty }.Concat(this.Args.Select(Git.GitRunner.QuoteArgument))).Trim()
        : this.Url ?? string.Empty;

    /// <summary>Identifies the server's exact configuration, so an approval doesn't carry over to a changed one.</summary>
    public string Fingerprint()
    {
        string text = JsonSerializer.Serialize(new { this.Name, this.Type, this.Command, this.Args, this.Env, this.Url, this.Headers });
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).Substring(0, 32);
    }
}

/// <summary>The MCP servers configured for a repository, plus which project servers Claude Code was told to trust.</summary>
internal sealed record McpConfiguration(IReadOnlyList<McpServerConfig> Servers, bool EnableAllProjectServers, IReadOnlyCollection<string> EnabledProjectServers)
{
    /// <summary>True if Claude Code's own settings already approve this project server.</summary>
    public bool IsPreApproved(McpServerConfig server) =>
        server.Scope != McpScope.Project || this.EnableAllProjectServers || this.EnabledProjectServers.Contains(server.Name);
}

/// <summary>
/// Reads MCP servers the way Claude Code does: ~/.claude.json (user servers, and local servers under "projects"), and
/// the repository's .mcp.json. A local server beats a project one with the same name, which beats a user one.
/// <c>${VAR}</c> and <c>${VAR:-default}</c> are expanded from the environment.
/// </summary>
internal static class McpConfig
{
    private static readonly Regex Variable = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}", RegexOptions.Compiled);

    /// <param name="userDirectory">The user's profile folder; null for the current user's.</param>
    /// <param name="environment">Looks up environment variables; null for the process's own.</param>
    public static McpConfiguration Load(string repoRoot, string? userDirectory = null, Func<string, string?>? environment = null)
    {
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        environment ??= Environment.GetEnvironmentVariable;
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        bool enableAll = false;

        JsonElement? userConfig = ClaudeSettingsFile.Read(Path.Combine(userDirectory, ".claude.json"));
        if (userConfig is JsonElement user)
        {
            AddServers(user, McpScope.User, servers, environment);
        }

        if (ClaudeSettingsFile.Read(Path.Combine(repoRoot, ".mcp.json")) is JsonElement project)
        {
            AddServers(project, McpScope.Project, servers, environment);
        }

        // Claude Code keeps per-project state, including local servers and project-server approvals, under "projects".
        if (userConfig is JsonElement userRoot && FindProject(userRoot, repoRoot) is JsonElement entry)
        {
            AddServers(entry, McpScope.Local, servers, environment);
            ReadApprovals(entry, enabled, disabled, ref enableAll);
        }

        foreach (string settings in PermissionRules.SettingsFiles(repoRoot, userDirectory))
        {
            if (ClaudeSettingsFile.Read(settings) is JsonElement root)
            {
                // The repository can't approve its own servers; it can still turn them off.
                if (PermissionRules.IsSharedRepoSettings(settings, repoRoot))
                {
                    bool ignored = false;
                    ReadApprovals(root, [], disabled, ref ignored);
                }
                else
                {
                    ReadApprovals(root, enabled, disabled, ref enableAll);
                }
            }
        }

        List<McpServerConfig> list = servers.Values
            .Where(s => !(s.Scope == McpScope.Project && disabled.Contains(s.Name)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new McpConfiguration(list, enableAll, enabled);
    }

    /// <summary>Replaces ${VAR} and ${VAR:-default}; an unset variable without a default becomes empty.</summary>
    internal static string Expand(string text, Func<string, string?> environment) =>
        Variable.Replace(text, m => environment(m.Groups[1].Value) is { Length: > 0 } value ? value : m.Groups[2].Success ? m.Groups[2].Value : string.Empty);

    private static void AddServers(JsonElement root, McpScope scope, Dictionary<string, McpServerConfig> servers, Func<string, string?> environment)
    {
        if (!root.TryGetProperty("mcpServers", out JsonElement map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in map.EnumerateObject())
        {
            if (Parse(property.Name, property.Value, scope, environment) is McpServerConfig server)
            {
                servers[server.Name] = server;
            }
        }
    }

    internal static McpServerConfig? Parse(string name, JsonElement value, McpScope scope, Func<string, string?> environment)
    {
        if (value.ValueKind != JsonValueKind.Object || name.Trim().Length == 0)
        {
            return null;
        }

        string? command = Str(value, "command") is string c ? Expand(c, environment) : null;
        string? url = Str(value, "url") is string u ? Expand(u, environment) : null;
        string type = (Str(value, "type") ?? (command != null ? "stdio" : "http")).ToLowerInvariant();
        if (type == "streamable-http")
        {
            type = "http";
        }

        if ((type == "stdio" && string.IsNullOrWhiteSpace(command)) || (type is "http" or "sse" && string.IsNullOrWhiteSpace(url)) || type is not ("stdio" or "http" or "sse"))
        {
            return null;
        }

        var args = new List<string>();
        if (value.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
        {
            args.AddRange(a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Expand(x.GetString()!, environment)));
        }

        return new McpServerConfig(name, scope, type, command, args, Map(value, "env", environment), url, Map(value, "headers", environment));
    }

    private static IReadOnlyDictionary<string, string> Map(JsonElement value, string name, Func<string, string?> environment)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.TryGetProperty(name, out JsonElement obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    map[p.Name] = Expand(p.Value.GetString()!, environment);
                }
            }
        }

        return map;
    }

    /// <summary>The repository's entry under ~/.claude.json's "projects", whose keys are paths (usually with forward slashes).</summary>
    private static JsonElement? FindProject(JsonElement root, string repoRoot)
    {
        if (!root.TryGetProperty("projects", out JsonElement projects) || projects.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string wanted = repoRoot.Replace('\\', '/').TrimEnd('/');
        foreach (JsonProperty project in projects.EnumerateObject())
        {
            if (string.Equals(project.Name.Replace('\\', '/').TrimEnd('/'), wanted, StringComparison.OrdinalIgnoreCase) && project.Value.ValueKind == JsonValueKind.Object)
            {
                return project.Value;
            }
        }

        return null;
    }

    private static void ReadApprovals(JsonElement root, HashSet<string> enabled, HashSet<string> disabled, ref bool enableAll)
    {
        if (root.TryGetProperty("enableAllProjectMcpServers", out JsonElement all) && all.ValueKind == JsonValueKind.True)
        {
            enableAll = true;
        }

        foreach ((string key, HashSet<string> set) in new[] { ("enabledMcpjsonServers", enabled), ("disabledMcpjsonServers", disabled) })
        {
            if (root.TryGetProperty(key, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        set.Add(item.GetString()!);
                    }
                }
            }
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Project MCP servers (.mcp.json) the user chose to always start in a repository, in %LOCALAPPDATA%\ClaudeForVS\mcp-approvals.json.</summary>
internal static class McpApprovals
{
    private static readonly string ApprovalsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "mcp-approvals.json");

    private static readonly object Gate = new();

    public static bool IsApproved(string repoRoot, McpServerConfig server)
    {
        lock (Gate)
        {
            return Load().TryGetValue(Key(repoRoot), out List<string>? list) && list.Contains(server.Fingerprint());
        }
    }

    public static void Approve(string repoRoot, IEnumerable<McpServerConfig> servers)
    {
        lock (Gate)
        {
            Dictionary<string, List<string>> all = Load();
            if (!all.TryGetValue(Key(repoRoot), out List<string>? list))
            {
                all[Key(repoRoot)] = list = [];
            }

            foreach (McpServerConfig server in servers)
            {
                if (!list.Contains(server.Fingerprint()))
                {
                    list.Add(server.Fingerprint());
                }
            }

            try
            {
                AtomicFile.WriteAllText(ApprovalsFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Key(string repoRoot) => repoRoot.Trim().TrimEnd('\\', '/').ToLowerInvariant();

    private static Dictionary<string, List<string>> Load()
    {
        try
        {
            if (File.Exists(ApprovalsFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(ApprovalsFile)) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return [];
    }
}
