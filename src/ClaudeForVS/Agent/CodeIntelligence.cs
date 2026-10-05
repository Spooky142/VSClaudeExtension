using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>A place in a source file that names a symbol: the file, the line, and the identifier on that line.</summary>
/// <param name="Line">1-based.</param>
internal sealed record CodeSite(string FullPath, int Line, string Symbol);

/// <summary>A file a rename changes, as the text of the whole file before and after.</summary>
internal sealed record RenamedFile(string FullPath, string Before, string After);

/// <summary>How a request to format part of a file went.</summary>
internal enum FormatStatus
{
    /// <summary>The file was formatted; <see cref="FormatResult.Text"/> is its new text.</summary>
    Formatted,

    /// <summary>The lines were already as the editor settings say.</summary>
    AlreadyFormatted,

    /// <summary>Visual Studio has no code model for the file (not C# or Visual Basic, or not in the open solution).</summary>
    NotInSolution,

    /// <summary>Visual Studio's copy of the file isn't the text given: it hasn't caught up with a change on disk yet, or has been edited.</summary>
    OutOfDate,

    /// <summary>Left alone on purpose: mixed line endings, or formatting would have changed more than whitespace.</summary>
    Skipped,
}

internal sealed record FormatResult(FormatStatus Status, string? Text = null);

/// <summary>What renaming a symbol would change.</summary>
/// <param name="Symbol">The symbol as it's written in code, e.g. "Order.Total".</param>
internal sealed record RenamePlan(string Symbol, string NewName, IReadOnlyList<RenamedFile> Files);

/// <summary>
/// Visual Studio's understanding of the code (Roslyn): what a name refers to, where it's used and how to rename it.
/// Every result for Claude is worked out here, so the engines only format and approve.
/// </summary>
internal interface ICodeIntelligence
{
    /// <param name="canRead">Whether a file's text may be shown (the user's permission rules); other files are left out.</param>
    Task<string> GoToDefinitionAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, CancellationToken cancellationToken);

    Task<string> FindReferencesAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken);

    /// <param name="kind">"type", "method", "property", "field", "event", or empty for any.</param>
    Task<string> FindSymbolsAsync(string query, string kind, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken);

    /// <summary>The types and members declared in one file, with their line numbers.</summary>
    Task<string> OutlineAsync(string fullPath, string repoRoot, CancellationToken cancellationToken);

    /// <summary>Works out a rename without changing anything.</summary>
    Task<RenamePlan> PlanRenameAsync(CodeSite site, string newName, CancellationToken cancellationToken);

    /// <summary>
    /// Formats the given lines of a file the way the user's editor settings (.editorconfig) say, if Visual Studio's copy
    /// of the file is <paramref name="expectedText"/> and formatting only changes whitespace.
    /// </summary>
    Task<FormatResult> FormatAsync(string fullPath, string expectedText, IReadOnlyList<(int Start, int End)> lines, CancellationToken cancellationToken);
}

/// <summary>Where the engines find Visual Studio's code model. Null outside Visual Studio (e.g. in tests).</summary>
internal static class CodeIntelligenceHost
{
    public static ICodeIntelligence? Current { get; set; }
}

/// <summary>The code tools Claude can call, shared by both engines: reads their input and calls <see cref="ICodeIntelligence"/>.</summary>
internal static class CodeTools
{
    public const int DefaultResults = 100;
    public const int MaxResults = 500;

    private static readonly TimeSpan DefaultSyncWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SyncStep = TimeSpan.FromMilliseconds(250);

    private static ICodeIntelligence Service =>
        CodeIntelligenceHost.Current ?? throw new ToolInputException("Code navigation needs Visual Studio with a solution open, so it isn't available here.");

    public static Task<string> GoToDefinitionAsync(IReadOnlyDictionary<string, JsonElement> input, RepoFiles files, string repoRoot, Func<string, bool> canRead, CancellationToken cancellationToken)
        => Service.GoToDefinitionAsync(ParseSite(input, files), repoRoot, canRead, cancellationToken);

    public static Task<string> FindReferencesAsync(IReadOnlyDictionary<string, JsonElement> input, RepoFiles files, string repoRoot, Func<string, bool> canRead, CancellationToken cancellationToken)
        => Service.FindReferencesAsync(ParseSite(input, files), repoRoot, canRead, Limit(input), cancellationToken);

    public static Task<string> FindSymbolsAsync(IReadOnlyDictionary<string, JsonElement> input, RepoFiles files, string repoRoot, Func<string, bool> canRead, CancellationToken cancellationToken)
    {
        string query = Text(input, "query");
        string path = Text(input, "path");
        if (query.Length == 0 && path.Length == 0)
        {
            throw new ToolInputException("Give a `query` to search the solution for, or a `path` to list the symbols declared in one file.");
        }

        if (query.Length == 0)
        {
            string full = files.Resolve(path);
            if (!File.Exists(full))
            {
                throw new ToolInputException($"File not found: {path}");
            }

            return Service.OutlineAsync(full, repoRoot, cancellationToken);
        }

        string kind = Text(input, "kind").ToLowerInvariant();
        if (kind is not ("" or "any" or "type" or "method" or "property" or "field" or "event"))
        {
            throw new ToolInputException("`kind` must be \"type\", \"method\", \"property\", \"field\", \"event\" or \"any\".");
        }

        return Service.FindSymbolsAsync(query, kind == "any" ? string.Empty : kind, repoRoot, canRead, Limit(input), cancellationToken);
    }

