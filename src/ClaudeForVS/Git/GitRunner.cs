using System.Diagnostics;
using System.Text;

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
            "remote" => rest.Length == 0 || rest.All(a => a is "-v" or "--verbose") || (rest[0] == "show" || rest[0] == "get-url"),
            "tag" => rest.Length == 0 || rest.All(a => a is "-l" or "--list" || a.StartsWith("-n")),
            "stash" => rest.Length > 0 && (rest[0] == "list" || rest[0] == "show"),
            "config" => rest.Length > 0 && (rest[0] is "--get" or "--get-all" or "--list" or "-l"),
            _ => false,
        };
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(string repoRoot, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string git = GitPath ?? throw new InvalidOperationException("git.exe was not found on PATH or in the Visual Studio installation.");

        var psi = new ProcessStartInfo(git)
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
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_EDITOR"] = "true";
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("color.ui=never");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        process.StandardInput.Close();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string output = (await stdout) + (await stderr is { Length: > 0 } err ? "\n[stderr]\n" + err : string.Empty);
        if (output.Length > MaxOutputChars)
        {
            output = output[..MaxOutputChars] + $"\n[output truncated: {output.Length - MaxOutputChars} more characters. Narrow the command, e.g. with a path or -n.]";
        }

        return (process.ExitCode, output);
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory containing .git.</summary>
    public static string? FindRepoRoot(string? start)
    {
        for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
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

        // Visual Studio ships its own git under Team Explorer.
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Visual Studio\Installer\vswhere.exe");
        var roots = new List<string>();
        if (File.Exists(vswhere))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(vswhere, "-all -prerelease -property installationPath")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                roots.AddRange(p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                p.WaitForExit();
            }
            catch
            {
            }
        }

        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, @"Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
