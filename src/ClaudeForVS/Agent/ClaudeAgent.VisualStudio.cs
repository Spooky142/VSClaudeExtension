using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.Git;
using ApiEffort = Anthropic.Models.Messages.Effort;

namespace ClaudeForVS.Agent;

/// <summary>Tools backed by Visual Studio: the task list, the Error List and builds.</summary>
internal sealed partial class ClaudeAgent
{
    private static string WriteTodos(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink)
    {
        IReadOnlyList<TaskItem> tasks = TaskList.Parse(JsonSerializer.SerializeToElement(input));
        if (tasks.Count == 0 && !(input.TryGetValue("todos", out JsonElement todos) && todos.ValueKind == JsonValueKind.Array))
        {
            throw new ToolInputException("`todos` must be an array of {content, status, activeForm}.");
        }

        sink.ReportTasks(tasks);
        sink.ToolActivity(ActivityKind.Other, "Updated the task list", TaskList.Format(tasks));
        int done = tasks.Count(t => t.Status == "completed");
        return $"Task list updated ({done} of {tasks.Count} completed).";
    }

    private async Task<string> GetDiagnosticsAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        IVisualStudioHost vs = VisualStudioHost.Current ?? throw new ToolInputException("Visual Studio's Error List isn't available.");
        string scope = this.OptionalRepoPath(input, "path", folderOnly: false);
        bool includeWarnings = !(input.TryGetValue("severity", out JsonElement s) && s.ValueKind == JsonValueKind.String && s.GetString() == "error");

        sink.SetStatus("Reading Visual Studio's Error List…");
        IReadOnlyList<ErrorListItem> items = await vs.GetDiagnosticsAsync(cancellationToken);
        string result = Diagnostics.Format(items, this.RepoRoot, scope, includeWarnings);
        string note = await this.SolutionNoteAsync(vs, cancellationToken);
        sink.ToolActivity(ActivityKind.Search, "Checked the Error List" + (scope.Length > 0 ? $" for {scope}" : string.Empty), result);
        return result + note;
    }

    private async Task<string> GetDebugStateAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        IVisualStudioHost vs = VisualStudioHost.Current ?? throw new ToolInputException("Visual Studio's debugger isn't available.");
        sink.SetStatus("Reading the debugger…");
        DebugState state = await vs.GetDebugStateAsync(cancellationToken);
        string result = DebugReport.Format(state, this.RepoRoot, OptionalInt(input, "frames") ?? DebugReport.DefaultFrames);
        sink.ToolActivity(ActivityKind.Search, "Read the debugger's state", result);
        return result;
    }

    private async Task<string> GetActiveDocumentAsync(IChatSink sink, CancellationToken cancellationToken)
    {
        IVisualStudioHost vs = VisualStudioHost.Current ?? throw new ToolInputException("Visual Studio's editor isn't available.");
        sink.SetStatus("Looking at the editor…");
        ActiveDocument? document = await vs.GetActiveDocumentAsync(cancellationToken);
        string result = ActiveDocumentReport.Format(document, this.RepoRoot, this.CanShow);
        sink.ToolActivity(ActivityKind.Search, document is null ? "Looked at the editor" : $"Looked at {Path.GetFileName(document.FullPath)} in the editor", result);
        return result;
    }

    private async Task<string> BuildSolutionAsync(IChatSink sink, CancellationToken cancellationToken)
    {
        const string Title = "Build the solution in Visual Studio";
        IVisualStudioHost vs = VisualStudioHost.Current ?? throw new ToolInputException("Visual Studio isn't available to build with.");
        if (this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, Title, "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        string solution = await vs.GetSolutionDirectoryAsync(cancellationToken) ?? throw new ToolInputException("No solution is open in Visual Studio.");
        string root = this.RepoRoot.TrimEnd('\\', '/');
        string dir = solution.TrimEnd('\\', '/');
        if (!dir.Equals(root, StringComparison.OrdinalIgnoreCase) && !dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolInputException($"The solution open in Visual Studio ({solution}) isn't in this repository, so it wasn't built. Use run_command to build instead.");
        }

        if (this.Mode == AgentMode.Coding && !CommandRules.IsToolAllowed(this.RepoRoot, "build_solution"))
        {
            sink.SetStatus("Waiting for your approval…");
            ApprovalResult approval = await sink.RequestApprovalAsync(ActivityKind.Shell, Title, string.Empty, "Always allow builds here", cancellationToken);
            if (approval.Decision == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, Title, "Declined by you.");
                return "The user declined to build the solution.";
            }

            if (approval.Decision == Approval.AllowAll)
            {
                CommandRules.Allow(this.RepoRoot, "build_solution");
            }
        }

        sink.SetStatus("Building the solution in Visual Studio…");
        BuildOutcome outcome;
        try
        {
            outcome = await vs.BuildSolutionAsync(cancellationToken);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new ToolInputException("Visual Studio couldn't start the build: " + ex.Message);
        }

        IReadOnlyList<ErrorListItem> items = await vs.GetDiagnosticsAsync(cancellationToken);
        string diagnostics = Diagnostics.Format(items, this.RepoRoot, string.Empty, includeWarnings: true);
        string result = $"{outcome.Summary}\n{diagnostics}";
        sink.ToolActivity(outcome.Succeeded ? ActivityKind.Shell : ActivityKind.Failed, outcome.Succeeded ? "Built the solution" : "Build failed", result);
        return result;
    }

    /// <summary>A warning to add when the Error List belongs to a solution outside this repository.</summary>
    private async Task<string> SolutionNoteAsync(IVisualStudioHost vs, CancellationToken cancellationToken)
    {
        string? solution = await vs.GetSolutionDirectoryAsync(cancellationToken);
        string root = this.RepoRoot.TrimEnd('\\', '/');
        string? dir = solution?.TrimEnd('\\', '/');
        bool inside = dir != null && (dir.Equals(root, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return inside ? string.Empty
            : dir is null ? "\n(No solution is open in Visual Studio, so only files open in the editor are checked.)"
            : $"\n(Note: the solution open in Visual Studio, {solution}, isn't in this repository.)";
    }
}
