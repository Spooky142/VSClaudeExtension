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

/// <summary>Deciding Claude Code's permission requests: asking the user, partial edits and questions.</summary>
internal sealed partial class ClaudeCodeEngine
{
    // Tool calls the extension refused (the user said no, plan mode, unsaved changes...), so their failed results show
    // as declined rather than as errors. Written by the approval server's threads.
    private readonly ConcurrentDictionary<string, bool> deniedTools = new(StringComparer.Ordinal);

    // Edits the user approved only some of: the extension wrote the kept changes itself (before → what was written).
    private readonly ConcurrentDictionary<string, FileChange> partlyApplied = new(StringComparer.Ordinal);

    /// <summary>Called by the approval server (on a thread-pool thread) when Claude Code needs permission.</summary>
    private async Task<PermissionDecision> DecideAsync(string toolName, JsonElement input, string? toolUseId)
    {
        PermissionDecision decision = await this.DecideCoreAsync(toolName, input, toolUseId).ConfigureAwait(false);
        if (!decision.Allow && toolUseId != null && !this.partlyApplied.ContainsKey(toolUseId))
        {
            this.deniedTools[toolUseId] = true;
        }

        return decision;
    }

    private async Task<PermissionDecision> DecideCoreAsync(string toolName, JsonElement input, string? toolUseId)
    {
        // Reading the Error List, the debugger, the code model or the package list never needs asking; a build is treated like a command.
        if (toolName is ApprovalServer.DiagnosticsTool or ApprovalServer.DebugTool or ApprovalServer.ActiveDocumentTool or ApprovalServer.CodeFixesTool
            or ApprovalServer.DefinitionTool or ApprovalServer.ReferencesTool or ApprovalServer.SymbolsTool or ApprovalServer.ListPackagesTool)
        {
            return new PermissionDecision(true);
        }

        // A rename or code fix asks for approval itself, once its changes are known and shown together.
        if (toolName is ApprovalServer.RenameTool or ApprovalServer.ApplyCodeFixTool)
        {
            return this.Mode == AgentMode.Plan
                ? new PermissionDecision(false, "Plan mode is on, so no files can be changed. Describe the change as text instead; the user can switch modes in Settings.")
                : new PermissionDecision(true);
        }

        if (toolName == ApprovalServer.BuildTool)
        {
            if (this.Mode == AgentMode.Plan)
            {
                return new PermissionDecision(false, "Plan mode is on, so the solution can't be built. The user can switch modes in Settings.");
            }

            if (this.Mode == AgentMode.Auto || CommandRules.IsToolAllowed(this.RepoRoot, "build_solution"))
            {
                return new PermissionDecision(true);
            }
        }

        if (toolName is ApprovalServer.AddPackageTool or ApprovalServer.RemovePackageTool)
        {
            if (this.Mode == AgentMode.Plan)
            {
                return new PermissionDecision(false, "Plan mode is on, so packages can't be changed. The user can switch modes in Settings.");
            }

            if (this.Mode == AgentMode.Auto || CommandRules.IsToolAllowed(this.RepoRoot, PackageTools.RuleKey))
            {
                return new PermissionDecision(true);
            }
        }

        // Writing to a file with unsaved changes in the editor would leave Visual Studio with two versions of it.
        if (EditTools.Contains(toolName) && this.Mode != AgentMode.Plan && this.FullPath(input) is string editPath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (OpenDocuments.HasUnsavedChanges(editPath))
            {
                return new PermissionDecision(false,
                    $"{this.Relative(editPath)} has unsaved changes in Visual Studio, so it wasn't changed. Ask the user to save or discard them, then read the file again.");
            }
        }

        if (this.editsPreApproved && EditTools.Contains(toolName) && this.Mode != AgentMode.Plan)
        {
            return new PermissionDecision(true);
        }

        if (toolName == "AskUserQuestion")
        {
            return await this.AskQuestionsAsync(input).ConfigureAwait(false);
        }

        // Plan mode never leaves plan mode or changes files; the user switches modes in Settings.
        if (this.Mode == AgentMode.Plan && (toolName == "ExitPlanMode" || EditTools.Contains(toolName)))
        {
            return new PermissionDecision(false, "Plan mode is on, so no changes can be made. Present your suggestions as text; the user can switch modes in Settings.");
        }

        if (this.Mode == AgentMode.Auto)
        {
            return new PermissionDecision(true);
        }

        bool isShell = toolName is "Bash" or "PowerShell";
        string? command = isShell ? Str(input, "command") : null;
        if (command != null && CommandRules.IsAllowed(this.RepoRoot, command, viaShell: true))
        {
            return new PermissionDecision(true);
        }

        IChatSink? chatSink = this.sink;
        if (chatSink is null)
        {
            return new PermissionDecision(false, "No one is available to approve this.");
        }

        await this.approvalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            (ActivityKind kind, string title, string detail) = this.DescribeRequest(toolName, input);
            bool isEdit = EditTools.Contains(toolName);
            FileChange? change = isEdit && this.FullPath(input) is string path
                ? FileChange.ForClaudeCodeTool(toolName, input, path, FileChange.ReadOrEmpty(path))
                : null;
            WithDiff(toolName, change, ref kind, ref detail);
            bool isBuild = toolName == ApprovalServer.BuildTool;
            bool isPackage = toolName is ApprovalServer.AddPackageTool or ApprovalServer.RemovePackageTool;
            string? rule = command is null ? null : CommandRules.RuleFor(command, viaShell: true);
            string? allowAllLabel = isEdit ? "Allow all edits" : isBuild ? "Always allow builds here" : isPackage ? "Always allow package changes here" : rule != null ? $"Always allow \"{rule}\" here" : null;

            // Only some of an edit can be kept if the extension can write the file itself: a text file inside the repository.
            bool canKeepSome = change != null && toolName != "NotebookEdit" && this.InsideRepo(change.FullPath);

            // The chat UI must be touched on Visual Studio's main thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.runToken);
            chatSink.SetStatus("Waiting for your approval…");
            ApprovalResult result = await chatSink.RequestApprovalAsync(kind, title, detail, allowAllLabel, this.runToken, change, canKeepSome);
            Approval approval = result.Decision;

            if (approval == Approval.AllowAll && isEdit)
            {
                this.editsPreApproved = true;
            }
            else if (approval == Approval.AllowAll && isBuild)
            {
                CommandRules.Allow(this.RepoRoot, "build_solution");
            }
            else if (approval == Approval.AllowAll && isPackage)
            {
                CommandRules.Allow(this.RepoRoot, PackageTools.RuleKey);
            }
            else if (approval == Approval.AllowAll && rule != null)
            {
                CommandRules.Allow(this.RepoRoot, rule);
            }

            if (approval != Approval.Deny && result.PartialAfter is string partial && change != null)
            {
                return this.WriteKeptChanges(change, partial, toolUseId);
            }

            return approval == Approval.Deny
                ? new PermissionDecision(false, "The user declined this action. Don't retry it; ask what they'd like instead.")
                : new PermissionDecision(true);
        }
        catch (OperationCanceledException)
        {
            return new PermissionDecision(false, "Cancelled by the user.");
        }
        finally
        {
            this.approvalGate.Release();
        }
    }

    /// <summary>
    /// Shows Claude Code's AskUserQuestion questions in the question card. Answers go back as the tool's input:
    /// the original questions plus an "answers" map from question text to the chosen label(s) or typed text.
    /// </summary>
    private async Task<PermissionDecision> AskQuestionsAsync(JsonElement input)
    {
        IReadOnlyList<UserQuestion> questions = UserQuestion.ParseAll(input);
        if (questions.Count == 0 || this.sink is not IChatSink chatSink)
        {
            return new PermissionDecision(false, "Couldn't show these questions; ask in plain text instead.");
        }

        await this.approvalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.runToken);
            chatSink.SetStatus("Waiting for your answer…");
            IReadOnlyDictionary<string, string>? answers = await chatSink.AskQuestionsAsync(questions, this.runToken);
            if (answers is null)
            {
                return new PermissionDecision(false, "The user declined to answer these questions. Continue with your best judgement, or ask in plain text.");
            }

            string updatedInput = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["questions"] = input.GetProperty("questions"),
                ["answers"] = answers,
            });
            return new PermissionDecision(true, UpdatedInputJson: updatedInput);
        }
        catch (OperationCanceledException)
        {
            return new PermissionDecision(false, "Cancelled by the user.");
        }
        finally
        {
            this.approvalGate.Release();
        }
    }

    /// <summary>
    /// The user approved only some of an edit: the extension writes those changes itself (in the file's own encoding)
    /// and tells Claude Code no, with a message saying what was applied and what was left out.
    /// </summary>
    private PermissionDecision WriteKeptChanges(FileChange change, string kept, string? toolUseId)
    {
        string relative = this.Relative(change.FullPath);
        try
        {
            var repoFiles = new RepoFiles(this.RepoRoot);
            string resolved = repoFiles.Resolve(relative);
            System.Text.Encoding encoding = File.Exists(resolved) ? repoFiles.Read(relative).Encoding : new UTF8Encoding(false);
            if (!string.Equals(FileChange.ReadOrEmpty(resolved), change.Before, StringComparison.Ordinal) || OpenDocuments.HasUnsavedChanges(resolved))
            {
                return new PermissionDecision(false, $"{relative} changed while waiting for approval, so nothing was written. Read it again first.");
            }

            repoFiles.Write(resolved, kept, encoding);
        }
        catch (Exception ex) when (ex is ToolInputException or IOException or UnauthorizedAccessException)
        {
            return new PermissionDecision(false, $"The user approved part of this edit, but it couldn't be written: {ex.Message}");
        }

        if (toolUseId != null)
        {
            this.partlyApplied[toolUseId] = change with { After = kept };
        }

        return new PermissionDecision(false,
            $"The user applied only some of this change to {relative} (the extension wrote it, so this tool call itself was not run). " +
            $"They left out the changes below (a diff from what the file now contains to what you proposed):\n{UnifiedDiff.Create(kept, change.After)}\n" +
            "Don't make the left-out changes again unless the user asks for them. Read the file again before editing it further.");
    }

    private bool InsideRepo(string fullPath) =>
        fullPath.StartsWith(this.RepoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
