using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeForVS.Agent.Mcp;

/// <summary>
/// MCP servers the user turned off in a repository, by name, in %LOCALAPPDATA%\ClaudeForVS\mcp-servers.json.
/// The Claude API engine doesn't start them; Claude Code is told not to use their tools.
/// </summary>
internal sealed class McpDisabledServers(string file)
{
    public static readonly McpDisabledServers Default = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "mcp-servers.json"));

    private readonly object gate = new();

    /// <summary>The names of the servers turned off in this repository.</summary>
    public IReadOnlyCollection<string> For(string repoRoot)
    {
        lock (this.gate)
        {
            return this.Load().TryGetValue(Key(repoRoot), out List<string>? names) ? names : [];
        }
    }

    public bool IsDisabled(string repoRoot, string server) => this.For(repoRoot).Contains(server, StringComparer.Ordinal);

    public void Set(string repoRoot, string server, bool enabled)
    {
        lock (this.gate)
        {
            Dictionary<string, List<string>> all = this.Load();
            if (!all.TryGetValue(Key(repoRoot), out List<string>? names))
            {
                all[Key(repoRoot)] = names = [];
            }

            names.Remove(server);
            if (!enabled)
            {
                names.Add(server);
            }

            if (names.Count == 0)
            {
                all.Remove(Key(repoRoot));
            }

            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Claude Code's deny rule for every tool of a server, "mcp__name", with the name cleaned the way Claude Code
    /// cleans it for tool names.
    /// </summary>
    public static string ToolRule(string server) => "mcp__" + Regex.Replace(server, "[^A-Za-z0-9_-]", "_");

    private static string Key(string repoRoot) => repoRoot.Trim().TrimEnd('\\', '/').ToLowerInvariant();

    private Dictionary<string, List<string>> Load()
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file)) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return [];
    }
}
