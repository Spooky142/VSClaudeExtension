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
