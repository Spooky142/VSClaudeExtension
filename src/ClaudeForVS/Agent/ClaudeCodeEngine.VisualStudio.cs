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

/// <summary>The Visual Studio tools Claude Code reaches through <see cref="VisualStudioToolServer"/>.</summary>
internal sealed partial class ClaudeCodeEngine
{
    /// <summary>Starts the Visual Studio tools' MCP server, if it isn't running; returns the --mcp-config file naming it.</summary>
    private string StartToolServer()
    {
        if (this.toolServer is null || this.mcpConfigPath is null)
        {
            this.toolServer?.Dispose();
            this.toolServer = new VisualStudioToolServer(this.RunVisualStudioToolAsync);
            string dir = Path.Combine(Path.GetTempPath(), "ClaudeForVS");
            Directory.CreateDirectory(dir);
            this.mcpConfigPath = Path.Combine(dir, $"mcp-{Guid.NewGuid():N}.json");
            File.WriteAllText(this.mcpConfigPath, JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    // Claude Code gives up on a call to an HTTP server that's quiet for 5 minutes; a build, or a rename
                    // or code fix waiting for the user's approval, can take longer. The server's own timeout raises that.
                    [VisualStudioToolServer.ServerName] = new { type = "http", url = this.toolServer.Url, timeout = VisualStudioToolServer.CallTimeoutMilliseconds },
                },
            }));
        }

        return this.mcpConfigPath;
    }

    /// <summary>Stops the Visual Studio tools' MCP server, if it's running, and deletes its config file.</summary>
    private void StopToolServer()
    {
        this.toolServer?.Dispose();
        this.toolServer = null;
        if (this.mcpConfigPath is string path)
        {
            this.mcpConfigPath = null;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Runs one of the extension's Visual Studio tools for Claude Code (called on a thread-pool thread).</summary>
    private async Task<(string Text, bool IsError)> RunVisualStudioToolAsync(string name, JsonElement input)
    {
        IVisualStudioHost vs = VisualStudioHost.Current ?? throw new InvalidOperationException("Visual Studio isn't available.");
        CancellationToken cancellationToken = this.runToken;
        switch (name)
        {
            case "get_diagnostics":
                string scope = Str(input, "path") is { Length: > 0 } path && path.Trim() is not ("." or "/")
                    ? this.Relative(this.FullPath(JsonSerializer.SerializeToElement(new { file_path = path })) ?? path).Replace('\\', '/')
                    : string.Empty;
                IReadOnlyList<ErrorListItem> items = await vs.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyCollection<string> changed = await Diagnostics.ChangedFilesAsync(this.RepoRoot, cancellationToken).ConfigureAwait(false);
                return (Diagnostics.Format(items, this.RepoRoot, scope, includeWarnings: Str(input, "severity") == "warning", changed), false);

            case "build_solution":
                string solution = await vs.GetSolutionDirectoryAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("No solution is open in Visual Studio.");
                string root = this.RepoRoot.TrimEnd('\\', '/');
                string dir = solution.TrimEnd('\\', '/');
                if (!dir.Equals(root, StringComparison.OrdinalIgnoreCase) && !dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return ($"The solution open in Visual Studio ({solution}) isn't in this repository, so it wasn't built.", true);
                }

                BuildOutcome outcome = await vs.BuildSolutionAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ErrorListItem> after = await vs.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyCollection<string> touched = await Diagnostics.ChangedFilesAsync(this.RepoRoot, cancellationToken).ConfigureAwait(false);
                return ($"{outcome.Summary}\n{Diagnostics.Format(after, this.RepoRoot, string.Empty, includeWarnings: false, touched)}", !outcome.Succeeded);

            case "get_debug_state":
                DebugState state = await vs.GetDebugStateAsync(cancellationToken).ConfigureAwait(false);
                return (DebugReport.Format(state, this.RepoRoot, input.ValueKind == JsonValueKind.Object && input.TryGetProperty("frames", out JsonElement framesEl) && framesEl.TryGetInt32(out int frames) ? frames : DebugReport.DefaultFrames), false);

            case "go_to_definition":
                return (await CodeTools.GoToDefinitionAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "find_references":
                return (await CodeTools.FindReferencesAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "find_symbols":
                return (await CodeTools.FindSymbolsAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "get_active_document":
                ActiveDocument? active = await vs.GetActiveDocumentAsync(cancellationToken).ConfigureAwait(false);
                return (ActiveDocumentReport.Format(active, this.RepoRoot, _ => true), false);

            case "rename_symbol":
                return await this.RenameSymbolAsync(CodeTools.ToDictionary(input), cancellationToken).ConfigureAwait(false);

            case "get_code_fixes":
                return (await CodeTools.GetCodeFixesAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, cancellationToken).ConfigureAwait(false), false);

            case "apply_code_fix":
                return await this.ApplyCodeFixAsync(CodeTools.ToDictionary(input), cancellationToken).ConfigureAwait(false);

            case "list_packages":
                return (await PackageTools.ListAsync(CodeTools.ToDictionary(input), this.RepoRoot, cancellationToken).ConfigureAwait(false), false);

            case "add_package":
                return (await PackageTools.AddAsync(PackageTools.Parse(CodeTools.ToDictionary(input), withVersion: true), cancellationToken).ConfigureAwait(false), false);

            case "remove_package":
                return (await PackageTools.RemoveAsync(PackageTools.Parse(CodeTools.ToDictionary(input), withVersion: false), cancellationToken).ConfigureAwait(false), false);

            default:
                return ($"Unknown tool: {name}", true);
        }
    }

    /// <summary>
    /// rename_symbol for Claude Code: works the rename out, shows every file's changes in one approval, then writes them.
    /// (The permission prompt lets the tool through, as only now are the changes known.)
    /// </summary>
    private async Task<(string Text, bool IsError)> RenameSymbolAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var repoFiles = new RepoFiles(this.RepoRoot);
        RenamePlan plan = await CodeTools.PlanRenameAsync(input, repoFiles, this.RepoRoot, cancellationToken).ConfigureAwait(false);
        if (await this.ApproveChangesAsync(CodeTools.Title(plan), CodeTools.Describe(plan, this.RepoRoot), cancellationToken).ConfigureAwait(false) is string refused)
        {
            return (refused.Length > 0 ? refused : "The user declined this rename. Don't retry it; ask what they'd like instead.", true);
        }

        // Writing checks for unsaved editor changes, which Visual Studio only answers on its main thread.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        IReadOnlyList<FileChange> changes = CodeTools.ApplyRename(plan, repoFiles, this.RepoRoot);
        foreach (FileChange change in changes)
        {
            this.sink?.ToolActivity(ActivityKind.Edit, $"Renamed in {this.Relative(change.FullPath)}", UnifiedDiff.Create(change.Before, change.After), change);
        }

        return ($"Renamed {plan.Symbol} to {plan.NewName} in {string.Join(", ", changes.Select(c => this.Relative(c.FullPath)))}. Check the result with get_diagnostics or build_solution.", false);
    }

    /// <summary>apply_code_fix for Claude Code: works the fix out, shows every file's changes in one approval, then writes them.</summary>
    private async Task<(string Text, bool IsError)> ApplyCodeFixAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var repoFiles = new RepoFiles(this.RepoRoot);
        CodeFixPlan plan = await CodeTools.PlanCodeFixAsync(input, repoFiles, this.RepoRoot, cancellationToken).ConfigureAwait(false);
        if (await this.ApproveChangesAsync(CodeTools.Title(plan), CodeTools.Describe(plan, this.RepoRoot), cancellationToken).ConfigureAwait(false) is string refused)
        {
            return (refused.Length > 0 ? refused : "The user declined this code fix. Don't retry it; ask what they'd like instead.", true);
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        IReadOnlyList<FileChange> changes = CodeTools.ApplyCodeFix(plan, repoFiles, this.RepoRoot);
        foreach (FileChange change in changes)
        {
            bool created = change.Before.Length == 0;
            this.sink?.ToolActivity(
                created ? ActivityKind.Create : ActivityKind.Edit,
                $"{plan.Fix} in {this.Relative(change.FullPath)}",
                created ? change.After : UnifiedDiff.Create(change.Before, change.After),
                change);
        }

        return ($"Applied \"{plan.Fix}\" ({plan.Diagnostic}) to {string.Join(", ", changes.Select(c => this.Relative(c.FullPath)))}. Check the result with get_diagnostics or build_solution.", false);
    }

    /// <summary>
    /// Asks the user to approve changes to several files at once, unless edits need no approval. Returns null to go
    /// ahead; otherwise why not (empty when the user declined).
    /// </summary>
    private async Task<string?> ApproveChangesAsync(string title, string detail, CancellationToken cancellationToken)
    {
        IChatSink? chatSink = this.sink;
        if (this.Mode == AgentMode.Auto || this.editsPreApproved)
        {
            return null;
        }

        if (chatSink is null)
        {
            return "No one is available to approve this.";
        }

        await this.approvalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            chatSink.SetStatus("Waiting for your approval…");
            ApprovalResult result = await chatSink.RequestApprovalAsync(ActivityKind.Edit, title, detail, "Allow all edits", cancellationToken);
            if (result.Decision == Approval.Deny)
            {
                return string.Empty;
            }

            if (result.Decision == Approval.AllowAll)
            {
                this.editsPreApproved = true;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return "Cancelled by the user.";
        }
        finally
        {
            this.approvalGate.Release();
        }
    }

    /// <summary>The approval card's title for add_package or remove_package.</summary>
    private static string PackageTitle(JsonElement input, bool remove)
    {
        try
        {
            return PackageTools.Title(PackageTools.Parse(CodeTools.ToDictionary(input), withVersion: !remove), remove);
        }
        catch (ToolInputException)
        {
            return remove ? "Remove a NuGet package" : "Add a NuGet package";
        }
    }
}
