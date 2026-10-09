using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>A finished command: its exit code (null if it timed out or was stopped) and what it wrote.</summary>
internal readonly record struct ShellResult(int? ExitCode, string Combined, string Stdout, string Stderr);

/// <summary>Runs a Windows PowerShell command line for the Claude API engine's run_command tool.</summary>
internal static class ShellRunner
{
    // Builds put the errors at the end, so long output keeps a little of the start and most of the end.
    private const int HeadChars = 15_000;
    private const int TailChars = 45_000;

    // Once the process exits, how long to wait for output still in the pipes. Processes it started that outlive it
    // (e.g. MSBuild nodes) can hold the pipes open indefinitely.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Runs the command and returns its exit code (null if it timed out or was stopped) and combined stdout and stderr.</summary>
    /// <param name="cancellationToken">Cancels the whole run, throwing <see cref="OperationCanceledException"/>.</param>
    /// <param name="stopToken">Stops just this command; it then returns like a timeout, with the output so far.</param>
    /// <param name="onOutput">Called with each line of output as it arrives, on a background thread.</param>
    /// <param name="stdin">Text for the command's standard input (UTF-8); null leaves it empty and closed.</param>
    /// <param name="environment">Extra environment variables for the command.</param>
    public static async Task<(int? ExitCode, string Output)> RunAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        CancellationToken stopToken = default,
        Action<string>? onOutput = null,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ShellResult result = await RunSeparatelyAsync(workingDirectory, command, timeout, cancellationToken, stopToken, onOutput, stdin, environment, bash: false).ConfigureAwait(false);
        return (result.ExitCode, result.Combined);
    }

    /// <summary>Like <see cref="RunAsync"/>, but keeps what the command wrote to stdout and to stderr apart (hooks need that).</summary>
    public static async Task<ShellResult> RunSeparatelyAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        CancellationToken stopToken = default,
        Action<string>? onOutput = null,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool bash = false)
    {
        // UTF-8 for PowerShell's own output and, through the console code page, for the programs it runs. Progress
        // bars would otherwise arrive on stderr as CLIXML. The suffix turns a failing last command into a non-zero exit.
        string script =
            "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8\n"
            + command
            + "\n$claudeOk = $?; if (-not $claudeOk) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        ProcessStartInfo psi;
        if (bash)
        {
            // The command goes in an environment variable and is eval'd, so it needs no quoting on the command line.
            string bashPath = FindBash() ?? throw new InvalidOperationException("bash wasn't found (it comes with Git for Windows), so this hook can't run with shell \"bash\".");
            psi = new ProcessStartInfo(bashPath, "-c \"eval \\\"$CLAUDE_HOOK_COMMAND\\\"\"");
        }
        else
        {
            psi = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
        }

        psi.WorkingDirectory = workingDirectory;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        if (bash)
        {
            psi.EnvironmentVariables["CLAUDE_HOOK_COMMAND"] = command;
        }

        // Nothing can answer a prompt.
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GIT_PAGER"] = "cat";
        if (environment != null)
        {
            foreach (KeyValuePair<string, string> variable in environment)
            {
                psi.EnvironmentVariables[variable.Key] = variable.Value;
            }
        }

        var output = new OutputBuffer();
        var stdoutOnly = new OutputBuffer();
        var stderrOnly = new OutputBuffer();
        var stdoutDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult(true);
            }
            else
            {
                output.AppendLine(e.Data);
                stdoutOnly.AppendLine(e.Data);
                onOutput?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.TrySetResult(true);
            }
            else
            {
                output.AppendLine(e.Data);
                stderrOnly.AppendLine(e.Data);
                onOutput?.Invoke(e.Data);
            }
        };
        process.Exited += (_, _) => exited.TrySetResult(true);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Couldn't start powershell.exe: " + ex.Message, ex);
        }

        // Output is read from the start, and the input is written in the background: a command that writes a lot
        // before it reads would otherwise block on a full pipe while this waits to write, and the timeout wouldn't apply.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = WriteInputAsync(process, stdin);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, stopToken);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (linked.Token.Register(() => stopped.TrySetResult(true)))
        {
            if (await Task.WhenAny(exited.Task, stopped.Task).ConfigureAwait(false) == stopped.Task)
            {
                Processes.KillTree(process); // the shell and everything it started
                cancellationToken.ThrowIfCancellationRequested();
                await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(DrainTimeout)).ConfigureAwait(false);
                return new ShellResult(null, output.ToString(), stdoutOnly.ToString(), stderrOnly.ToString());
            }
        }

        await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(DrainTimeout)).ConfigureAwait(false);
        return new ShellResult(process.ExitCode, output.ToString(), stdoutOnly.ToString(), stderrOnly.ToString());
    }

    /// <summary>
    /// Git for Windows' bash, found from the git that's in use (not the WSL launcher in System32, which isn't a real
    /// shell for this). Null if git isn't from Git for Windows.
    /// </summary>
    public static string? FindBash()
    {
        string? git = Git.GitRunner.GitPath;
        if (git is null)
        {
            return null;
        }

        string dir = Path.GetDirectoryName(git) ?? string.Empty;
        foreach (string candidate in new[] { Path.Combine(dir, "..", "bin", "bash.exe"), Path.Combine(dir, "bash.exe"), Path.Combine(dir, "..", "usr", "bin", "bash.exe") })
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>Writes the command's input (UTF-8, no byte order mark, whatever the console's code page is) and closes its stdin.</summary>
    private static async Task WriteInputAsync(Process process, string? stdin)
    {
        try
        {
            if (stdin != null)
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(stdin);
                await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The command exited without reading it all, or has already finished.
        }
    }

    /// <summary>Collects output lines, keeping the start and the end once it gets long.</summary>
    private sealed class OutputBuffer
    {
        private readonly object gate = new();
        private readonly StringBuilder head = new();
        private readonly LinkedList<string> tail = new();
        private int tailChars;
        private long droppedChars;

        public void AppendLine(string line)
        {
            lock (this.gate)
            {
                if (this.head.Length < HeadChars && this.tail.Count == 0)
                {
                    this.head.Append(line).Append('\n');
                    return;
                }

                this.tail.AddLast(line);
                this.tailChars += line.Length + 1;
                while (this.tailChars > TailChars && this.tail.Count > 1)
                {
                    this.droppedChars += this.tail.First.Value.Length + 1;
                    this.tailChars -= this.tail.First.Value.Length + 1;
                    this.tail.RemoveFirst();
                }
            }
        }

        public override string ToString()
        {
            lock (this.gate)
            {
                var sb = new StringBuilder(this.head.ToString());
                if (this.droppedChars > 0)
                {
                    sb.Append($"[… {this.droppedChars:N0} characters of output omitted …]\n");
                }

                foreach (string line in this.tail)
                {
                    sb.Append(line).Append('\n');
                }

                return sb.ToString().TrimEnd('\n');
            }
        }
    }
}
