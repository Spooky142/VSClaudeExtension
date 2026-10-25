using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>How a build in Visual Studio went.</summary>
/// <param name="Succeeded">False if any project failed to build.</param>
/// <param name="Summary">One line, e.g. "Build succeeded: 4 projects" or "Build failed: 1 of 4 projects".</param>
internal sealed record BuildOutcome(bool Succeeded, string Summary);

/// <summary>What the engines can ask Visual Studio itself for. Implemented by the package; every member switches to the UI thread as needed.</summary>
internal interface IVisualStudioHost
{
    /// <summary>The directory of the open solution or folder, or null if none is open.</summary>
    Task<string?> GetSolutionDirectoryAsync(CancellationToken cancellationToken);

    /// <summary>Everything in the Error List: build errors and the live (IntelliSense) errors in open files.</summary>
    Task<IReadOnlyList<ErrorListItem>> GetDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>Builds the open solution the way Build > Build Solution does, and waits for it to finish.</summary>
    Task<BuildOutcome> BuildSolutionAsync(CancellationToken cancellationToken);

    /// <summary>Whether the debugger is paused, and if so the exception, call stack and locals.</summary>
    Task<DebugState> GetDebugStateAsync(CancellationToken cancellationToken);

    /// <summary>The document the user last worked in, where the caret is and what's selected; null when no text document is open.</summary>
    Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken cancellationToken);
}

/// <summary>The document in the code editor and where the user is in it.</summary>
/// <param name="CaretLine">1-based, like the other lines here.</param>
/// <param name="FirstVisibleLine">The first line scrolled into view; 0 if unknown.</param>
/// <param name="SelectedText">Empty when nothing is selected.</param>
/// <param name="OtherOpenFiles">The other documents open in the editor, and whether each has unsaved changes.</param>
internal sealed record ActiveDocument(
    string FullPath,
    bool HasUnsavedChanges,
    int CaretLine,
    int CaretColumn,
    int FirstVisibleLine,
    int LastVisibleLine,
    int SelectionStartLine,
    int SelectionEndLine,
    string SelectedText,
    IReadOnlyList<(string FullPath, bool Unsaved)> OtherOpenFiles);

/// <summary>Formats the active document for Claude.</summary>
internal static class ActiveDocumentReport
{
    public const int MaxSelectionChars = 8_000;
    private const int MaxOtherFiles = 30;

    /// <param name="canShow">Whether a file's text may be shown (the user's permission rules); the selection is left out otherwise.</param>
    public static string Format(ActiveDocument? document, string repoRoot, Func<string, bool> canShow)
    {
        if (document is null)
        {
            return "No text document is open in Visual Studio's editor.";
        }

        var sb = new StringBuilder();
        sb.Append($"Active document: {Shown(document.FullPath, repoRoot)}{(document.HasUnsavedChanges ? " (unsaved changes)" : string.Empty)}\n");
        sb.Append($"Caret: line {document.CaretLine}, column {document.CaretColumn}\n");
        if (document.FirstVisibleLine > 0 && document.LastVisibleLine >= document.FirstVisibleLine)
        {
            sb.Append($"Visible in the editor: lines {document.FirstVisibleLine}-{document.LastVisibleLine}\n");
        }

        if (document.SelectedText.Trim().Length > 0)
        {
            string lines = document.SelectionStartLine == document.SelectionEndLine
                ? $"line {document.SelectionStartLine}"
                : $"lines {document.SelectionStartLine}-{document.SelectionEndLine}";
            if (!canShow(document.FullPath))
            {
                sb.Append($"Selection: {lines} (its text is left out: reading this file is restricted by your permission settings)\n");
            }
            else
            {
                string text = document.SelectedText.Replace("\r\n", "\n");
                bool cut = text.Length > MaxSelectionChars;
                sb.Append($"Selection: {lines}:\n```\n{(cut ? text.Substring(0, MaxSelectionChars) : text).TrimEnd('\n')}\n```\n");
                if (cut)
                {
                    sb.Append($"[The selection is {text.Length:N0} characters; only the first {MaxSelectionChars:N0} are shown. Read the file for the rest.]\n");
                }
            }
        }
        else
        {
            sb.Append("Nothing is selected.\n");
        }

        List<(string FullPath, bool Unsaved)> others = document.OtherOpenFiles
            .Where(f => !string.Equals(f.FullPath, document.FullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count > 0)
        {
            sb.Append("Also open: ").Append(string.Join(", ", others.Take(MaxOtherFiles).Select(f => Shown(f.FullPath, repoRoot) + (f.Unsaved ? " (unsaved)" : string.Empty))));
            if (others.Count > MaxOtherFiles)
            {
                sb.Append($" and {others.Count - MaxOtherFiles} more");
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>A path relative to the repository with forward slashes, or the full path for files outside it.</summary>
    private static string Shown(string fullPath, string repoRoot)
    {
        string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : fullPath).Replace('\\', '/');
    }
}

/// <summary>Where the engines find Visual Studio. Null outside Visual Studio (e.g. in tests).</summary>
internal static class VisualStudioHost
{
    public static IVisualStudioHost? Current { get; set; }
}

/// <summary>Formats Error List rows for Claude.</summary>
internal static class Diagnostics
{
    public const int MaxItems = 200;

    /// <param name="scope">A repository-relative file or folder to limit the list to; empty for everything.</param>
    /// <param name="includeWarnings">False to list errors only.</param>
    public static string Format(IReadOnlyList<ErrorListItem> items, string repoRoot, string scope, bool includeWarnings)
    {
        string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string prefix = scope.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);

        List<ErrorListItem> shown = items
            .Where(i => includeWarnings ? i.Severity is "error" or "warning" : i.Severity == "error")
            .Where(i => prefix.Length == 0 || (i.File is string f && Relative(f, root) is string rel
                && (rel.Equals(prefix, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            .Distinct()
            .OrderBy(i => i.Severity == "error" ? 0 : 1)
            .ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Line)
            .ToList();

        int errors = shown.Count(i => i.Severity == "error");
        int warnings = shown.Count - errors;
        string where = prefix.Length > 0 ? $" in {scope}" : string.Empty;
        if (shown.Count == 0)
        {
            return includeWarnings ? $"No errors or warnings{where}." : $"No errors{where}.";
        }

        var sb = new StringBuilder();
        sb.Append($"{Count(errors, "error")}{(includeWarnings ? ", " + Count(warnings, "warning") : string.Empty)}{where}:\n");
        foreach (ErrorListItem item in shown.Take(MaxItems))
        {
            string file = item.File is string path ? (Relative(path, root) ?? path).Replace('\\', '/') : "(no file)";
            string location = item.Line > 0 ? $"{file}:{item.Line}" : file;
            string code = item.Code.Length > 0 ? $" {item.Code}" : string.Empty;
            string project = string.IsNullOrEmpty(item.Project) ? string.Empty : $" [{item.Project}]";
            string source = string.IsNullOrEmpty(item.Source) ? string.Empty : $" ({item.Source})";
            sb.Append($"{location}: {item.Severity}{code}: {item.Description}{project}{source}\n");
        }

        if (shown.Count > MaxItems)
        {
            sb.Append($"[{shown.Count - MaxItems} more not shown; pass a path to narrow the list.]\n");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string? Relative(string fullPath, string root) =>
        fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : null;
}
