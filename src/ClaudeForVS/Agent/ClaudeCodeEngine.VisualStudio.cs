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

/// <summary>The Visual Studio tools Claude Code reaches through the approval server.</summary>
internal sealed partial class ClaudeCodeEngine
{
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
                return (Diagnostics.Format(items, this.RepoRoot, scope, includeWarnings: Str(input, "severity") != "error"), false);

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
                return ($"{outcome.Summary}\n{Diagnostics.Format(after, this.RepoRoot, string.Empty, includeWarnings: true)}", !outcome.Succeeded);

            case "get_debug_state":
                DebugState state = await vs.GetDebugStateAsync(cancellationToken).ConfigureAwait(false);
                return (DebugReport.Format(state, this.RepoRoot, input.ValueKind == JsonValueKind.Object && input.TryGetProperty("frames", out JsonElement framesEl) && framesEl.TryGetInt32(out int frames) ? frames : DebugReport.DefaultFrames), false);

            case "go_to_definition":
                return (await CodeTools.GoToDefinitionAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "find_references":
                return (await CodeTools.FindReferencesAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "find_symbols":
                return (await CodeTools.FindSymbolsAsync(CodeTools.ToDictionary(input), new RepoFiles(this.RepoRoot), this.RepoRoot, _ => true, cancellationToken).ConfigureAwait(false), false);

            case "rename_symbol":
                return await this.RenameSymbolAsync(CodeTools.ToDictionary(input), cancellationToken).ConfigureAwait(false);

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
        string title = CodeTools.Title(plan);
        IChatSink? chatSink = this.sink;

        if (this.Mode != AgentMode.Auto && !this.editsPreApproved)
        {
            if (chatSink is null)
            {
                return ("No one is available to approve this.", true);
            }

            await this.approvalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                chatSink.SetStatus("Waiting for your approval…");
                ApprovalResult result = await chatSink.RequestApprovalAsync(ActivityKind.Edit, title, CodeTools.Describe(plan, this.RepoRoot), "Allow all edits", cancellationToken);
                if (result.Decision == Approval.Deny)
                {
                    return ("The user declined this rename. Don't retry it; ask what they'd like instead.", true);
                }

                if (result.Decision == Approval.AllowAll)
                {
                    this.editsPreApproved = true;
                }
            }
            catch (OperationCanceledException)
            {
                return ("Cancelled by the user.", true);
            }
            finally
            {
                this.approvalGate.Release();
            }
        }

        // Writing checks for unsaved editor changes, which Visual Studio only answers on its main thread.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        IReadOnlyList<FileChange> changes = CodeTools.ApplyRename(plan, repoFiles, this.RepoRoot);
        foreach (FileChange change in changes)
        {
            chatSink?.ToolActivity(ActivityKind.Edit, $"Renamed in {this.Relative(change.FullPath)}", UnifiedDiff.Create(change.Before, change.After), change);
        }

        return ($"Renamed {plan.Symbol} to {plan.NewName} in {string.Join(", ", changes.Select(c => this.Relative(c.FullPath)))}. Check the result with get_diagnostics or build_solution.", false);
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
