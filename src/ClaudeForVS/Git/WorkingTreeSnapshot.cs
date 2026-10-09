using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Git;

/// <summary>A file that differs between two snapshots.</summary>
/// <param name="Status">'A' added since the first snapshot, 'D' deleted, 'M' modified (or changed type).</param>
/// <param name="Path">Repository-relative path with forward slashes.</param>
internal sealed record SnapshotChange(char Status, string Path);

/// <summary>A changed file with its line counts.</summary>
/// <param name="Status">'A' added, 'D' deleted or 'M' modified, as in <see cref="SnapshotChange"/>.</param>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Added">Lines added; 0 for binary files.</param>
/// <param name="Removed">Lines removed; 0 for binary files.</param>
/// <param name="Binary">True when git can't count lines in the file.</param>
internal sealed record FileStat(char Status, string Path, int Added, int Removed, bool Binary);

/// <summary>
/// Records the whole working tree (tracked and untracked files, minus ignored ones) as a git tree object, without
/// touching the index, branches or stash, so Claude's changes since a snapshot can be listed and undone.
/// The tree lives in the object database; git keeps unreferenced objects for at least two weeks.
/// </summary>
internal static class WorkingTreeSnapshot
{
    /// <summary>
    /// Untracked files bigger than this (often build output or downloads that aren't git-ignored) are left out of
    /// snapshots, rather than copied into the repository's object database before every prompt. Changes to them
    /// aren't listed or undone.
    /// </summary>
    internal const long MaxUntrackedFileBytes = 5 * 1024 * 1024;

