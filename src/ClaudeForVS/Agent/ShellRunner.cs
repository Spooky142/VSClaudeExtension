using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

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
    public static async Task<(int? ExitCode, string Output)> RunAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        CancellationToken stopToken = default,
        Action<string>? onOutput = null)
    {
        // UTF-8 for PowerShell's own output and, through the console code page, for the programs it runs. Progress
        // bars would otherwise arrive on stderr as CLIXML. The suffix turns a failing last command into a non-zero exit.
        string script =
            "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8\n"
            + command
            + "\n$claudeOk = $?; if (-not $claudeOk) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Nothing can answer a prompt.
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GIT_PAGER"] = "cat";

        var output = new OutputBuffer();
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

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, stopToken);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (linked.Token.Register(() => stopped.TrySetResult(true)))
        {
            if (await Task.WhenAny(exited.Task, stopped.Task).ConfigureAwait(false) == stopped.Task)
            {
                KillTree(process);
                cancellationToken.ThrowIfCancellationRequested();
                await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(DrainTimeout)).ConfigureAwait(false);
                return (null, output.ToString());
            }
        }

        await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(DrainTimeout)).ConfigureAwait(false);
        return (process.ExitCode, output.ToString());
    }

    /// <summary>Stops the shell and everything it started; Process.Kill on .NET Framework only stops the shell.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            using Process? taskkill = Process.Start(new ProcessStartInfo("taskkill.exe", $"/T /F /PID {process.Id}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            taskkill?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }

        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
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
