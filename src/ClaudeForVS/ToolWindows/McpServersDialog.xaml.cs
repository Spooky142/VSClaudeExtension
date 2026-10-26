using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClaudeForVS.Agent;
using ClaudeForVS.Agent.Mcp;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ClaudeForVS.ToolWindows;

/// <summary>
/// The MCP servers configured for the repository (as Claude Code reads them), each with whether it's on and how it
/// answered when last checked. With the Claude Code engine, `claude mcp list` adds the servers only Claude Code knows
/// about (claude.ai connectors, plugins' servers) and checks them all. Turning one off is saved straight away; with the
/// Claude API engine, servers that are on and need no approval are checked when the window opens.
/// </summary>
public partial class McpServersDialog : DialogWindow
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    // `claude mcp list` checks every server one after another, connectors and all.
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(90);

    private readonly string repoRoot;
    private readonly ChatEngineKind engine;
    private readonly JoinableTaskFactory jtf;
    private readonly ObservableCollection<McpServerRow> rows;

    // Cancels the checks still running when the window closes.
    private readonly CancellationTokenSource closing = new();

    private bool listing;

    internal McpServersDialog(string repoRoot, ChatEngineKind engine, JoinableTaskFactory jtf)
    {
        this.repoRoot = repoRoot;
        this.engine = engine;
        this.jtf = jtf;
        this.InitializeComponent();
        HighContrast.Follow(this);

        McpConfiguration config = McpConfig.Load(repoRoot);
        IReadOnlyCollection<string> disabled = DisabledNames.McpServers.For(repoRoot);
        this.rows = new ObservableCollection<McpServerRow>(config.Servers
            .Select(s => new McpServerRow(s, !disabled.Contains(s.Name), config.IsPreApproved(s) || McpApprovals.IsApproved(repoRoot, s))));
        foreach (McpServerRow row in this.rows)
        {
            row.EnabledChanged += this.OnEnabledChanged;
            if (!row.IsCheckable && row.IsEnabled)
            {
                row.SetStatus(McpServerState.Unknown, engine == ChatEngineKind.ClaudeCode
                    ? "Claude Code connects to SSE servers itself; this window can't check them."
                    : "SSE servers work only with the Claude Code engine.");
            }
        }

        this.ServerList.ItemsSource = this.rows;
        this.RepoText.Text = "In " + Path.GetFileName(repoRoot.TrimEnd('\\', '/')) + " · " + repoRoot;
        this.EngineNote.Text = engine == ChatEngineKind.ClaudeCode
            ? "Every server Claude Code connects to here: from ~/.claude.json and the repository's .mcp.json, plus claude.ai connectors and Claude Code plugins. Claude Code still starts servers you turn off, but Claude can't use their tools."
            : "From ~/.claude.json and the repository's .mcp.json, the same places Claude Code reads. Servers you turn off aren't started. claude.ai connectors and plugins' servers work only with the Claude Code engine.";
        this.ApplyNote.Text = engine == ChatEngineKind.ClaudeCode
            ? "Changes are saved straight away and apply from your next message."
            : "Changes are saved straight away and apply from your next chat.";

        if (engine == ChatEngineKind.ClaudeCode)
        {
            // Claude Code's list checks every server it would start, which is what Check all does too.
            this.ListFromClaudeCode();
        }
        else
        {
            // Opening the window checks only what a chat would start anyway; a .mcp.json server waiting for approval runs only when Check is clicked.
            foreach (McpServerRow row in this.rows.Where(r => r.IsEnabled && r.IsApproved && r.IsCheckable))
            {
                this.Check(row);
            }
        }

        this.UpdateButtons();
        this.Closed += (_, _) => this.closing.Cancel();
    }

    private void UpdateButtons()
    {
        this.EmptyNote.Visibility = this.rows.Count == 0 && !this.listing ? Visibility.Visible : Visibility.Collapsed;
        this.CheckAllButton.Visibility = this.engine == ChatEngineKind.ClaudeCode || this.rows.Any(r => r.IsCheckable) ? Visibility.Visible : Visibility.Collapsed;
        this.CheckAllButton.IsEnabled = !this.listing;
    }

    private void OnEnabledChanged(object sender, EventArgs e)
    {
        if (sender is McpServerRow row)
        {
            DisabledNames.McpServers.Set(this.repoRoot, row.ToolName, row.IsEnabled);
        }
    }

    private void OnCheck(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is McpServerRow row)
        {
            this.Check(row);
        }
    }

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        if (this.engine == ChatEngineKind.ClaudeCode)
        {
            this.ListFromClaudeCode();
            return;
        }

        foreach (McpServerRow row in this.rows.Where(r => r.IsEnabled && r.CanCheck))
        {
            this.Check(row);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();

    /// <summary>
    /// Runs `claude mcp list`, which reports every server Claude Code connects to and how it answered: the configured
    /// rows get its status, and servers only Claude Code knows about are added.
    /// </summary>
    private void ListFromClaudeCode()
    {
        if (ClaudeCodeCli.FindExecutable() is not string exe)
        {
            this.ListNote.Text = "Claude Code isn't installed, so only the servers in its configuration files are listed.";
            this.ListNote.Visibility = Visibility.Visible;
            return;
        }

        this.listing = true;
        this.ListNote.Text = "Asking Claude Code for its servers and checking them…";
        this.ListNote.Visibility = Visibility.Visible;
        foreach (McpServerRow row in this.rows.Where(r => r.IsEnabled && r.IsApproved))
        {
            row.SetStatus(McpServerState.Checking, "Checking…");
        }

        this.UpdateButtons();
        CancellationToken token = this.closing.Token;
        this.jtf.RunAsync(async () =>
        {
            IReadOnlyList<ReportedMcpServer>? servers = null;
            string? error = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ListTimeout);
                servers = await Task.Run(() => ClaudeCodeCli.ListMcpServersAsync(exe, this.repoRoot, timeout.Token));
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    return; // the window has closed
                }

                error = ex is OperationCanceledException ? $"Claude Code didn't list its servers within {ListTimeout.TotalSeconds:0} seconds." : "Claude Code couldn't list its servers: " + ex.Message;
            }

            await this.jtf.SwitchToMainThreadAsync();
            this.listing = false;
            this.ShowReported(servers ?? []);
            this.ListNote.Text = error ?? string.Empty;
            this.ListNote.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
            this.UpdateButtons();
        }).Task.FileAndForget("ClaudeForVS/McpList");
    }

    private void ShowReported(IReadOnlyList<ReportedMcpServer> servers)
    {
        IReadOnlyCollection<string> disabled = DisabledNames.McpServers.For(this.repoRoot);
        foreach (ReportedMcpServer server in servers
            .Where(s => s.Name != VisualStudioToolServer.ServerName)
            .OrderBy(s => s.IsConnector)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (this.rows.FirstOrDefault(r => r.ToolName == server.Name) is McpServerRow row)
            {
                row.ShowReported(server);
                continue;
            }

            var added = new McpServerRow(server, !disabled.Contains(server.Name));
            added.EnabledChanged += this.OnEnabledChanged;
            this.rows.Add(added);
        }

        // A configured server Claude Code didn't mention (one from .mcp.json it hasn't been allowed to start, say).
        foreach (McpServerRow row in this.rows.Where(r => r.State == McpServerState.Checking))
        {
            row.SetStatus(McpServerState.Unknown, "Claude Code didn't report on it.");
        }
    }

    /// <summary>Starts the server (or connects to it), lists its tools and stops it again, showing how that went.</summary>
    private void Check(McpServerRow row)
    {
        if (row.Config is not McpServerConfig config)
        {
            return;
        }

        row.SetStatus(McpServerState.Checking, "Checking…");
        CancellationToken token = this.closing.Token;
        this.jtf.RunAsync(async () =>
        {
            McpServerState state;
            string text;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(CheckTimeout);
                int tools = await Task.Run(async () =>
                {
                    using McpClient client = await McpClient.StartAsync(config, this.repoRoot, timeout.Token);
                    return client.Tools.Count;
                });
                (state, text) = (McpServerState.Connected, tools == 1 ? "Connected · 1 tool" : $"Connected · {tools} tools");
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    return; // the window has closed
                }

                (state, text) = (McpServerState.Failed, ex is OperationCanceledException
                    ? $"Didn't answer within {CheckTimeout.TotalSeconds:0} seconds."
                    : "Didn't start: " + ex.Message);
            }

            await this.jtf.SwitchToMainThreadAsync();
            row.SetStatus(state, text);
        }).Task.FileAndForget("ClaudeForVS/McpCheck");
    }
}
