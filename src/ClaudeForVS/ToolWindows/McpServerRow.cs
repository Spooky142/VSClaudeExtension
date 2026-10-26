using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClaudeForVS.Agent.Mcp;

namespace ClaudeForVS.ToolWindows;

/// <summary>How an MCP server in the MCP servers window is doing, which picks its status dot's colour.</summary>
internal enum McpServerState
{
    Unknown,
    Checking,
    Connected,
    Failed,
    Off,
    NeedsApproval,
}

/// <summary>One server in the MCP servers window: where it's configured, whether it's on, and how it last answered.</summary>
internal sealed class McpServerRow : INotifyPropertyChanged
{
    private bool isEnabled;
    private McpServerState state;
    private string statusText;

    /// <summary>A server from ~/.claude.json or .mcp.json, which this window can check itself.</summary>
    public McpServerRow(McpServerConfig config, bool isEnabled, bool isApproved)
    {
        this.Config = config;
        this.Name = this.ToolName = config.Name;
        this.IsApproved = isApproved;
        this.ScopeText = config.Scope switch
        {
            McpScope.User => "User · every repository",
            McpScope.Local => "Local · this repository, just you",
            _ => "Project · .mcp.json",
        };
        this.TypeText = config.Type;
        this.Target = config.Describe();
        this.isEnabled = isEnabled;
        this.state = isEnabled ? (isApproved ? McpServerState.Unknown : McpServerState.NeedsApproval) : McpServerState.Off;
        this.statusText = StatusFor(isEnabled, isApproved);
    }

    /// <summary>A server only Claude Code knows about (a claude.ai connector or a plugin's), as `claude mcp list` reported it.</summary>
    public McpServerRow(ReportedMcpServer server, bool isEnabled)
    {
        this.Name = server.IsConnector ? server.Name.Substring("claude.ai ".Length) : server.Name;
        this.IsApproved = true;
        this.ScopeText = server.IsConnector ? "claude.ai connector · your account"
            : server.IsPlugin ? "Plugin · " + server.Name.Split(':')[1]
            : "Claude Code";
        this.TypeText = server.IsConnector ? "claude.ai" : string.Empty;
        this.Target = server.Target;
        this.ToolName = server.Name;
        this.isEnabled = isEnabled;
        this.state = McpServerState.Off;
        this.statusText = "Off";
        this.ShowReported(server);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user turns the server on or off.</summary>
    public event EventHandler? EnabledChanged;

    /// <summary>Its configuration; null for a server only Claude Code knows about.</summary>
    public McpServerConfig? Config { get; }

    public string Name { get; }

    /// <summary>The name Claude Code knows it by, which is what's turned off (e.g. "claude.ai Gmail" for the Gmail row).</summary>
    public string ToolName { get; }

    /// <summary>True if it may run without asking: not from .mcp.json, or already approved.</summary>
    public bool IsApproved { get; }

    public string ScopeText { get; }

    public string TypeText { get; }

    public string Target { get; }

    public bool IsEnabled
    {
        get => this.isEnabled;
        set
        {
            if (this.isEnabled == value)
            {
                return;
            }

            this.isEnabled = value;
            this.OnChanged();
            this.SetStatus(value ? (this.IsApproved ? McpServerState.Unknown : McpServerState.NeedsApproval) : McpServerState.Off, StatusFor(value, this.IsApproved));
            this.EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public McpServerState State => this.state;

    public string StatusText => this.statusText;

    /// <summary>This extension can start stdio servers and connect to HTTP ones; SSE servers and those only Claude Code knows about are left to it.</summary>
    public bool IsCheckable => this.Config is { Type: not "sse" };

    public bool CanCheck => this.IsCheckable && this.state != McpServerState.Checking;

    public void SetStatus(McpServerState newState, string text)
    {
        this.state = newState;
        this.statusText = text;
        this.OnChanged(nameof(this.State));
        this.OnChanged(nameof(this.StatusText));
        this.OnChanged(nameof(this.CanCheck));
    }

    /// <summary>Shows how the server answered `claude mcp list`, unless it's turned off here.</summary>
    public void ShowReported(ReportedMcpServer server)
    {
        if (!this.isEnabled)
        {
            return;
        }

        string text = server.Health switch
        {
            McpHealth.NeedsAuthentication => server.IsConnector
                ? "Needs authentication: connect it in claude.ai's connector settings, or with /mcp in Claude Code"
                : "Needs authentication: sign in with /mcp in Claude Code",
            _ => server.StatusText.Length > 0 ? server.StatusText : "Not checked",
        };
        this.SetStatus(server.Health switch
        {
            McpHealth.Connected => McpServerState.Connected,
            McpHealth.Failed => McpServerState.Failed,
            McpHealth.NeedsAuthentication => McpServerState.NeedsApproval,
            _ => McpServerState.Unknown,
        }, text);
    }

    internal static string StatusFor(bool enabled, bool approved) =>
        !enabled ? "Off" : approved ? "Not checked yet" : "Asks for your approval in a chat before it starts";

    private void OnChanged([CallerMemberName] string? name = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
