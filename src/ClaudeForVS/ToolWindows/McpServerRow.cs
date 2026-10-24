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
internal sealed class McpServerRow(McpServerConfig config, bool isEnabled, bool isApproved) : INotifyPropertyChanged
{
    private bool isEnabled = isEnabled;
    private McpServerState state = isEnabled ? (isApproved ? McpServerState.Unknown : McpServerState.NeedsApproval) : McpServerState.Off;
    private string statusText = StatusFor(isEnabled, isApproved);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user turns the server on or off.</summary>
    public event EventHandler? EnabledChanged;

    public McpServerConfig Config { get; } = config;

    public string Name => this.Config.Name;

    /// <summary>True if it may run without asking: not from .mcp.json, or already approved.</summary>
    public bool IsApproved { get; } = isApproved;

    public string ScopeText => this.Config.Scope switch
    {
        McpScope.User => "User · every repository",
        McpScope.Local => "Local · this repository, just you",
        _ => "Project · .mcp.json",
    };

    public string TypeText => this.Config.Type;

    public string Target => this.Config.Describe();

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

    /// <summary>This extension can start stdio servers and connect to HTTP ones; SSE servers are left to Claude Code.</summary>
    public bool IsCheckable => this.Config.Type != "sse";

    public bool CanCheck => this.IsCheckable && this.state != McpServerState.Checking;

    public void SetStatus(McpServerState newState, string text)
    {
        this.state = newState;
        this.statusText = text;
        this.OnChanged(nameof(this.State));
        this.OnChanged(nameof(this.StatusText));
        this.OnChanged(nameof(this.CanCheck));
    }

    internal static string StatusFor(bool enabled, bool approved) =>
        !enabled ? "Off" : approved ? "Not checked yet" : "Asks for your approval in a chat before it starts";

    private void OnChanged([CallerMemberName] string? name = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
