using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.Git;
using ApiEffort = Anthropic.Models.Messages.Effort;

namespace ClaudeForVS.Agent;

/// <summary>MCP servers: starting them, naming their tools and calling them.</summary>
internal sealed partial class ClaudeAgent
{
    /// <summary>
    /// Works out the tools for this chat before its first message: the built-in ones (less any the user's permission
    /// settings deny) plus the tools of the MCP servers configured for the repository, less any the user turned off.
    /// Servers from the repository's .mcp.json run only after the user approves them.
    /// </summary>
    private async Task EnsureToolsAsync(IChatSink sink, CancellationToken cancellationToken)
    {
        if (this.tools != null)
        {
            return;
        }

        var list = BuiltInTools(this.permissions, this.Model);
        var mcpList = new List<(string Name, string Description, IReadOnlyDictionary<string, JsonElement> Schema)>();
        McpConfiguration config = McpConfig.Load(this.RepoRoot);
        IReadOnlyCollection<string> disabled = McpDisabledServers.Default.For(this.RepoRoot);
        List<McpServerConfig> servers = config.Servers.Where(s => !disabled.Contains(s.Name)).ToList();
        List<McpServerConfig> needApproval = servers.Where(s => !config.IsPreApproved(s) && !McpApprovals.IsApproved(this.RepoRoot, s)).ToList();
        List<McpServerConfig> toStart = servers.Except(needApproval).ToList();
        if (needApproval.Count > 0)
        {
            sink.SetStatus("Waiting for your approval…");
            string title = needApproval.Count == 1
                ? $"Start the MCP server \"{needApproval[0].Name}\" from .mcp.json"
                : $"Start {needApproval.Count} MCP servers from .mcp.json";
            string detail = string.Join("\n", needApproval.Select(s => $"{s.Name}: {s.Describe()}"))
                + "\n\nThey come from the repository's .mcp.json and run on this machine with your permissions.";
            ApprovalResult approval = await sink.RequestApprovalAsync(ActivityKind.Shell, title, detail, "Always allow here", cancellationToken);
            if (approval.Decision == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, title, "Not started. You'll be asked again in your next chat.");
            }
            else
            {
                if (approval.Decision == Approval.AllowAll)
                {
                    McpApprovals.Approve(this.RepoRoot, needApproval);
                }

                toStart.AddRange(needApproval);
            }
        }

        if (toStart.Count > 0)
        {
            sink.SetStatus(toStart.Count == 1 ? $"Starting the MCP server {toStart[0].Name}…" : $"Starting {toStart.Count} MCP servers…");
            var started = await Task.WhenAll(toStart.Select(async server =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(McpStartTimeout);
                try
                {
                    return (Server: server, Client: await Task.Run(() => McpClient.StartAsync(server, this.RepoRoot, timeout.Token)), Error: (string?)null);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Whatever went wrong with one server (it isn't installed, crashed, isn't MCP...), the chat carries on without it.
                    string error = ex is OperationCanceledException ? $"It didn't answer within {McpStartTimeout.TotalSeconds:0} seconds." : ex.Message;
                    return (Server: server, Client: (McpClient?)null, Error: error);
                }
            }));

            var connected = new List<string>();
            foreach ((McpServerConfig server, McpClient? mcp, string? error) in started)
            {
                if (mcp is null)
                {
                    sink.ToolActivity(ActivityKind.Failed, $"MCP server {server.Name} didn't start", $"{error}\n{server.Describe()}".Trim());
                    continue;
                }

                this.mcpClients.Add(mcp);
                int count = 0;
                foreach (McpTool tool in mcp.Tools)
                {
                    string name = this.UniqueToolName(server.Name, tool.Name);
                    if (this.permissions.ForTool(name) == PermissionVerdict.Deny)
                    {
                        continue;
                    }

                    this.mcpTools[name] = (mcp, tool);
                    mcpList.Add((name, tool.Description.Length > 4000 ? tool.Description.Substring(0, 4000) : tool.Description, tool.InputSchema));
                    count++;
                }

                connected.Add($"{server.Name} · {(count == 1 ? "1 tool" : $"{count} tools")}");
            }

