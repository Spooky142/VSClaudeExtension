using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>The run_command and run_tests tools, which run PowerShell in the repository.</summary>
internal sealed partial class ClaudeAgent
{
    private const int DefaultTestSeconds = 600;

    private async Task<string> RunCommandAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string command = RequireString(input, "command").Trim();
        int timeout = Math.Max(1, Math.Min(MaxCommandSeconds, OptionalInt(input, "timeout_seconds") ?? DefaultCommandSeconds));
        string directory = this.RepoRoot;
        string where = string.Empty;
        if (input.TryGetValue("cwd", out JsonElement cwdEl) && cwdEl.ValueKind == JsonValueKind.String
            && cwdEl.GetString() is string cwd && cwd.Trim().Length > 0 && cwd.Trim() != ".")
        {
            directory = this.files.Resolve(cwd);
            if (!Directory.Exists(directory))
            {
                throw new ToolInputException($"Directory not found: {cwd}");
            }

            where = $" (in {this.files.Relative(directory)})";
        }

        PermissionVerdict verdict = this.permissions.ForCommand(command);
        if (verdict == PermissionVerdict.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, command + where, "Denied by your permission settings.");
            return SettingsRefusal;
        }

        if (this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, command + where, "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (verdict == PermissionVerdict.Ask
            || (this.Mode == AgentMode.Coding && verdict != PermissionVerdict.Allow && !CommandRules.IsAllowed(this.RepoRoot, command, viaShell: true)))
        {
            sink.SetStatus("Waiting for your approval…");
            string? rule = verdict == PermissionVerdict.Ask ? null : CommandRules.RuleFor(command, viaShell: true);
            Approval approval = (await sink.RequestApprovalAsync(ActivityKind.Shell, $"Run: {command}{where}", string.Empty, rule is null ? null : $"Always allow \"{rule}\" here", cancellationToken)).Decision;
            if (approval == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, command + where, "Declined by you.");
                return "The user declined to run this command.";
            }

            if (approval == Approval.AllowAll && rule != null)
            {
                CommandRules.Allow(this.RepoRoot, rule);
            }
        }

        (int? exitCode, string output, bool stoppedByUser) = await this.RunWithLiveRowAsync(directory, command, command + where, timeout, sink, cancellationToken);
        string shown = output.Length == 0 ? "(no output)" : output;
        if (stoppedByUser)
        {
            sink.ToolActivity(ActivityKind.Declined, command + where, $"Stopped by you.\n{output}".TrimEnd());
            return $"The user stopped this command before it finished. Don't run it again unless they ask.\n{output}".TrimEnd();
        }

        if (exitCode is not int code)
        {
            sink.ToolActivity(ActivityKind.Failed, command + where, $"Stopped after {timeout}s.\n{output}".TrimEnd());
            return $"The command was stopped after {timeout} seconds. Pass a larger timeout_seconds (up to {MaxCommandSeconds}) if it needs longer.\n{output}".TrimEnd();
        }

        sink.ToolActivity(code == 0 ? ActivityKind.Shell : ActivityKind.Failed, command + where, code == 0 ? shown : $"Exit code {code}\n{shown}");
        return code == 0 ? shown : $"Exit code {code}.\n{shown}";
    }

    /// <summary>
    /// Runs a command with a row in the transcript that shows its latest output and a Stop button. Returns how it
    /// ended: the exit code (null if it timed out), everything it wrote, and whether the user stopped it.
    /// </summary>
    private async Task<(int? ExitCode, string Output, bool StoppedByUser)> RunWithLiveRowAsync(
        string directory, string command, string title, int timeoutSeconds, IChatSink sink, CancellationToken cancellationToken)
    {
        sink.SetStatus($"Running {title}");
        using var stop = new CancellationTokenSource();
        using ILiveActivity live = sink.StartLiveActivity(ActivityKind.Shell, title, stop.Cancel);

        // The row shows the end of the output as it arrives, refreshed a few times a second.
        var tail = new OutputTail(LiveOutputLines);
        Task<(int? ExitCode, string Output)> run = ShellRunner.RunAsync(directory, command, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, stop.Token, tail.Add);
        while (await Task.WhenAny(run, Task.Delay(LiveOutputInterval)) != run)
        {
            live.SetOutput(tail.Text);
        }

        (int? exitCode, string output) = await run;
        return (exitCode, output, stop.IsCancellationRequested);
    }

    private static readonly string[] TestTargetExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".dll", ".exe"];

    /// <summary>
    /// The run_tests tool: runs the tests with <c>dotnet test</c> (or, for a test assembly, Visual Studio's own
    /// <c>vstest.console</c>, which also runs MSTest and native C++ tests that <c>dotnet test</c> can't) and reports the
    /// counts and each failure from the TRX results file, instead of pages of console output.
    /// </summary>
    private async Task<string> RunTestsAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string target = input.TryGetValue("target", out JsonElement targetEl) && targetEl.ValueKind == JsonValueKind.String ? targetEl.GetString()!.Trim() : string.Empty;
        string filter = input.TryGetValue("filter", out JsonElement filterEl) && filterEl.ValueKind == JsonValueKind.String ? filterEl.GetString()!.Trim() : string.Empty;
        string configuration = input.TryGetValue("configuration", out JsonElement cfgEl) && cfgEl.ValueKind == JsonValueKind.String ? cfgEl.GetString()!.Trim() : string.Empty;
        bool noBuild = input.TryGetValue("no_build", out JsonElement noBuildEl) && noBuildEl.ValueKind == JsonValueKind.True;
        int timeout = Math.Max(1, Math.Min(MaxCommandSeconds, OptionalInt(input, "timeout_seconds") ?? DefaultTestSeconds));

        string fullTarget = this.RepoRoot;
        if (target.Length > 0 && target != ".")
        {
            fullTarget = this.files.Resolve(target);
            bool isFolder = Directory.Exists(fullTarget);
            if (!isFolder && !File.Exists(fullTarget))
            {
                throw new ToolInputException($"Target not found: {target}");
            }

            if (!isFolder && !TestTargetExtensions.Contains(Path.GetExtension(fullTarget), StringComparer.OrdinalIgnoreCase))
            {
                throw new ToolInputException("`target` must be a solution, a project, a folder or a test assembly (.dll).");
            }
        }

        // Values end up in a PowerShell command line, so they're quoted rather than trusted.
        if (filter.IndexOfAny(['\r', '\n']) >= 0 || configuration.IndexOfAny([' ', '\r', '\n', '\'', '"', ';', '&', '|']) >= 0)
        {
            throw new ToolInputException("`filter` and `configuration` can't contain line breaks, and `configuration` must be a plain name such as Debug or Release.");
        }

        string resultsDirectory = Path.Combine(Path.GetTempPath(), "ClaudeForVS", "tests-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        const string TrxName = "results.trx";
        bool assembly = File.Exists(fullTarget) && Path.GetExtension(fullTarget).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        bool exe = File.Exists(fullTarget) && Path.GetExtension(fullTarget).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        string label = target.Length > 0 ? target : "the repository";
        string command;
        if (assembly || exe)
        {
            string? vstest = await Task.Run(TestRunner.FindVsTest);
            if (vstest is null)
            {
                throw new ToolInputException("Visual Studio's vstest.console.exe wasn't found, so a test assembly can't be run directly. Pass the test project instead.");
            }

            command = $"& {TestResults.PsQuote(vstest)} {TestResults.PsQuote(fullTarget)} {TestResults.PsQuote("/Logger:trx;LogFileName=" + TrxName)} {TestResults.PsQuote("/ResultsDirectory:" + resultsDirectory)}"
                + (filter.Length > 0 ? " " + TestResults.PsQuote("/TestCaseFilter:" + filter) : string.Empty);
        }
        else
        {
            command = "dotnet test" + (fullTarget.Equals(this.RepoRoot, StringComparison.OrdinalIgnoreCase) ? string.Empty : " " + TestResults.PsQuote(fullTarget))
                + $" --logger {TestResults.PsQuote("trx;LogFileName=" + TrxName)} --results-directory {TestResults.PsQuote(resultsDirectory)}"
                + (filter.Length > 0 ? $" --filter {TestResults.PsQuote(filter)}" : string.Empty)
                + (configuration.Length > 0 ? $" -c {configuration}" : string.Empty)
                + (noBuild ? " --no-build" : string.Empty);
        }

        string title = $"Run tests: {label}{(filter.Length > 0 ? $" ({filter})" : string.Empty)}";
        PermissionVerdict verdict = this.permissions.ForCommand(command);
        if (verdict == PermissionVerdict.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Denied by your permission settings.");
            return SettingsRefusal;
        }

        if (this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (verdict == PermissionVerdict.Ask
            || (this.Mode == AgentMode.Coding && verdict != PermissionVerdict.Allow && !CommandRules.IsToolAllowed(this.RepoRoot, "run_tests")))
        {
            sink.SetStatus("Waiting for your approval…");
            ApprovalResult approval = await sink.RequestApprovalAsync(
                ActivityKind.Shell, title, string.Empty, verdict == PermissionVerdict.Ask ? null : "Always allow running tests here", cancellationToken);
            if (approval.Decision == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, title, "Declined by you.");
                return "The user declined to run the tests.";
            }

            if (approval.Decision == Approval.AllowAll)
            {
                CommandRules.Allow(this.RepoRoot, "run_tests");
            }
        }

        try
        {
            Directory.CreateDirectory(resultsDirectory);
            (int? exitCode, string output, bool stoppedByUser) = await this.RunWithLiveRowAsync(this.RepoRoot, command, title, timeout, sink, cancellationToken);
            if (stoppedByUser)
            {
                sink.ToolActivity(ActivityKind.Declined, title, "Stopped by you.");
                return "The user stopped the test run before it finished. Don't run the tests again unless they ask.";
            }

            if (exitCode is null)
            {
                sink.ToolActivity(ActivityKind.Failed, title, $"Stopped after {timeout}s.");
                return $"The test run was stopped after {timeout} seconds. Pass a larger timeout_seconds (up to {MaxCommandSeconds}), or narrow it with `filter`.";
            }

            string trxPath = Path.Combine(resultsDirectory, TrxName);
            TestRunSummary? summary = File.Exists(trxPath) ? TestResults.Parse(File.ReadAllText(trxPath)) : null;
            if (summary is null)
            {
                // No results file: most likely it didn't build, or the target has no tests.
                string text = output.Length == 0 ? "(no output)" : output;
                sink.ToolActivity(ActivityKind.Failed, title, $"No test results were produced (exit code {exitCode}).\n{text}");
                return $"No test results were produced (exit code {exitCode}); the build probably failed or the target has no tests. Output:\n{text}";
            }

            string report = TestResults.Format(summary, this.RepoRoot);
            sink.ToolActivity(summary.Failed == 0 && summary.Total > 0 ? ActivityKind.Shell : ActivityKind.Failed, title, report);
            return report;
        }
        finally
        {
            try
            {
                Directory.Delete(resultsDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The latest lines of a running command's output. Lines arrive on background threads.</summary>
    private sealed class OutputTail(int maxLines)
    {
        private readonly Queue<string> lines = new();

        public string Text
        {
            get
            {
                lock (this.lines)
                {
                    return string.Join("\n", this.lines);
                }
            }
        }

        public void Add(string line)
        {
            lock (this.lines)
            {
                this.lines.Enqueue(line);
                while (this.lines.Count > maxLines)
                {
                    this.lines.Dequeue();
                }
            }
        }
    }
}

/// <summary>Finds Visual Studio's test runner.</summary>
internal static class TestRunner
{
    private const string RelativePath = @"Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe";

    /// <summary>The <c>vstest.console.exe</c> of the running Visual Studio, or failing that the newest installed one; null if there's none.</summary>
    public static string? FindVsTest()
    {
        try
        {
            if (Process.GetCurrentProcess().MainModule?.FileName is string exe
                && Path.GetFileName(exe).Equals("devenv.exe", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(exe) is string ide)
            {
                string beside = Path.Combine(ide, @"CommonExtensions\Microsoft\TestWindow\vstest.console.exe");
                if (File.Exists(beside))
                {
                    return beside;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        string vswhere = programFiles is null ? string.Empty : Path.Combine(programFiles, @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(vswhere, $"-latest -products * -find \"{RelativePath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
