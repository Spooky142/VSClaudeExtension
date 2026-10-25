using System;
using System.Collections.Generic;
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
/// answered when last checked. Turning one off is saved straight away; servers that are on and need no approval are
/// checked when the window opens.
/// </summary>
public partial class McpServersDialog : DialogWindow
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly string repoRoot;
    private readonly JoinableTaskFactory jtf;
    private readonly List<McpServerRow> rows;

    // Cancels the checks still running when the window closes.
    private readonly CancellationTokenSource closing = new();

    internal McpServersDialog(string repoRoot, ChatEngineKind engine, JoinableTaskFactory jtf)
    {
        this.repoRoot = repoRoot;
        this.jtf = jtf;
        this.InitializeComponent();
        HighContrast.Follow(this);

        McpConfiguration config = McpConfig.Load(repoRoot);
        IReadOnlyCollection<string> disabled = McpDisabledServers.Default.For(repoRoot);
        this.rows = config.Servers
            .Select(s => new McpServerRow(s, !disabled.Contains(s.Name), config.IsPreApproved(s) || McpApprovals.IsApproved(repoRoot, s)))
            .ToList();
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
        this.EmptyNote.Visibility = this.rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.CheckAllButton.Visibility = this.rows.Any(r => r.IsCheckable) ? Visibility.Visible : Visibility.Collapsed;
        this.RepoText.Text = "In " + Path.GetFileName(repoRoot.TrimEnd('\\', '/')) + " · " + repoRoot;
        this.EngineNote.Text = engine == ChatEngineKind.ClaudeCode
            ? "From ~/.claude.json and the repository's .mcp.json. Claude Code still starts servers you turn off, but Claude can't use their tools. Servers from Claude Code plugins and claude.ai connectors aren't listed; manage those with /mcp in Claude Code."
            : "From ~/.claude.json and the repository's .mcp.json, the same places Claude Code reads. Servers you turn off aren't started.";
        this.ApplyNote.Text = engine == ChatEngineKind.ClaudeCode
            ? "Changes are saved straight away and apply from your next message."
            : "Changes are saved straight away and apply from your next chat.";

        // Opening the window checks only what a chat would start anyway; a .mcp.json server waiting for approval runs only when Check is clicked.
        foreach (McpServerRow row in this.rows.Where(r => r.IsEnabled && r.IsApproved && r.IsCheckable))
        {
            this.Check(row);
        }

        this.Closed += (_, _) => this.closing.Cancel();
    }

    private void OnEnabledChanged(object sender, EventArgs e)
    {
        if (sender is McpServerRow row)
        {
            McpDisabledServers.Default.Set(this.repoRoot, row.Name, row.IsEnabled);
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
        foreach (McpServerRow row in this.rows.Where(r => r.IsEnabled && r.CanCheck))
        {
            this.Check(row);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();

    /// <summary>Starts the server (or connects to it), lists its tools and stops it again, showing how that went.</summary>
    private void Check(McpServerRow row)
    {
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
                    using McpClient client = await McpClient.StartAsync(row.Config, this.repoRoot, timeout.Token);
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
