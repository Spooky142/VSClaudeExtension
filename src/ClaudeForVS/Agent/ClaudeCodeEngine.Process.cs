using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.Git;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.Agent;

/// <summary>Claude Code's process: starting it, writing prompts to it, reading its output between messages and stopping it.</summary>
internal sealed partial class ClaudeCodeEngine
{
    /// <summary>
    /// A message takes over reading Claude Code's output, before its prompt goes in so no line of the reply can be
    /// dropped: the first line is the idle read in progress, if there is one, or a new read.
    /// </summary>
    private Task<string?> ReadFirstLineAsync(Process process)
    {
        lock (this.outputLock)
        {
            this.messageReading = true;
            Task<string?> read = this.idleRead ?? process.StandardOutput.ReadLineAsync();
            this.idleRead = null;
            return read;
        }
    }

    /// <summary>
    /// Reads and drops what Claude Code writes after a reply has ended, until the next message takes over. Nothing should
    /// come then, but if something did and wasn't read, the pipe would fill, and a Claude Code that can't write its output
    /// stops dead (subagents, API requests and all) until someone reads it.
    /// </summary>
    private async Task DrainIdleOutputAsync(Process process)
    {
        lock (this.outputLock)
        {
            this.messageReading = false;
        }

        try
        {
            while (true)
            {
                Task<string?> read;
                lock (this.outputLock)
                {
                    if (this.messageReading || this.process != process)
                    {
                        return;
                    }

                    read = this.idleRead = process.StandardOutput.ReadLineAsync();
                }

                string? line = await read.ConfigureAwait(false);
                lock (this.outputLock)
                {
                    if (this.idleRead != read)
                    {
                        return; // a message took this read over, line and all
                    }

                    this.idleRead = null;
                }

                if (line is null)
                {
                    return; // it exited
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // It was stopped.
        }
    }

    private static async Task WriteAsync(Process process, byte[] bytes, CancellationToken cancellationToken)
    {
        Stream stdin = process.StandardInput.BaseStream;
        await stdin.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        await stdin.FlushAsync(cancellationToken);
    }

    /// <summary>The Claude Code process for the next message: the running one if it still fits the chat's settings, otherwise a new one.</summary>
    private async Task<Process> EnsureProcessAsync(IChatSink chatSink)
    {
        // MCP servers turned off in the MCP servers window: Claude Code still loads them, but can't use their tools.
        string disabledMcp = string.Join(",", McpDisabledServers.Default.For(this.RepoRoot).Select(McpDisabledServers.ToolRule));
        if (this.process is { HasExited: false } running && !this.restartNeeded && this.processSettings == (this.Mode, this.Effort, this.SubagentModel, disabledMcp, LowPriorityMode.IsActive))
        {
            chatSink.SetStatus("Claude Code is working…");
            return running;
        }

        await this.StopProcessAsync();
        this.restartNeeded = false;
        this.processSettings = (this.Mode, this.Effort, this.SubagentModel, disabledMcp, LowPriorityMode.IsActive);

        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--model", AgentSettings.ClaudeCodeAlias(this.Model),
            "--permission-mode", this.Mode == AgentMode.Plan ? "plan" : "default",
            "--mcp-config", this.mcpConfigPath,
            "--permission-prompt-tool", ApprovalServer.QualifiedToolName,
            "--append-system-prompt", ExtraSystemPrompt + " " + AgentSettings.ModeInstructions(this.Mode),
            "--effort", AgentSettings.EffortApiName(this.Effort),
        };

        // Passed as command-line settings rather than an environment variable, which an env block in the user's
        // ~/.claude/settings.json can replace; settings from the command line take precedence over the user's.
        if (this.SubagentModel is ClaudeModel subagentModel)
        {
            args.Add("--settings");
            args.Add(JsonSerializer.Serialize(new { env = new Dictionary<string, string> { ["CLAUDE_CODE_SUBAGENT_MODEL"] = AgentSettings.ClaudeCodeAlias(subagentModel) } }));
        }

        if (disabledMcp.Length > 0)
        {
            args.Add("--disallowedTools");
            args.Add(disabledMcp);
        }

        if (this.SessionId != null)
        {
            args.Add("--resume");
            args.Add(this.SessionId);
            if (this.resumeAt != null)
            {
                args.Add("--resume-session-at");
                args.Add(this.resumeAt);
                args.Add("--fork-session");
                this.resumeAt = null;
            }
        }

        var psi = new ProcessStartInfo(this.executable, string.Join(" ", args.Select(GitRunner.QuoteArgument)))
        {
            WorkingDirectory = this.RepoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ClaudeCodeCli.RemoveApiKeyVariables(psi); // use the subscription login, not a stray API key
        LowPriorityMode.Apply(psi); // past the 5-hour limit, carry on against the weekly one

        // The approval tool's reply echoes the tool's whole input back (e.g. a file being written), JSON-escaped inside a
        // text block. Past Claude Code's default MCP output limit (25k tokens) the reply gets truncated, which Claude Code
        // then rejects as an invalid permission result, so large writes would always fail.
        psi.EnvironmentVariables["MAX_MCP_OUTPUT_TOKENS"] = "2000000";

        // Left to itself, Claude Code runs subagents (and long commands) in the background and ends its reply while they
        // work, and a scheduled prompt starts a reply of its own later. Either way the output comes when no message is
        // waiting for it, so the user never sees it; with these off, a reply waits for its subagents.
        psi.EnvironmentVariables["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1";
        psi.EnvironmentVariables["CLAUDE_CODE_DISABLE_CRON"] = "1";

        chatSink.SetStatus("Starting Claude Code…");
        var started = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!started.Start())
        {
            started.Dispose();
            throw new InvalidOperationException("Couldn't start Claude Code.");
        }

        this.process = started;
        this.processStderr = started.StandardError.ReadToEndAsync();
        return started;
    }

    /// <summary>
    /// Ends the Claude Code process, if there is one, without blocking: closing stdin lets an idle one finish and
    /// exit, and one that doesn't within a few seconds is stopped. Returns what it wrote to stderr.
    /// </summary>
    private async Task<string> StopProcessAsync()
    {
        if (this.process is not Process old)
        {
            return string.Empty;
        }

        Task<string>? stderr = this.processStderr;
        lock (this.outputLock)
        {
            this.process = null;
            this.idleRead = null;
        }

        this.processStderr = null;
        CloseInput(old);
        if (!await WaitForExitAsync(old, TimeSpan.FromSeconds(5)))
        {
            await Task.Run(() => Processes.KillTree(old));
            await WaitForExitAsync(old, TimeSpan.FromSeconds(5));
        }

        // Once it has exited, stderr reaches its end; read it before Dispose closes the pipe.
        string err = string.Empty;
        if (stderr != null && await Task.WhenAny(stderr, Task.Delay(TimeSpan.FromSeconds(2))) == stderr && stderr.Status == TaskStatus.RanToCompletion)
        {
            err = (await stderr).Trim();
        }

        old.Dispose();
        return err;
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Waits for the process to exit without blocking a thread; false if it's still running after the timeout.</summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        try
        {
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return true; // never started, or already released
        }

        return await Task.WhenAny(exited.Task, Task.Delay(timeout)).ConfigureAwait(false) == exited.Task;
    }

    /// <summary>Refreshes plan usage every so often while a prompt runs, so the bars move before the reply ends.</summary>
    private static async Task PollPlanUsageAsync(IChatSink chatSink, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PlanUsagePollInterval, cancellationToken);
                foreach (PlanLimit limit in await PlanUsageClient.FetchAsync(cancellationToken))
                {
                    chatSink.ReportPlanLimit(limit);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
