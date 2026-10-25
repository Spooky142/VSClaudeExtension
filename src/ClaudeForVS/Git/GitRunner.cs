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
    private const int MaxOutputChars = 30_000;

    // Subcommands Claude may run without asking. Everything else needs user approval.
    private static readonly HashSet<string> ReadOnlySubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "blame", "rev-parse", "ls-files", "grep",
        "describe", "shortlog", "cat-file", "ls-tree", "merge-base", "rev-list", "whatchanged",
    };

    // Options that make an otherwise read-only command write a file (--output), run a program (grep's
    // --open-files-in-pager, --ext-diff) or read files outside the repository (--no-index, blame's --contents and
    // --ignore-revs-file, config's --file and --blob).
    private static readonly string[] UnsafeLongOptions =
    [
        "--output", "--open-files-in-pager", "--ext-diff", "--no-index", "--contents", "--ignore-revs-file", "--file", "--blob",
    ];

    private static readonly Lazy<string?> GitExe = new(FindGit);

    public static string? GitPath => GitExe.Value;

    /// <summary>True if running these args can't modify the repo or remotes, run other programs or read files outside the repository.</summary>
    public static bool IsReadOnly(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return true;
        }

        string sub = args[0];
        if (args.Skip(1).TakeWhile(a => a != "--").Any(a => IsUnsafeOption(sub, a)))
        {
            return false;
        }

        if (ReadOnlySubcommands.Contains(sub))
        {
            return true;
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
            "reflog" => rest.Length == 0 || rest[0] is "show" or "exists" or "list" || rest[0].StartsWith("-", StringComparison.Ordinal), // not expire, delete or drop
            _ => false,
        };
    }

    private static bool IsUnsafeOption(string subcommand, string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            // Git accepts any unambiguous prefix of a long option, e.g. "--open" for --open-files-in-pager.
            string name = arg.Split('=')[0];
            return name.Length > 2 && UnsafeLongOptions.Any(option => option.StartsWith(name, StringComparison.Ordinal));
        }

        if (arg.Length < 2 || arg[0] != '-')
        {
            return false;
        }

        // Short options can be bundled ("-nOcalc" is -n plus -O calc), so look for the letter anywhere in the cluster:
        // grep's -O (run a pager) and -f (patterns from a file), blame's -S (revisions from a file), config's -f (a file).
        string flags = arg.Substring(1);
        return subcommand.ToLowerInvariant() switch
        {
            "grep" => flags.IndexOfAny(['O', 'f']) >= 0,
            "blame" => flags.IndexOf('S') >= 0,
            "config" => flags.IndexOf('f') >= 0,
            _ => false,
        };
    }

    /// <summary>How long git gets unless the caller says otherwise; a command still running then is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>For commands that can rightly take a while: talking to a remote, or hashing a whole large working tree.</summary>
    public static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(10);

    // Exit code returned when git was stopped for taking longer than its timeout.
    public const int TimedOutExitCode = -1;

    // Shown output past this many times the cap isn't worth waiting for, so a read-only command is stopped there.
    private const int StopAtCapMultiple = 4;

    /// <param name="environment">Extra environment variables for git, e.g. GIT_INDEX_FILE.</param>
    /// <param name="includeStderr">
    /// False to return only standard output (for output that's parsed rather than shown), all of it. True returns
    /// both, cut to <see cref="MaxOutputChars"/>.
    /// </param>
    /// <param name="timeout">How long git may run (default <see cref="DefaultTimeout"/>); it then returns <see cref="TimedOutExitCode"/>.</param>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string repoRoot,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        bool includeStderr = true,
        TimeSpan? timeout = null)
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
        foreach (KeyValuePair<string, string> variable in environment ?? new Dictionary<string, string>())
        {
            psi.EnvironmentVariables[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();
        Processes.CloseWithVisualStudio(process);
        process.StandardInput.Close();

        // Shown output is only kept up to the cap. Past several times that, a read-only command (a huge log or diff) is
        // stopped rather than left to run on; anything else is drained to the end, since stopping it part way through
        // could leave the repository half changed.
        bool stoppedForSize = false;
        void TooMuch()
        {
            if (!stoppedForSize && IsReadOnly(args))
            {
                stoppedForSize = true;
                Processes.Kill(process);
            }
        }

        int keep = includeStderr ? MaxOutputChars : int.MaxValue;
        Task<(string Text, long Total)> stdout = ReadCappedAsync(process.StandardOutput, keep, TooMuch);
        Task<(string Text, long Total)> stderr = ReadCappedAsync(process.StandardError, keep, TooMuch);

        bool timedOut = false;
        using var timer = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using (cancellationToken.Register(() =>
        {
            Processes.Kill(process);
            exited.TrySetCanceled();
        }))
        using (timer.Token.Register(() =>
        {
            if (!exited.Task.IsCompleted)
            {
                timedOut = true;
                _ = Task.Run(() => Processes.KillTree(process)); // a fetch or push has helpers (git-remote-https) holding the pipes
            }
        }))
        {
            await exited.Task.ConfigureAwait(false);
        }

        (string output, long outputChars) = await stdout.ConfigureAwait(false);
        (string err, long errChars) = await stderr.ConfigureAwait(false);
        int exitCode = timedOut ? TimedOutExitCode : stoppedForSize ? 0 : process.ExitCode;
        if (!includeStderr)
        {
            return (exitCode, output);
        }

        if (err.Length > 0)
        {
            output += "\n[stderr]\n" + err;
        }

        output = Truncate(output, outputChars + errChars, stoppedForSize);
        if (timedOut)
        {
            output += $"\n[git was stopped after {(timeout ?? DefaultTimeout).TotalSeconds:0} seconds.]";
        }

        return (exitCode, output);
    }

    /// <summary>Cuts shown output to <see cref="MaxOutputChars"/>, saying how much more there was.</summary>
    private static string Truncate(string output, long totalChars, bool stoppedEarly)
    {
        if (output.Length <= MaxOutputChars && totalChars <= MaxOutputChars)
        {
            return output;
        }

        long more = Math.Max(totalChars - MaxOutputChars, 1);
        return output.Substring(0, Math.Min(output.Length, MaxOutputChars))
            + $"\n[output truncated: {(stoppedEarly ? "at least " : string.Empty)}{more:N0} more characters. Narrow the command, e.g. with a path or -n.]";
    }

    /// <summary>
    /// Reads a stream to its end in chunks, keeping at most <paramref name="keep"/> characters, and calls
    /// <paramref name="tooMuch"/> once it has seen several times <see cref="MaxOutputChars"/> while keeping only that.
    /// Returns what was kept and how many characters there were in all. Internal for tests.
    /// </summary>
    internal static async Task<(string Text, long Total)> ReadCappedAsync(TextReader reader, int keep, Action tooMuch)
    {
        var text = new StringBuilder();
        char[] buffer = new char[8192];
        long total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (text.Length < keep)
            {
                text.Append(buffer, 0, (int)Math.Min(read, keep - text.Length));
            }

            if (keep <= MaxOutputChars && total > (long)MaxOutputChars * StopAtCapMultiple)
            {
                tooMuch();
            }
        }

        return (text.ToString(), total);
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
