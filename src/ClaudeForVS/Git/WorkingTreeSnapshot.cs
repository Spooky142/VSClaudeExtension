using System;
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

/// <summary>
/// Records the whole working tree (tracked and untracked files, minus ignored ones) as a git tree object, without
/// touching the index, branches or stash, so Claude's changes since a snapshot can be listed and undone.
/// The tree lives in the object database; git keeps unreferenced objects for at least two weeks.
/// </summary>
internal static class WorkingTreeSnapshot
{
    /// <summary>Returns the snapshot's tree hash, or null if git couldn't make one.</summary>
    public static async Task<string?> CaptureAsync(string repoRoot, CancellationToken cancellationToken)
    {
        // Stage everything into a throwaway copy of the index. Starting from the real index lets git skip
        // re-hashing files whose timestamps haven't changed.
        string tempIndex = Path.Combine(Path.GetTempPath(), "ClaudeForVS", $"index-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempIndex)!);
            (int code, string indexPath) = await GitRunner.RunAsync(repoRoot, ["rev-parse", "--git-path", "index"], cancellationToken, includeStderr: false).ConfigureAwait(false);
            string realIndex = Path.Combine(repoRoot, indexPath.Trim());
            if (code == 0 && File.Exists(realIndex))
            {
                File.Copy(realIndex, tempIndex, overwrite: true);
            }

            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };
            (code, _) = await GitRunner.RunAsync(repoRoot, ["add", "-A", "--", "."], cancellationToken, env, includeStderr: false).ConfigureAwait(false);
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
            try
            {
                File.Delete(tempIndex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
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