    /// <summary>Returns the snapshot's tree hash, or null if git couldn't make one.</summary>
    public static async Task<string?> CaptureAsync(string repoRoot, CancellationToken cancellationToken)
    {
        // Stage everything into a throwaway copy of the index. Starting from the real index lets git skip
        // re-hashing files whose timestamps haven't changed.
        string tempIndex = Path.Combine(Path.GetTempPath(), "ClaudeForVS", $"index-{Guid.NewGuid():N}");
        string pathspecFile = tempIndex + ".pathspec";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempIndex)!);
            if (await IndexPathAsync(repoRoot, cancellationToken).ConfigureAwait(false) is string realIndex && File.Exists(realIndex))
            {
                File.Copy(realIndex, tempIndex, overwrite: true);
            }

            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };
            List<string> excluded = await LargeUntrackedFilesAsync(repoRoot, env, cancellationToken).ConfigureAwait(false);
            List<string> addArgs = ["add", "-A", "--", "."];
            if (excluded.Count > 0)
            {
                // Through a file, since there could be too many to fit on a command line.
                File.WriteAllText(pathspecFile, string.Join("\0", new[] { "." }.Concat(excluded.Select(p => ":(exclude,literal)" + p))));
                addArgs = ["add", "-A", $"--pathspec-from-file={pathspecFile}", "--pathspec-file-nul"];
            }

            (int code, _) = await GitRunner.RunAsync(repoRoot, addArgs, cancellationToken, env, includeStderr: false).ConfigureAwait(false);
            if (code != 0)
            {
                return null;
            }

            (code, string tree) = await GitRunner.RunAsync(repoRoot, ["write-tree"], cancellationToken, env, includeStderr: false).ConfigureAwait(false);
            tree = tree.Trim();
            return code == 0 && tree.Length >= 40 ? tree : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            foreach (string temp in new[] { tempIndex, pathspecFile })
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    // Where each repository keeps its index (it differs in worktrees), which doesn't change, so git is asked once.
    private static readonly ConcurrentDictionary<string, string> IndexPaths = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<string?> IndexPathAsync(string repoRoot, CancellationToken cancellationToken)
    {
        if (IndexPaths.TryGetValue(repoRoot, out string? known))
        {
            return known;
        }

        (int code, string indexPath) = await GitRunner.RunAsync(repoRoot, ["rev-parse", "--git-path", "index"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        if (code != 0)
        {
            return null;
        }

        return IndexPaths[repoRoot] = Path.Combine(repoRoot, indexPath.Trim());
    }

    /// <summary>Untracked, not ignored files over <see cref="MaxUntrackedFileBytes"/>, as repository-relative paths.</summary>
    private static async Task<List<string>> LargeUntrackedFilesAsync(string repoRoot, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        (int code, string output) = await GitRunner.RunAsync(
            repoRoot, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken, env, includeStderr: false).ConfigureAwait(false);
        var large = new List<string>();
        if (code != 0)
        {
            return large;
        }

        foreach (string path in output.Split(['\0'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (new FileInfo(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar))).Length > MaxUntrackedFileBytes)
                {
                    large.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Gone already, or a name Windows can't open; git will deal with it.
            }
        }

        return large;
    }

    /// <summary>Files that differ between two snapshots.</summary>
    public static async Task<IReadOnlyList<SnapshotChange>> ChangesAsync(string repoRoot, string fromTree, string toTree, CancellationToken cancellationToken)
    {
        if (fromTree == toTree)
        {
            return [];
        }

        (int code, string output) = await GitRunner.RunAsync(
            repoRoot, ["diff", "--name-status", "--no-renames", "-z", fromTree, toTree], cancellationToken, includeStderr: false).ConfigureAwait(false);
        return code == 0 ? ParseNameStatus(output) : [];
    }

    /// <summary>Files that differ between two snapshots, with lines added and removed in each.</summary>
    public static async Task<IReadOnlyList<FileStat>> StatsAsync(string repoRoot, string fromTree, string toTree, CancellationToken cancellationToken)
    {
        if (fromTree == toTree)
        {
            return [];
        }

        // Both at once; neither depends on the other.
        Task<IReadOnlyList<SnapshotChange>> changing = ChangesAsync(repoRoot, fromTree, toTree, cancellationToken);
        Task<(int ExitCode, string Output)> counting = GitRunner.RunAsync(
            repoRoot, ["diff", "--numstat", "--no-renames", "-z", fromTree, toTree], cancellationToken, includeStderr: false);
        await Task.WhenAll(changing, counting).ConfigureAwait(false);
        IReadOnlyList<SnapshotChange> changes = await changing.ConfigureAwait(false);
        (int code, string output) = await counting.ConfigureAwait(false);
        Dictionary<string, (int Added, int Removed, bool Binary)> counts = code == 0 ? ParseNumstat(output) : [];
        return changes
            .Select(c => counts.TryGetValue(c.Path, out var n) ? new FileStat(c.Status, c.Path, n.Added, n.Removed, n.Binary) : new FileStat(c.Status, c.Path, 0, 0, false))
            .ToList();
    }

    /// <summary>
    /// Parses <c>git diff --numstat --no-renames -z</c> output: "added\tremoved\tpath", each NUL-terminated, with "-"
    /// for both counts when the file is binary.
    /// </summary>
    internal static Dictionary<string, (int Added, int Removed, bool Binary)> ParseNumstat(string output)
    {
        var counts = new Dictionary<string, (int, int, bool)>(StringComparer.Ordinal);
        foreach (string record in output.Split('\0'))
        {
            string[] parts = record.Split(['\t'], 3);
            if (parts.Length != 3 || parts[2].Length == 0)
            {
                continue;
            }

            bool binary = parts[0] == "-" && parts[1] == "-";
            int.TryParse(parts[0], out int added);
            int.TryParse(parts[1], out int removed);
            counts[parts[2]] = (added, removed, binary);
        }

        return counts;
    }

    /// <summary>A file's text as it was in a snapshot, or null if it wasn't in it.</summary>
    public static async Task<string?> ReadFileAsync(string repoRoot, string tree, string path, CancellationToken cancellationToken)
    {
        (int code, string text) = await GitRunner.RunAsync(repoRoot, ["show", $"{tree}:{path}"], cancellationToken, includeStderr: false).ConfigureAwait(false);
        return code == 0 ? text.TrimStart('﻿') : null;
    }

    /// <summary>Parses <c>git diff --name-status -z</c> output: a status, then a path, each NUL-terminated.</summary>
    internal static IReadOnlyList<SnapshotChange> ParseNameStatus(string output)
    {
        var changes = new List<SnapshotChange>();
        string[] fields = output.Split('\0');
        for (int i = 0; i + 1 < fields.Length; i += 2)
        {
            if (fields[i].Length > 0 && fields[i + 1].Length > 0)
            {
                char status = fields[i][0] switch { 'A' => 'A', 'D' => 'D', _ => 'M' };
                changes.Add(new SnapshotChange(status, fields[i + 1]));
            }
        }

        return changes;
    }

    /// <summary>
    /// Puts the given files back as they were in the snapshot: changed and deleted files are restored from it and
    /// files added since are deleted. The index isn't touched.
    /// </summary>
    public static async Task<string?> RestoreAsync(string repoRoot, string tree, IReadOnlyList<SnapshotChange> changes, CancellationToken cancellationToken)
    {
        List<string> restore = changes.Where(c => c.Status != 'A').Select(c => c.Path).ToList();
        if (restore.Count > 0)
        {
            var args = new List<string> { "restore", $"--source={tree}", "--worktree", "--" };
            args.AddRange(restore);
            (int code, string output) = await GitRunner.RunAsync(repoRoot, args, cancellationToken).ConfigureAwait(false);
            if (code != 0)
            {
                return output.Trim();
            }
        }

        foreach (SnapshotChange added in changes.Where(c => c.Status == 'A'))
        {
            try
            {
                File.Delete(Path.Combine(repoRoot, added.Path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Couldn't delete {added.Path}: {ex.Message}";
            }
        }

        return null;
    }
}