    /// <summary>Works out a rename and checks that it only touches repository files that are as Visual Studio sees them.</summary>
    /// <param name="syncWait">How long to wait for Visual Studio to catch up with a file that changed on disk a moment ago (it follows changes a little later); tests pass a short one.</param>
    public static async Task<RenamePlan> PlanRenameAsync(
        IReadOnlyDictionary<string, JsonElement> input, RepoFiles files, string repoRoot, CancellationToken cancellationToken, TimeSpan? syncWait = null)
    {
        string newName = Text(input, "new_name").Trim();
        CodeSite site = ParseSite(input, files);
        DateTime deadline = DateTime.UtcNow + (syncWait ?? DefaultSyncWait);
        while (true)
        {
            RenamePlan plan = await Service.PlanRenameAsync(site, newName, cancellationToken);
            string? stale = null;
            foreach (RenamedFile file in plan.Files)
            {
                string relative = RelativeTo(repoRoot, file.FullPath) ?? throw new ToolInputException(
                    $"Renaming {plan.Symbol} also changes {file.FullPath}, which is outside this repository, so nothing was renamed.");
                RepoFiles.TextFile onDisk = files.Read(relative); // refuses .git and links that lead elsewhere
                if (!string.Equals(onDisk.Text.Replace("\r\n", "\n"), file.Before.Replace("\r\n", "\n"), StringComparison.Ordinal))
                {
                    stale = relative;
                    break;
                }
            }

            if (stale is null)
            {
                return plan;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new ToolInputException(
                    $"Visual Studio's copy of {stale} is out of date with the file on disk, so nothing was renamed. Wait a moment, or save the file in Visual Studio, and try again.");
            }

            await Task.Delay(SyncStep, cancellationToken);
        }
    }

    /// <summary>
    /// Writes an approved rename. Every file is checked again first (the user took their time deciding, and may have
    /// edited one), and nothing is written unless all of them still match. Call on the UI thread, which the unsaved-changes check needs.
    /// </summary>
    public static IReadOnlyList<FileChange> ApplyRename(RenamePlan plan, RepoFiles files, string repoRoot)
    {
        var pending = new List<(RepoFiles.TextFile File, string After)>();
        foreach (RenamedFile file in plan.Files)
        {
            string relative = RelativeTo(repoRoot, file.FullPath) ?? throw new ToolInputException($"{file.FullPath} is outside this repository, so nothing was renamed.");
            if (OpenDocuments.HasUnsavedChanges(file.FullPath))
            {
                throw new ToolInputException($"{relative} has unsaved changes in Visual Studio, so nothing was renamed. Ask the user to save or discard them, then try again.");
            }

            RepoFiles.TextFile onDisk = files.Read(relative);
            if (!string.Equals(onDisk.Text.Replace("\r\n", "\n"), file.Before.Replace("\r\n", "\n"), StringComparison.Ordinal))
            {
                throw new ToolInputException($"{relative} changed while waiting for approval, so nothing was renamed. Plan the rename again.");
            }

            pending.Add((onDisk, onDisk.Text == file.Before ? file.After : RepoFiles.NormalizeNewlines(file.After, onDisk.Newline)));
        }

        var changes = new List<FileChange>();
        foreach ((RepoFiles.TextFile file, string after) in pending)
        {
            files.Write(file.FullPath, after, file.Encoding);
            changes.Add(new FileChange(file.FullPath, file.Text, after));
        }

        return changes;
    }

    /// <summary>The whole rename as one preview for the approval card: each file's name, then its diff.</summary>
    public static string Describe(RenamePlan plan, string repoRoot)
    {
        var sb = new StringBuilder();
        foreach (RenamedFile file in plan.Files)
        {
            sb.Append(RelativeTo(repoRoot, file.FullPath)?.Replace('\\', '/') ?? file.FullPath).Append('\n');
            sb.Append(UnifiedDiff.Create(file.Before, file.After)).Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    public static string Title(RenamePlan plan) =>
        $"Rename {plan.Symbol} to {plan.NewName} in {(plan.Files.Count == 1 ? "1 file" : $"{plan.Files.Count} files")}";

    private static string? RelativeTo(string repoRoot, string fullPath)
    {
        string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : null;
    }

    private static CodeSite ParseSite(IReadOnlyDictionary<string, JsonElement> input, RepoFiles files)
    {
        string path = Text(input, "path");
        if (path.Length == 0)
        {
            throw new ToolInputException("`path` must be a file in the repository.");
        }

        string full = files.Resolve(path);
        if (!File.Exists(full))
        {
            throw new ToolInputException($"File not found: {path}");
        }

        int line = input.TryGetValue("line", out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)
            ? n
            : throw new ToolInputException("`line` must be a line number (1-based).");
        string symbol = Text(input, "symbol").Trim();
        if (symbol.Length == 0)
        {
            throw new ToolInputException("`symbol` must be the name as written on that line, e.g. \"GetTotal\".");
        }

        return new CodeSite(full, line, symbol);
    }

    private static int Limit(IReadOnlyDictionary<string, JsonElement> input) =>
        input.TryGetValue("max_results", out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)
            ? Math.Max(1, Math.Min(MaxResults, n))
            : DefaultResults;

    private static string Text(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;

    /// <summary>Claude Code's tool input as the dictionary the shared code reads.</summary>
    public static IReadOnlyDictionary<string, JsonElement> ToDictionary(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object ? input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : new Dictionary<string, JsonElement>();
}