            if (connected.Count > 0)
            {
                sink.ToolActivity(ActivityKind.Other, connected.Count == 1 ? "Connected to an MCP server" : $"Connected to {connected.Count} MCP servers", string.Join("\n", connected));
            }
        }

        // Every request carries every tool's definition. When the MCP servers' add up to a lot, they're left out until
        // Claude searches for one it needs. (Haiku doesn't take the search tool, so it always gets them all.)
        bool defer = this.Model != ClaudeModel.Haiku
            && mcpList.Sum(t => t.Description.Length + JsonSerializer.Serialize(t.Schema).Length) > DeferMcpToolsAtChars;
        foreach ((string name, string description, IReadOnlyDictionary<string, JsonElement> schema) in mcpList)
        {
            list.Add(defer
                ? new Tool { Name = name, Description = description, InputSchema = InputSchema.FromRawUnchecked(schema), DeferLoading = true }
                : new Tool { Name = name, Description = description, InputSchema = InputSchema.FromRawUnchecked(schema) });
        }

        if (defer)
        {
            list.Add(new ToolSearchToolBm25_20251119 { Type = ToolSearchToolBm25_20251119Type.ToolSearchToolBm25_20251119 });
        }

        this.tools = list;
    }

    // About 10K tokens of MCP tool definitions; below that, searching for tools costs more than it saves.
    private const int DeferMcpToolsAtChars = 40_000;

    /// <summary>"mcp__server__tool", with characters tool names can't contain replaced, and kept unique.</summary>
    private string UniqueToolName(string server, string tool)
    {
        static string Clean(string s) => Regex.Replace(s, "[^A-Za-z0-9_-]", "_");
        string name = $"mcp__{Clean(server)}__{Clean(tool)}";
        if (name.Length > 64)
        {
            name = name.Substring(0, 64);
        }

        string unique = name;
        for (int i = 2; this.mcpTools.ContainsKey(unique); i++)
        {
            unique = name.Substring(0, Math.Min(name.Length, 60)) + "_" + i;
        }

        return unique;
    }

    private async Task<string> CallMcpToolAsync(string name, McpClient mcp, McpTool tool, IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string title = $"{mcp.Config.Name} · {tool.Name}";
        string arguments = input.Count == 0 ? string.Empty : JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true });
        PermissionVerdict verdict = this.permissions.ForTool(name);
        if (verdict == PermissionVerdict.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Denied by your permission settings.");
            return SettingsRefusal;
        }

        if (this.Mode == AgentMode.Plan && !tool.ReadOnly)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Blocked in Plan mode.");
            return PlanModeRefusal + " (This MCP tool doesn't say it's read-only.)";
        }

        bool ask = verdict == PermissionVerdict.Ask
            || (this.Mode == AgentMode.Coding && verdict != PermissionVerdict.Allow && !CommandRules.IsToolAllowed(this.RepoRoot, name));
        if (ask)
        {
            sink.SetStatus("Waiting for your approval…");
            ApprovalResult approval = await sink.RequestApprovalAsync(
                ActivityKind.Other, $"Use {title}", arguments, verdict == PermissionVerdict.Ask ? null : $"Always allow {tool.Name} here", cancellationToken);
            if (approval.Decision == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, title, "Declined by you.");
                return "The user declined this tool call.";
            }

            if (approval.Decision == Approval.AllowAll)
            {
                CommandRules.Allow(this.RepoRoot, name);
            }
        }

        sink.SetStatus($"Running {title}…");
        McpCallResult result;
        try
        {
            result = await Task.Run(() => mcp.CallToolAsync(tool.Name, input, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is McpClient.McpException or System.Net.Http.HttpRequestException or JsonException)
        {
            throw new InvalidOperationException($"{title} failed: {ex.Message}");
        }

        sink.ToolActivity(result.IsError ? ActivityKind.Failed : ActivityKind.Other, title, result.Text);
        return result.IsError ? "The tool reported an error:\n" + result.Text : result.Text;
    }
}
