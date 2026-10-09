using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Git;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.Agent;

/// <summary>How tool calls are shown on the approval card and in the transcript.</summary>
internal sealed partial class ClaudeCodeEngine
{
    /// <summary>How a pending permission request is shown on the approval card.</summary>
    private (ActivityKind Kind, string Title, string Detail) DescribeRequest(string tool, JsonElement input) => tool switch
    {
        ApprovalServer.BuildTool => (ActivityKind.Shell, "Build the solution in Visual Studio", string.Empty),
        ApprovalServer.AddPackageTool => (ActivityKind.Shell, PackageTitle(input, remove: false), string.Empty),
        ApprovalServer.RemovePackageTool => (ActivityKind.Shell, PackageTitle(input, remove: true), string.Empty),
        "Bash" or "PowerShell" => (ActivityKind.Shell, $"Run: {Str(input, "command")}", Str(input, "description") ?? string.Empty),
        "Edit" => (ActivityKind.Edit, $"Edit {this.Relative(Str(input, "file_path"))}", EditDiff(input)),
        "MultiEdit" => (ActivityKind.Edit, $"Edit {this.Relative(Str(input, "file_path"))}", MultiEditDiff(input)),
        "Write" => (ActivityKind.Create, $"Write {this.Relative(Str(input, "file_path"))}", Str(input, "content") ?? string.Empty),
        "NotebookEdit" => (ActivityKind.Edit, $"Edit notebook {this.Relative(Str(input, "notebook_path"))}", Str(input, "new_source") ?? string.Empty),
        "WebFetch" => (ActivityKind.Web, $"Fetch {Str(input, "url")}", Str(input, "prompt") ?? string.Empty),
        "WebSearch" => (ActivityKind.Web, $"Search the web: {Str(input, "query")}", string.Empty),
        _ => (ActivityKind.Other, $"Use {tool}", input.ValueKind == JsonValueKind.Undefined ? string.Empty : Pretty(input)),
    };

    /// <summary>How a completed tool call is shown in the transcript.</summary>
    /// <param name="denied">True if the extension refused this call when Claude Code asked permission.</param>
    private (ActivityKind Kind, string Title, string Detail) Describe(string tool, JsonElement input, string result, bool isError, bool denied)
    {
        (ActivityKind kind, string title, string detail) = tool switch
        {
            "Bash" or "PowerShell" => (ActivityKind.Shell, Str(input, "command") ?? tool, result),
            "Read" => (ActivityKind.Read, $"Read {this.Relative(Str(input, "file_path"))}", string.Empty),
            "Edit" => (ActivityKind.Edit, $"Edited {this.Relative(Str(input, "file_path"))}", EditDiff(input)),
            "MultiEdit" => (ActivityKind.Edit, $"Edited {this.Relative(Str(input, "file_path"))}", MultiEditDiff(input)),
            "Write" => (ActivityKind.Create, $"Wrote {this.Relative(Str(input, "file_path"))}", Str(input, "content") ?? string.Empty),
            "NotebookEdit" => (ActivityKind.Edit, $"Edited notebook {this.Relative(Str(input, "notebook_path"))}", string.Empty),
            "Glob" => (ActivityKind.Search, $"Find files: {Str(input, "pattern")}", result),
            "Grep" => (ActivityKind.Search, $"Search: {Str(input, "pattern")}", result),
            "WebFetch" => (ActivityKind.Web, $"Fetched {Str(input, "url")}", result),
            "WebSearch" => (ActivityKind.Web, $"Searched the web: {Str(input, "query")}", result),
            "TodoWrite" => (ActivityKind.Other, "Updated the task list", TaskList.Format(TaskList.Parse(input))),
            ApprovalServer.DiagnosticsTool => (ActivityKind.Search, "Checked the Error List", result),
            ApprovalServer.DebugTool => (ActivityKind.Search, "Read the debugger's state", result),
            ApprovalServer.BuildTool => (result.StartsWith("Build succeeded", StringComparison.Ordinal) ? ActivityKind.Shell : ActivityKind.Failed, "Built the solution", result),
            ApprovalServer.DefinitionTool => (ActivityKind.Search, $"Go to definition of {Str(input, "symbol")}", result),
            ApprovalServer.ReferencesTool => (ActivityKind.Search, $"Find references to {Str(input, "symbol")}", result),
            ApprovalServer.SymbolsTool => (ActivityKind.Search, Str(input, "query") is { Length: > 0 } query ? $"Find symbols matching {query}" : $"Find symbols in {Str(input, "path")}", result),
            ApprovalServer.RenameTool => (ActivityKind.Edit, $"Rename {Str(input, "symbol")} to {Str(input, "new_name")}", result),
            ApprovalServer.ListPackagesTool => (ActivityKind.Search, "Listed NuGet packages", result),
            ApprovalServer.AddPackageTool => (ActivityKind.Shell, PackageTitle(input, remove: false), result),
            ApprovalServer.RemovePackageTool => (ActivityKind.Shell, PackageTitle(input, remove: true), result),
            "AskUserQuestion" => (ActivityKind.Question, "Asked you " + QuestionCount(input), result),
            "Task" or "Agent" => (ActivityKind.Other, $"Subagent: {Str(input, "description") ?? "task"}", result),
            _ => (ActivityKind.Other, tool, result),
        };

        if (isError)
        {
            // Without the call's ID (older Claude Code), go by the wording of the refusals: the extension's own, and
            // Claude Code's for permission rules. A plain "denied" is usually an error, e.g. "Access denied".
            bool declined = denied
                || result.IndexOf("declined", StringComparison.OrdinalIgnoreCase) >= 0
                || result.IndexOf("Cancelled by the user", StringComparison.OrdinalIgnoreCase) >= 0
                || result.IndexOf("Permission to use", StringComparison.OrdinalIgnoreCase) >= 0;
            return (declined ? ActivityKind.Declined : ActivityKind.Failed, title, result);
        }

        return (kind, title, detail);
    }

    /// <summary>The absolute path an edit tool's input refers to, or null if it has none.</summary>
    private string? FullPath(JsonElement input)
    {
        string? path = Str(input, "file_path") ?? Str(input, "notebook_path");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(this.RepoRoot, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string Relative(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(unknown file)";
        }

        string root = this.RepoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path!.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
    }

    /// <summary>
    /// When the whole file before and after is known, shows the edit as a diff with context instead of the tool's
    /// old and new strings. A new file stays as its contents.
    /// </summary>
    private static void WithDiff(string tool, FileChange? change, ref ActivityKind kind, ref string detail)
    {
        if (change is null || (tool == "Write" && change.Before.Length == 0))
        {
            return;
        }

        kind = ActivityKind.Edit;
        detail = UnifiedDiff.Create(change.Before, change.After);
    }

    private static string EditDiff(JsonElement input) => Diff(Str(input, "old_string") ?? string.Empty, Str(input, "new_string") ?? string.Empty);

    private static string MultiEditDiff(JsonElement input)
    {
        if (!input.TryGetProperty("edits", out JsonElement edits) || edits.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join("\n", edits.EnumerateArray().Select(EditDiff));
    }

    private static string Diff(string oldText, string newText)
    {
        IEnumerable<string> Lines(string text, string prefix) =>
            text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n').Select(l => prefix + l);
        return string.Join("\n", Lines(oldText, "- ").Concat(Lines(newText, "+ ")));
    }

    private static string QuestionCount(JsonElement input) =>
        UserQuestion.ParseAll(input).Count is int n && n == 1 ? "a question" : $"{UserQuestion.ParseAll(input).Count} questions";
}
