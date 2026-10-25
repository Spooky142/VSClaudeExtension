using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>Tools backed by Visual Studio's code model (Roslyn) and NuGet.</summary>
internal sealed partial class ClaudeAgent
{
    private async Task<string> GoToDefinitionAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string title = $"Go to definition of {Described(input)}";
        if (this.RefuseUnreadable(input, title, sink) is string refusal)
        {
            return refusal;
        }

        sink.SetStatus("Looking up a definition…");
        string result = await CodeTools.GoToDefinitionAsync(input, this.files, this.RepoRoot, this.CanShow, cancellationToken);
        sink.ToolActivity(ActivityKind.Search, title, result);
        return result;
    }

    private async Task<string> FindReferencesAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string title = $"Find references to {Described(input)}";
        if (this.RefuseUnreadable(input, title, sink) is string refusal)
        {
            return refusal;
        }

        sink.SetStatus("Finding references…");
        string result = await CodeTools.FindReferencesAsync(input, this.files, this.RepoRoot, this.CanShow, cancellationToken);
        sink.ToolActivity(ActivityKind.Search, title, result);
        return result;
    }

    private async Task<string> FindSymbolsAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string what = input.TryGetValue("query", out JsonElement q) && q.ValueKind == JsonValueKind.String && q.GetString()!.Length > 0
            ? $"symbols matching {q.GetString()}"
            : $"symbols in {(input.TryGetValue("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : "a file")}";
        string title = "Find " + what;
        if (this.RefuseUnreadable(input, title, sink) is string refusal)
        {
            return refusal;
        }

        sink.SetStatus("Searching the code model…");
        string result = await CodeTools.FindSymbolsAsync(input, this.files, this.RepoRoot, this.CanShow, cancellationToken);
        sink.ToolActivity(ActivityKind.Search, title, result);
        return result;
    }

    private async Task<string> RenameSymbolAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        if (this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, "Rename a symbol", "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (this.RefuseUnreadable(input, "Rename a symbol", sink) is string refusal)
        {
            return refusal;
        }

        sink.SetStatus("Working out the rename…");
        RenamePlan plan = await CodeTools.PlanRenameAsync(input, this.files, this.RepoRoot, cancellationToken);
        string title = CodeTools.Title(plan);
        ApprovalResult approval = await this.ApproveEditAsync(
            ActivityKind.Edit, title, CodeTools.Describe(plan, this.RepoRoot), null, sink, cancellationToken, plan.Files.Select(f => f.FullPath).ToList());
        if (approval.Decision == Approval.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Declined by you.");
            return "The user declined this rename.";
        }

        IReadOnlyList<FileChange> changes = CodeTools.ApplyRename(plan, this.files, this.RepoRoot);
        foreach (FileChange change in changes)
        {
            sink.ToolActivity(ActivityKind.Edit, $"Renamed in {this.files.Relative(change.FullPath)}", UnifiedDiff.Create(change.Before, change.After), change);
        }

        return $"Renamed {plan.Symbol} to {plan.NewName} in {string.Join(", ", changes.Select(c => this.files.Relative(c.FullPath)))}. " +
            "Check the result with get_diagnostics or build_solution.";
    }

    private async Task<string> GetCodeFixesAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string title = $"Find code fixes for {Described(input, "diagnostic", "the problems")}";
        if (this.RefuseUnreadable(input, title, sink) is string refusal)
        {
            return refusal;
        }

        sink.SetStatus("Looking for code fixes…");
        string result = await CodeTools.GetCodeFixesAsync(input, this.files, this.RepoRoot, cancellationToken);
        sink.ToolActivity(ActivityKind.Search, title, result);
        return result;
    }

    private async Task<string> ApplyCodeFixAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        if (this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, "Apply a code fix", "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (this.RefuseUnreadable(input, "Apply a code fix", sink) is string refusal)
        {
            return refusal;
        }

        sink.SetStatus("Working out the code fix…");
        CodeFixPlan plan = await CodeTools.PlanCodeFixAsync(input, this.files, this.RepoRoot, cancellationToken);
        string title = CodeTools.Title(plan);
        ApprovalResult approval = await this.ApproveEditAsync(
            ActivityKind.Edit, title, CodeTools.Describe(plan, this.RepoRoot), null, sink, cancellationToken, plan.Files.Select(f => f.FullPath).ToList());
        if (approval.Decision == Approval.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Declined by you.");
            return "The user declined this code fix.";
        }

        IReadOnlyList<FileChange> changes = CodeTools.ApplyCodeFix(plan, this.files, this.RepoRoot);
        foreach (FileChange change in changes)
        {
            sink.ToolActivity(
                change.Before.Length == 0 ? ActivityKind.Create : ActivityKind.Edit,
                $"{plan.Fix} in {this.files.Relative(change.FullPath)}",
                change.Before.Length == 0 ? change.After : UnifiedDiff.Create(change.Before, change.After),
                change);
        }

        return $"Applied \"{plan.Fix}\" ({plan.Diagnostic}) to {string.Join(", ", changes.Select(c => this.files.Relative(c.FullPath)))}. " +
            "Check the result with get_diagnostics or build_solution.";
    }

    private async Task<string> ListPackagesAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        sink.SetStatus("Reading the projects' NuGet packages…");
        string result = await PackageTools.ListAsync(input, this.RepoRoot, cancellationToken);
        sink.ToolActivity(ActivityKind.Search, "Listed NuGet packages", result);
        return result;
    }

    private Task<string> AddPackageAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
        => this.ChangePackageAsync(input, remove: false, sink, cancellationToken);

    private Task<string> RemovePackageAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
        => this.ChangePackageAsync(input, remove: true, sink, cancellationToken);

    private async Task<string> ChangePackageAsync(IReadOnlyDictionary<string, JsonElement> input, bool remove, IChatSink sink, CancellationToken cancellationToken)
    {
        PackageRequest request = PackageTools.Parse(input, withVersion: !remove);
        string title = PackageTools.Title(request, remove);
        if (this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, title, "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (this.Mode == AgentMode.Coding && !CommandRules.IsToolAllowed(this.RepoRoot, PackageTools.RuleKey))
        {
            sink.SetStatus("Waiting for your approval…");
            ApprovalResult approval = await sink.RequestApprovalAsync(ActivityKind.Shell, title, string.Empty, "Always allow package changes here", cancellationToken);
            if (approval.Decision == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, title, "Declined by you.");
                return "The user declined this package change.";
            }

            if (approval.Decision == Approval.AllowAll)
            {
                CommandRules.Allow(this.RepoRoot, PackageTools.RuleKey);
            }
        }

        sink.SetStatus(remove ? $"Removing {request.PackageId}…" : $"Adding {request.PackageId}…");
        string result = remove
            ? await PackageTools.RemoveAsync(request, cancellationToken)
            : await PackageTools.AddAsync(request, cancellationToken);
        sink.ToolActivity(ActivityKind.Shell, title, result);
        return result;
    }

    /// <summary>The files whose text the code tools may show: the ones the user's permission rules don't restrict.</summary>
    private bool CanShow(string fullPath) => this.permissions.ForRead(fullPath) is PermissionVerdict.None or PermissionVerdict.Allow;

    /// <summary>A refusal for Claude if the tool's own file can't be read under the permission rules; null otherwise.</summary>
    private string? RefuseUnreadable(IReadOnlyDictionary<string, JsonElement> input, string title, IChatSink sink)
    {
        if (!input.TryGetValue("path", out JsonElement el) || el.ValueKind != JsonValueKind.String || el.GetString() is not { Length: > 0 } path
            || this.ReadVerdict(path) is PermissionVerdict.None or PermissionVerdict.Allow)
        {
            return null;
        }

        sink.ToolActivity(ActivityKind.Declined, title, "Reading this file is restricted by your permission settings.");
        return SettingsRefusal;
    }

    /// <summary>"Total (Report.cs:7)": what a code tool call is about, for its row in the chat.</summary>
    private static string Described(IReadOnlyDictionary<string, JsonElement> input, string name = "symbol", string fallback = "a symbol")
    {
        string symbol = input.TryGetValue(name, out JsonElement s) && s.ValueKind == JsonValueKind.String && s.GetString()!.Length > 0 ? s.GetString()! : fallback;
        string file = input.TryGetValue("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? Path.GetFileName(p.GetString()!) : string.Empty;
        string line = input.TryGetValue("line", out JsonElement l) && l.ValueKind == JsonValueKind.Number ? ":" + l.GetRawText() : string.Empty;
        return file.Length == 0 ? symbol : $"{symbol} ({file}{line})";
    }
}

