using System.Collections.Generic;
using System.Linq;

namespace ClaudeForVS.Agent.Mcp;

/// <summary>How a server in `claude mcp list` answered its health check.</summary>
internal enum McpHealth
{
    Unknown,
    Connected,
    NeedsAuthentication,
    Failed,
}

/// <summary>One server from `claude mcp list`.</summary>
/// <param name="Target">What it runs or connects to, as Claude Code printed it.</param>
/// <param name="StatusText">Claude Code's words for its health, e.g. "Connected" or "Needs authentication".</param>
internal sealed record ReportedMcpServer(string Name, string Target, McpHealth Health, string StatusText)
{
    /// <summary>A claude.ai connector, which Claude Code names "claude.ai Gmail" and so on.</summary>
    public bool IsConnector => this.Name.StartsWith("claude.ai ", System.StringComparison.Ordinal);

    /// <summary>A plugin's server, which Claude Code names "plugin:plugin-name:server".</summary>
    public bool IsPlugin => this.Name.StartsWith("plugin:", System.StringComparison.Ordinal);
}

/// <summary>Reads the output of `claude mcp list`: every server Claude Code connects to, wherever it's configured.</summary>
internal static class McpServerList
{
    // Before the status, depending on the console: ✓ or √ (connected), ✗ or × (failed), ! or ⚠ (needs attention).
    private static readonly char[] StatusMarks = ['✓', '√', '✔', '✗', '×', '✘', '!', '⚠', ' '];

    /// <summary>Each "name: target - status" line; the header and anything else is skipped.</summary>
    public static IReadOnlyList<ReportedMcpServer> Parse(string output)
    {
        var servers = new List<ReportedMcpServer>();
        foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(": ", System.StringComparison.Ordinal);
            int dash = line.LastIndexOf(" - ", System.StringComparison.Ordinal);
            if (colon <= 0 || dash <= colon)
            {
                continue;
            }

            string status = line.Substring(dash + 3).TrimStart(StatusMarks).Trim();
            McpHealth health = status.IndexOf("Connected", System.StringComparison.OrdinalIgnoreCase) >= 0 ? McpHealth.Connected
                : status.IndexOf("auth", System.StringComparison.OrdinalIgnoreCase) >= 0 ? McpHealth.NeedsAuthentication
                : status.IndexOf("Fail", System.StringComparison.OrdinalIgnoreCase) >= 0 ? McpHealth.Failed
                : McpHealth.Unknown;
            servers.Add(new ReportedMcpServer(line.Substring(0, colon).Trim(), line.Substring(colon + 2, dash - colon - 2).Trim(), health, status));
        }

        return servers.GroupBy(s => s.Name).Select(g => g.Last()).ToList();
    }
}
