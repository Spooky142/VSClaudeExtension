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

/// <summary>Tools for git, searching and the repository's files, and what edits share.</summary>
internal sealed partial class ClaudeAgent
{
    private async Task<string> RunGitAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        if (!input.TryGetValue("args", out JsonElement argsEl) || argsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ToolInputException("`args` must be an array of strings.");
        }

        List<string> args = argsEl.EnumerateArray()
            .Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : throw new ToolInputException("`args` must contain only strings."))
            .ToList();
        if (args.Count == 0)
        {
            throw new ToolInputException("`args` must not be empty.");
        }

        string commandLine = "git " + string.Join(" ", args.Select(GitRunner.QuoteArgument));
        PermissionVerdict verdict = this.permissions.ForCommand(commandLine);
        if (verdict == PermissionVerdict.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, commandLine, "Denied by your permission settings.");
            return SettingsRefusal;
        }

        bool readOnly = GitRunner.IsReadOnly(args);
        if (!readOnly && this.Mode == AgentMode.Plan)
        {
            sink.ToolActivity(ActivityKind.Declined, commandLine, "Blocked in Plan mode.");
            return PlanModeRefusal;
        }

        if (verdict == PermissionVerdict.Ask
            || (!readOnly && this.Mode == AgentMode.Coding && verdict != PermissionVerdict.Allow && !CommandRules.IsAllowed(this.RepoRoot, commandLine, viaShell: false)))
        {
            sink.SetStatus("Waiting for your approval…");
            string? rule = verdict == PermissionVerdict.Ask ? null : CommandRules.RuleFor(commandLine, viaShell: false);
            Approval approval = (await sink.RequestApprovalAsync(ActivityKind.Git, commandLine, string.Empty, rule is null ? null : $"Always allow \"{rule}\" here", cancellationToken)).Decision;
            if (approval == Approval.Deny)
            {
                sink.ToolActivity(ActivityKind.Declined, commandLine, "Declined by you.");
                return "The user declined to run this command.";
            }

            if (approval == Approval.AllowAll && rule != null)
            {
                CommandRules.Allow(this.RepoRoot, rule);
            }
        }

        sink.SetStatus($"Running {commandLine}");
        (int exitCode, string output) = await GitRunner.RunAsync(this.RepoRoot, args, cancellationToken);
        sink.ToolActivity(ActivityKind.Git, commandLine, output.Length == 0 ? "(no output)" : output);
        return exitCode == 0
            ? (output.Length == 0 ? "(no output)" : output)
            : $"git exited with code {exitCode}.\n{output}";
    }

    private async Task<string> GlobAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string pattern = RequireString(input, "pattern");
        string folder = this.OptionalRepoPath(input, "path", folderOnly: true);
        string where = folder.Length > 0 ? $" in {folder}" : string.Empty;

        sink.SetStatus($"Finding {pattern}{where}");
        IReadOnlyList<string> all = this.Readable(await RepoSearch.ListFilesAsync(this.RepoRoot, cancellationToken));
        IReadOnlyList<string> matches = RepoSearch.Glob(all, pattern, folder);
        string list = string.Join("\n", matches.Take(RepoSearch.MaxGlobResults));
        string more = matches.Count > RepoSearch.MaxGlobResults
            ? $"\n[{matches.Count - RepoSearch.MaxGlobResults} more files; narrow the pattern or path.]"
            : string.Empty;

        string count = matches.Count == 1 ? "1 file" : $"{matches.Count} files";
        sink.ToolActivity(ActivityKind.Search, $"Glob {pattern}{where}", matches.Count == 0 ? "No files found." : $"{count}\n{list}{more}");
        return matches.Count == 0 ? "No files found." : list + more;
    }

    private async Task<string> GrepAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string pattern = RequireString(input, "pattern");
        string scope = this.OptionalRepoPath(input, "path", folderOnly: false);
        string? glob = input.TryGetValue("glob", out JsonElement g) && g.ValueKind == JsonValueKind.String && g.GetString()!.Trim().Length > 0
            ? g.GetString()!.Trim()
            : null;
        bool ignoreCase = input.TryGetValue("case_insensitive", out JsonElement ci) && ci.ValueKind == JsonValueKind.True;
        string modeName = input.TryGetValue("output_mode", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "content";
        GrepOutput mode = modeName switch
        {
            "content" => GrepOutput.Content,
            "files_with_matches" => GrepOutput.FilesWithMatches,
            "count" => GrepOutput.Count,
            _ => throw new ToolInputException("`output_mode` must be \"content\", \"files_with_matches\" or \"count\"."),
        };
        int context = Math.Max(0, Math.Min(10, OptionalInt(input, "context") ?? 0));
        int maxResults = Math.Max(1, Math.Min(2000, OptionalInt(input, "max_results") ?? 200));

        string where = (scope.Length > 0 ? $" in {scope}" : string.Empty) + (glob is null ? string.Empty : $" ({glob})");
        sink.SetStatus($"Searching for {pattern}{where}");
        IReadOnlyList<string> all = this.Readable(await RepoSearch.ListFilesAsync(this.RepoRoot, cancellationToken));
        bool scopeIsFile = scope.Length > 0 && File.Exists(Path.Combine(this.RepoRoot, scope));
        IEnumerable<string> files = glob is null || scopeIsFile ? RepoSearch.Under(all, scope) : RepoSearch.Glob(all, glob, scope);
        string result = await Task.Run(
            () => RepoSearch.Grep(this.RepoRoot, files, pattern, ignoreCase, mode, context, maxResults, cancellationToken), cancellationToken);
        sink.ToolActivity(ActivityKind.Search, $"Grep {pattern}{where}", result);
        return result;
    }

    /// <summary>The files glob and grep may show: all of them, less any the permission rules deny reading.</summary>
    private IReadOnlyList<string> Readable(IReadOnlyList<string> files) => !this.permissions.HasReadDenials
        ? files
        : files.Where(f => this.permissions.ForRead(Path.Combine(this.RepoRoot, f.Replace('/', Path.DirectorySeparatorChar))) != PermissionVerdict.Deny).ToList();

    /// <summary>An optional repository-relative path argument, checked and normalized to forward slashes; empty for the root.</summary>
    private string OptionalRepoPath(IReadOnlyDictionary<string, JsonElement> input, string name, bool folderOnly)
    {
        if (!input.TryGetValue(name, out JsonElement el) || el.ValueKind != JsonValueKind.String
            || el.GetString()!.Trim() is not { Length: > 0 } path || path is "." or "/" or "\\")
        {
            return string.Empty;
        }

        string full = this.files.Resolve(path);
        if (!Directory.Exists(full) && (folderOnly || !File.Exists(full)))
        {
            throw new ToolInputException(folderOnly ? $"Directory not found: {path}" : $"Not found: {path}");
        }

        return this.files.Relative(full).Replace('\\', '/');
    }

    private async Task<string> ReadFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string path = RequireString(input, "path");
        string full = this.files.Resolve(path);
        switch (this.permissions.ForRead(full))
        {
            case PermissionVerdict.Deny:
                sink.ToolActivity(ActivityKind.Declined, $"Read {this.files.Relative(full)}", "Denied by your permission settings.");
                return SettingsRefusal;
            case PermissionVerdict.Ask:
                sink.SetStatus("Waiting for your approval…");
                if ((await sink.RequestApprovalAsync(ActivityKind.Read, $"Read {this.files.Relative(full)}", string.Empty, null, cancellationToken)).Decision == Approval.Deny)
                {
                    sink.ToolActivity(ActivityKind.Declined, $"Read {this.files.Relative(full)}", "Declined by you.");
                    return "The user declined to let you read this file.";
                }

                break;
        }

        return this.ReadFile(path, input, sink);
    }

    private string ReadFile(string path, IReadOnlyDictionary<string, JsonElement> input, IChatSink sink)
    {
        RepoFiles.TextFile file = this.files.Read(path);
        string[] lines = file.Text.Replace("\r\n", "\n").Split('\n');
        int startLine = OptionalInt(input, "start_line") is int s ? Math.Max(1, s) : 1;
        int endLine = OptionalInt(input, "end_line") is int e ? Math.Min(lines.Length, e) : lines.Length;
        if (file.Text.Length == 0)
        {
            sink.ToolActivity(ActivityKind.Read, $"Read {this.files.Relative(file.FullPath)}", "empty file");
            return "(empty file)";
        }

        if (startLine > lines.Length)
        {
            throw new ToolInputException($"{path} has {lines.Length} line{(lines.Length == 1 ? "" : "s")}, so start_line {startLine} is past the end.");
        }

        if (startLine > endLine)
        {
            throw new ToolInputException($"end_line ({endLine}) is before start_line ({startLine}).");
        }

        var sb = new StringBuilder();
        for (int i = startLine; i <= endLine; i++)
        {
            sb.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
            if (sb.Length > MaxFileChars)
            {
                sb.Append($"[truncated at line {i} of {lines.Length}; use start_line/end_line to read more]\n");
                break;
            }
        }

        sink.ToolActivity(ActivityKind.Read, $"Read {this.files.Relative(file.FullPath)}", $"lines {startLine}-{endLine} of {lines.Length}");
        return sb.Length == 0 ? "(empty file)" : sb.ToString();
    }

    private async Task<string> EditFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string path = RequireString(input, "path");
        string oldString = RequireString(input, "old_string");
        string newString = RequireString(input, "new_string", allowEmpty: true);
        bool replaceAll = input.TryGetValue("replace_all", out JsonElement ra) && ra.ValueKind == JsonValueKind.True;
        if (oldString.Length == 0)
        {
            throw new ToolInputException("`old_string` must not be empty. Use write_file to create a file.");
        }

        RepoFiles.TextFile file = this.files.Read(path);
        string find = RepoFiles.NormalizeNewlines(oldString, file.Newline);
        string replace = RepoFiles.NormalizeNewlines(newString, file.Newline);

        int count = CountOccurrences(file.Text, find);
        if (count == 0)
        {
            throw new ToolInputException($"`old_string` was not found in {path}. Read the file again and copy the text exactly (without line numbers).");
        }

        if (count > 1 && !replaceAll)
        {
            throw new ToolInputException($"`old_string` appears {count} times in {path}. Include more surrounding context to make it unique, or set replace_all.");
        }

        string relative = this.files.Relative(file.FullPath);
        string updated = replaceAll ? file.Text.Replace(find, replace) : ReplaceFirst(file.Text, find, replace);
        string diff = UnifiedDiff.Create(file.Text, updated);
        var change = new FileChange(file.FullPath, file.Text, updated);
        ThrowIfUnsaved(file.FullPath, relative);
        ApprovalResult approval = await this.ApproveEditAsync(ActivityKind.Edit, $"Edit {relative}", diff, change, sink, cancellationToken, [file.FullPath], canKeepSome: true);
        if (approval.Decision == Approval.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, $"Edit {relative}", "Declined by you.");
            return "The user declined this edit.";
        }

        this.ThrowIfChanged(path, file.Text);
        if (approval.PartialAfter is string partial)
        {
            this.files.Write(file.FullPath, partial, file.Encoding);
            sink.ToolActivity(ActivityKind.Edit, $"Edited {relative} (some changes left out)", UnifiedDiff.Create(file.Text, partial), change with { After = partial });
            return PartlyApplied(relative, partial, updated);
        }

        this.files.Write(file.FullPath, updated, file.Encoding);
        sink.ToolActivity(ActivityKind.Edit, $"Edited {relative}", diff, change);
        return $"Edited {relative} ({count} replacement{(count == 1 ? "" : "s")}).";
    }

    /// <summary>Tells Claude which parts of a change the user left out when they approved only some of it.</summary>
    private static string PartlyApplied(string relative, string written, string proposed) =>
        $"The user applied only some of this change to {relative}. They left out the changes below (a diff from what the file now " +
        $"contains to what you proposed):\n{UnifiedDiff.Create(written, proposed)}\n" +
        "Don't make the left-out changes again unless the user asks for them. Read the file again before editing it further.";

    private async Task<string> WriteFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string path = RequireString(input, "path");
        string content = RequireString(input, "content", allowEmpty: true);
        string full = this.files.Resolve(path);
        string relative = this.files.Relative(full);

        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string newline = "\r\n";
        string action = "Create";
        string before = string.Empty;
        if (File.Exists(full))
        {
            RepoFiles.TextFile existing = this.files.Read(path);
            encoding = existing.Encoding;
            newline = existing.Newline;
            before = existing.Text;
            action = "Overwrite";
        }
        else if (Directory.Exists(full))
        {
            throw new ToolInputException($"{path} is a directory.");
        }

        string after = RepoFiles.NormalizeNewlines(content, newline);

        // A new file is shown whole; an overwritten one as what changed.
        ActivityKind kind = action == "Create" ? ActivityKind.Create : ActivityKind.Edit;
        string preview = action == "Create" ? Preview(content) : UnifiedDiff.Create(before, after);
        var change = new FileChange(full, before, after);
        ThrowIfUnsaved(full, relative);
        ApprovalResult approval = await this.ApproveEditAsync(kind, $"{action} {relative}", preview, change, sink, cancellationToken, [full], canKeepSome: action != "Create");
        if (approval.Decision == Approval.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, $"{action} {relative}", "Declined by you.");
            return "The user declined this change.";
        }

        this.ThrowIfChanged(path, action == "Create" ? null : before);
        if (approval.PartialAfter is string partial)
        {
            this.files.Write(full, partial, encoding);
            sink.ToolActivity(ActivityKind.Edit, $"Overwrote {relative} (some changes left out)", UnifiedDiff.Create(before, partial), change with { After = partial });
            return PartlyApplied(relative, partial, after);
        }

        this.files.Write(full, after, encoding);
        sink.ToolActivity(kind, action == "Create" ? $"Created {relative}" : $"Overwrote {relative}", preview, change);
        return $"{(action == "Create" ? "Created" : "Overwrote")} {relative}.";
    }

    private async Task<string> DeleteFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string path = RequireString(input, "path");
        string full = this.files.Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ToolInputException($"{path} is a directory; only files can be deleted.");
        }

        if (!File.Exists(full))
        {
            throw new ToolInputException($"File not found: {path}");
        }

        // Binary files can be deleted too, just without a preview.
        string? before = null;
        try
        {
            before = this.files.Read(path).Text;
        }
        catch (ToolInputException)
        {
        }

        string relative = this.files.Relative(full);
        string preview = before is null ? "(binary file)" : Removed(before);
        FileChange? change = before is null ? null : new FileChange(full, before, string.Empty);
        ThrowIfUnsaved(full, relative);
        if ((await this.ApproveEditAsync(ActivityKind.Edit, $"Delete {relative}", preview, change, sink, cancellationToken, [full])).Decision == Approval.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, $"Delete {relative}", "Declined by you.");
            return "The user declined to delete this file.";
        }

        if (before != null)
        {
            this.ThrowIfChanged(path, before);
        }
        else
        {
            ThrowIfUnsaved(full, relative);
        }

        File.Delete(full);
        sink.ToolActivity(ActivityKind.Edit, $"Deleted {relative}", preview, change);
        return $"Deleted {relative}.";
    }

    private async Task<string> MoveFileAsync(IReadOnlyDictionary<string, JsonElement> input, IChatSink sink, CancellationToken cancellationToken)
    {
        string from = RequireString(input, "from");
        string to = RequireString(input, "to");
        string source = this.files.Resolve(from);
        string destination = this.files.Resolve(to);
        if (Directory.Exists(source))
        {
            throw new ToolInputException($"{from} is a directory; only files can be moved.");
        }

        if (!File.Exists(source))
        {
            throw new ToolInputException($"File not found: {from}");
        }

        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            throw new ToolInputException("`from` and `to` are the same path.");
        }

        // A change of case only is a rename of the same file, so the destination "exists" already.
        bool caseOnly = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new ToolInputException($"{to} already exists. Delete it first if it should be replaced.");
        }

        string title = $"{this.files.Relative(source)} → {this.files.Relative(destination)}";
        ThrowIfUnsaved(source, this.files.Relative(source));
        if ((await this.ApproveEditAsync(ActivityKind.Edit, "Move " + title, string.Empty, null, sink, cancellationToken, [source, destination])).Decision == Approval.Deny)
        {
            sink.ToolActivity(ActivityKind.Declined, "Move " + title, "Declined by you.");
            return "The user declined to move this file.";
        }

        // Things may have moved on while the user was deciding.
        ThrowIfUnsaved(source, this.files.Relative(source));
        if (!File.Exists(source) || (!caseOnly && (File.Exists(destination) || Directory.Exists(destination))))
        {
            throw new ToolInputException($"{from} or {to} changed while waiting for approval, so the file wasn't moved. Check them again first.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        sink.ToolActivity(ActivityKind.Edit, "Moved " + title, string.Empty);
        return $"Moved {title}.";
    }

    /// <summary>Refuses to change a file the user is editing in Visual Studio, rather than racing their unsaved changes.</summary>
    private static void ThrowIfUnsaved(string fullPath, string relative)
    {
        if (OpenDocuments.HasUnsavedChanges(fullPath))
        {
            throw new ToolInputException(
                $"{relative} has unsaved changes in Visual Studio, so it wasn't changed. Ask the user to save or discard them, then read the file again.");
        }
    }

    /// <summary>
    /// Refuses the change if the file isn't as it was when the change was worked out (null: didn't exist), e.g.
    /// because the user edited it while deciding, so their work isn't overwritten.
    /// </summary>
    private void ThrowIfChanged(string path, string? expected)
    {
        string full = this.files.Resolve(path);
        ThrowIfUnsaved(full, this.files.Relative(full));
        string? now = File.Exists(full) ? this.files.Read(path).Text : null;
        if (!string.Equals(now, expected, StringComparison.Ordinal))
        {
            throw new ToolInputException($"{path} changed while waiting for approval, so the change wasn't made. Read it again first.");
        }
    }

    /// <param name="paths">The files the change touches, checked against the permission rules.</param>
    /// <param name="canKeepSome">True if the caller can write just the changes the user keeps (see <see cref="ApprovalResult.PartialAfter"/>).</param>
    private async Task<ApprovalResult> ApproveEditAsync(
        ActivityKind kind, string title, string detail, FileChange? change, IChatSink sink, CancellationToken cancellationToken, IReadOnlyList<string> paths, bool canKeepSome = false)
    {
        if (this.Mode == AgentMode.Plan)
        {
            throw new ToolInputException(PlanModeRefusal);
        }

        // The strictest rule for any of the files wins: deny, then ask, then allow.
        PermissionVerdict verdict = paths.Select(this.permissions.ForEdit).DefaultIfEmpty(PermissionVerdict.None).Max();
        if (verdict == PermissionVerdict.Deny)
        {
            throw new ToolInputException(SettingsRefusal);
        }

        if (verdict != PermissionVerdict.Ask && (this.editsPreApproved || this.Mode == AgentMode.Auto || verdict == PermissionVerdict.Allow))
        {
            return Approval.Allow;
        }

        sink.SetStatus("Waiting for your approval…");
        ApprovalResult approval = await sink.RequestApprovalAsync(kind, title, detail, verdict == PermissionVerdict.Ask ? null : "Allow all edits", cancellationToken, change, canKeepSome);
        if (approval.Decision == Approval.AllowAll)
        {
            this.editsPreApproved = true;
        }

        return approval;
    }

    private static string RequireString(IReadOnlyDictionary<string, JsonElement> input, string name, bool allowEmpty = false)
    {
        if (!input.TryGetValue(name, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            throw new ToolInputException($"`{name}` must be a string.");
        }

        string value = el.GetString()!;
        if (!allowEmpty && value.Length == 0)
        {
            throw new ToolInputException($"`{name}` must not be empty.");
        }

        return value;
    }

    private static int? OptionalInt(IReadOnlyDictionary<string, JsonElement> input, string name)
        => input.TryGetValue(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : null;

    private static int CountOccurrences(string text, string find)
    {
        int count = 0;
        for (int i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        int i = text.IndexOf(find, StringComparison.Ordinal);
        return text.Substring(0, i) + replace + text.Substring(i + find.Length);
    }

    private static string Preview(string content)
    {
        string[] lines = Lines(content);
        string head = string.Join("\n", lines.Take(PreviewLines));
        return lines.Length > PreviewLines ? head + $"\n… ({lines.Length - PreviewLines} more lines)" : head;
    }

    private static string Removed(string content)
    {
        string[] lines = Lines(content);
        string head = string.Join("\n", lines.Take(PreviewLines).Select(l => "- " + l));
        return lines.Length > PreviewLines ? head + $"\n… ({lines.Length - PreviewLines} more lines)" : head;
    }

    private static string[] Lines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');
}
