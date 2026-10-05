using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>
/// After Claude changes code, formats just the lines it wrote the way the user's editor settings (.editorconfig) say,
/// so the result matches the rest of the file without reformatting what Claude didn't touch.
/// </summary>
internal static class ChangeFormatter
{
    private static readonly TimeSpan DefaultSyncWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SyncStep = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Formats the changed lines of each file in <paramref name="changes"/> (a file changed several times counts once,
    /// from how it began to how it ended). A file is only touched while it's exactly as Claude left it: if it's gone,
    /// has unsaved edits in Visual Studio or has changed since, it's left alone. Returns the formatted files, as
    /// repository-relative paths.
    /// </summary>
    /// <param name="hasUnsavedChanges">Whether a file has unsaved changes in an editor. Called on the caller's thread.</param>
    /// <param name="syncWait">How long to wait for Visual Studio to catch up with a file Claude has only just written; it follows file changes a moment later.</param>
    public static async Task<IReadOnlyList<string>> FormatAsync(
        IReadOnlyList<FileChange> changes, string repoRoot, ICodeIntelligence code, Func<string, bool> hasUnsavedChanges, CancellationToken cancellationToken, TimeSpan? syncWait = null)
    {
        DateTime deadline = DateTime.UtcNow + (syncWait ?? DefaultSyncWait); // one wait for all the files, not one each
        var files = new RepoFiles(repoRoot);
        var formatted = new List<string>();
        foreach (IGrouping<string, FileChange> group in changes.GroupBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            FileChange first = group.First();
            FileChange last = group.Last();
            string path = first.FullPath;
            if (!File.Exists(path) || hasUnsavedChanges(path))
            {
                continue;
            }

            RepoFiles.TextFile onDisk;
            string relative;
            try
            {
                relative = Relative(repoRoot, path) ?? throw new ToolInputException("outside the repository");
                onDisk = files.Read(relative);
            }
            catch (ToolInputException)
            {
                continue; // outside the repository, binary, or reached through a link
            }

            if (!Same(onDisk.Text, last.After))
            {
                continue;
            }

            IReadOnlyList<(int Start, int End)> lines = UnifiedDiff.ChangedLines(first.Before, last.After);
            if (lines.Count == 0)
            {
                continue;
            }

            FormatResult result = await code.FormatAsync(path, onDisk.Text, lines, cancellationToken);
            while (result.Status == FormatStatus.OutOfDate && DateTime.UtcNow < deadline)
            {
                await Task.Delay(SyncStep, cancellationToken);
                result = await code.FormatAsync(path, onDisk.Text, lines, cancellationToken);
            }

            if (result.Status != FormatStatus.Formatted || result.Text is not string text)
            {
                continue;
            }

            // Formatting took a moment; make sure nothing else changed the file meanwhile.
            if (hasUnsavedChanges(path) || !Same(files.Read(relative).Text, onDisk.Text))
            {
                continue;
            }

            files.Write(path, text, onDisk.Encoding);
            formatted.Add(relative.Replace('\\', '/'));
        }

        return formatted;
    }

    private static bool Same(string a, string b) => string.Equals(a.Replace("\r\n", "\n"), b.Replace("\r\n", "\n"), StringComparison.Ordinal);

    private static string? Relative(string repoRoot, string fullPath)
    {
        string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : null;
    }
}
