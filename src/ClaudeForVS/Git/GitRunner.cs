using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Git;

/// <summary>Runs git.exe against a repository. Uses git on PATH, falling back to the copy bundled with Visual Studio.</summary>
internal sealed class GitRunner
{
    private const int MaxOutputChars = 60_000;

    // Subcommands Claude may run without asking. Everything else needs user approval.
    private static readonly HashSet<string> ReadOnlySubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "blame", "rev-parse", "ls-files", "grep",
        "describe", "shortlog", "reflog", "cat-file", "ls-tree", "merge-base", "rev-list", "whatchanged",
    };

    private static readonly Lazy<string?> GitExe = new(FindGit);

    public static string? GitPath => GitExe.Value;

    /// <summary>True if running these args can't modify the repo or remotes.</summary>
    public static bool IsReadOnly(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return true;
        }

        string sub = args[0];
        if (ReadOnlySubcommands.Contains(sub))
        {
            // "git diff --output=x" writes files; "git log/show --output" too.
            return !args.Any(a => a.StartsWith("--output", StringComparison.OrdinalIgnoreCase));
        }

        // Listing forms of commands that can also mutate.
        string[] rest = args.Skip(1).ToArray();
        return sub.ToLowerInvariant() switch
        {
            "branch" => rest.All(a => a is "-a" or "-r" or "-v" or "-vv" or "--all" or "--list" or "--show-current" or "--merged" or "--no-merged" || a.StartsWith("--format")),
            "remote" => rest.Length == 0 || rest.All(a => a is "-v" or "--verbose") || rest[0] == "show" || rest[0] == "get-url",
            "tag" => rest.Length == 0 || rest.All(a => a is "-l" or "--list" || a.StartsWith("-n")),
            "stash" => rest.Length > 0 && (rest[0] == "list" || rest[0] == "show"),
            "config" => rest.Length > 0 && rest[0] is "--get" or "--get-all" or "--list" or "-l",
            _ => false,
        };
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(string repoRoot, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string git = GitPath ?? throw new InvalidOperationException("git.exe was not found on PATH or in the Visual Studio installation.");

        var allArgs = new List<string> { "-c", "color.ui=never", "-c", "core.quotepath=off" };
        allArgs.AddRange(args);

        var psi = new ProcessStartInfo(git, string.Join(" ", allArgs.Select(QuoteArgument)))
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Never page, prompt or open an editor - there is no terminal to answer.
        psi.EnvironmentVariables["GIT_PAGER"] = "cat";
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GIT_EDITOR"] = "true";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();
        process.StandardInput.Close();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using (cancellationToken.Register(() =>
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            exited.TrySetCanceled();
        }))
        {
            await exited.Task.ConfigureAwait(false);
        }

        string output = await stdout.ConfigureAwait(false);
        string err = await stderr.ConfigureAwait(false);
        if (err.Length > 0)
        {
            output += "\n[stderr]\n" + err;
        }

        if (output.Length > MaxOutputChars)
        {
            output = output.Substring(0, MaxOutputChars) + $"\n[output truncated: {output.Length - MaxOutputChars} more characters. Narrow the command, e.g. with a path or -n.]";
        }

        return (process.ExitCode, output);
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory containing .git.</summary>
    public static string? FindRepoRoot(string? start)
    {
        if (string.IsNullOrWhiteSpace(start))
        {
            return null;
        }

        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(start);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (; dir != null; dir = dir.Parent)
        {
            string dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>Quotes one argument using the rules CreateProcess/MSVCRT use to split a command line.</summary>
    internal static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }

            backslashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static string? FindGit()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "git.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        // We're running inside devenv.exe, so look relative to it first.
        string? ideDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
        if (ideDir != null)
        {
            string candidate = Path.Combine(ideDir, @"CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
