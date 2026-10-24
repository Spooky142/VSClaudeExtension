using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

namespace ClaudeForVS.ToolWindows;

/// <summary>Going back: editing or retrying an earlier prompt, and undoing the file changes made since one.</summary>
internal sealed partial class ClaudeChatViewModel
{
    /// <summary>Takes a prompt and everything after it out of the chat, and puts its text back in the prompt box.</summary>
    public void Edit(ChatEntry entry)
    {
        if (this.IsBusy || !this.RewindTo(entry))
        {
            return;
        }

        this.EditRequested?.Invoke(this, entry.Text);
        this.Status = "Edit your message and send it again. Files Claude changed are left as they are.";
    }

    /// <summary>Takes a prompt and everything after it out of the chat, and sends it again.</summary>
    public Task RetryAsync(ChatEntry entry)
    {
        if (this.IsBusy)
        {
            return Task.CompletedTask;
        }

        ChatPrompt prompt = entry.SentPrompt ?? new ChatPrompt(entry.Text);
        return this.RewindTo(entry) && this.TrySendPrompt(entry.Text, prompt, entry.Note, out Task run) ? run : Task.CompletedTask;
    }

    /// <summary>Rewinds the conversation to just before a prompt and removes the prompt and everything after it from the transcript.</summary>
    private bool RewindTo(ChatEntry entry)
    {
        int index = this.Messages.IndexOf(entry);
        if (index < 0)
        {
            return false;
        }

        string? checkpoint = entry.Checkpoint;
        if (this.engine != null)
        {
            if (checkpoint is null || !this.engine.Rewind(checkpoint))
            {
                if (this.engine is not ClaudeAgent)
                {
                    this.Status = "This message can't be edited any more: the conversation has changed since (it may have been compacted).";
                    return false;
                }

                // An API conversation can always be rebuilt from what's left of the transcript.
                this.resumeFrom = new SessionInfo { RepoPath = this.engine.RepoRoot, Engine = ChatEngineKind.Api };
                this.DisposeEngine();
            }
        }
        else if (this.resumeFrom is { Engine: ChatEngineKind.ClaudeCode } info)
        {
            if (checkpoint is null)
            {
                this.Status = "This message can't be edited: it was sent by an older version of the extension.";
                return false;
            }

            this.resumeFrom = info with { ClaudeCodeResumeAt = checkpoint };
        }

        // Otherwise there's no engine yet and an API chat is rebuilt from the transcript, so trimming it is enough.
        while (this.Messages.Count > index)
        {
            this.Messages.RemoveAt(this.Messages.Count - 1);
        }

        this.currentAssistant = null;
        this.currentThinking = null;
        if (this.chatId != null && !this.Messages.Any(m => m.Kind == ChatEntryKind.User))
        {
            SessionStore.Delete(this.chatId); // saved again, under the same ID, by the next message
        }
        else
        {
            this.SaveChat();
        }

        return true;
    }

    /// <summary>
    /// Adds a row listing the files a prompt changed, with lines added and removed, under Claude's reply and above the
    /// prompt's token count. Nothing is added if no files changed.
    /// </summary>
    private async Task ShowChangedFilesAsync(string root, ChatEntry prompt, ChatEntry? usageEntry, CancellationToken cancellationToken)
    {
        if (prompt.SnapshotBefore is not string before || prompt.SnapshotAfter is not string after || before == after || !this.Messages.Contains(prompt))
        {
            return;
        }

        IReadOnlyList<FileStat> files;
        try
        {
            files = await WorkingTreeSnapshot.StatsAsync(root, before, after, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        if (files.Count == 0)
        {
            return;
        }

        var entry = new ChatEntry(ChatEntryKind.Changes, string.Empty) { Files = files, SnapshotBefore = before, SnapshotAfter = after };
        int at = usageEntry is null ? -1 : this.Messages.IndexOf(usageEntry);
        if (at >= 0)
        {
            this.Messages.Insert(at, entry);
        }
        else
        {
            this.Messages.Add(entry);
        }
    }

    /// <summary>A changed file's text before and after the prompt that changed it, for the diff viewer.</summary>
    public async Task<FileChange?> LoadChangeAsync(ChangedFileRow row)
    {
        if (row.Entry.SnapshotBefore is not string before || row.Entry.SnapshotAfter is not string after || this.RepoRoot is not string root)
        {
            return null;
        }

        try
        {
            string? oldText = row.Stat.Status == 'A' ? string.Empty : await WorkingTreeSnapshot.ReadFileAsync(root, before, row.Path, CancellationToken.None);
            string? newText = row.Stat.Status == 'D' ? string.Empty : await WorkingTreeSnapshot.ReadFileAsync(root, after, row.Path, CancellationToken.None);
            if (oldText is null || newText is null)
            {
                this.Status = "That change can't be shown any more: git no longer has the snapshot.";
                return null;
            }

            // Git may store LF where the working tree has CRLF (core.autocrlf); match the file so only real changes show.
            string full = Path.Combine(root, row.Path.Replace('/', Path.DirectorySeparatorChar));
            if (FileChange.ReadOrEmpty(full).Contains("\r\n"))
            {
                oldText = RepoFiles.NormalizeNewlines(oldText, "\r\n");
                newText = RepoFiles.NormalizeNewlines(newText, "\r\n");
            }

            return new FileChange(full, oldText, newText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            this.Status = "Couldn't read the change: " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Files a prompt changed that Claude's own file edits don't account for, so were changed while it ran by the user,
    /// another program or a command it ran. A file Claude edited counts if it ended up as Claude left it; after a chat
    /// is reopened the edits' contents aren't known, so a tool row naming the file is taken as accounting for it.
    /// </summary>
    private async Task<List<string>> UnexplainedChangesAsync(string root, ChatEntry prompt)
    {
        var result = new List<string>();
        if (prompt.SnapshotBefore is not string before || prompt.SnapshotAfter is not string after || before == after)
        {
            return result;
        }

        int start = this.Messages.IndexOf(prompt);
        List<ChatEntry> tools = this.Messages.Skip(start + 1)
            .TakeWhile(m => m.Kind != ChatEntryKind.User)
            .Where(m => m.Kind == ChatEntryKind.Tool && m.Activity != ActivityKind.Declined && m.Activity != ActivityKind.Failed)
            .ToList();

        // What Claude's file tools last left in each file.
        string prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChatEntry tool in tools)
        {
            if (tool.Change is FileChange change && change.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                written[change.FullPath.Substring(prefix.Length).Replace('\\', '/')] = change.After;
            }
        }

        static string Normalize(string text) => text.TrimStart('﻿').Replace("\r\n", "\n");

        foreach (SnapshotChange change in await WorkingTreeSnapshot.ChangesAsync(root, before, after, CancellationToken.None))
        {
            if (written.TryGetValue(change.Path, out string? expected))
            {
                string? actual = change.Status == 'D' ? string.Empty : await WorkingTreeSnapshot.ReadFileAsync(root, after, change.Path, CancellationToken.None);
                if (actual != null && Normalize(actual) == Normalize(expected))
                {
                    continue;
                }
            }
            else if (tools.Any(t => NamesPath(t.Header, change.Path)))
            {
                continue; // e.g. a move, a binary delete, or an edit in a reopened chat
            }

            result.Add(change.Path);
        }

        return result;
    }

    /// <summary>True if a tool row's title mentions the repository-relative path as a whole word.</summary>
    private static bool NamesPath(string title, string path)
    {
        string normalized = title.Replace('\\', '/');
        for (int at = normalized.IndexOf(path, StringComparison.OrdinalIgnoreCase); at >= 0; at = normalized.IndexOf(path, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + path.Length;
            if ((at == 0 || normalized[at - 1] == ' ') && (end == normalized.Length || normalized[end] == ' '))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The files a restore would touch, one per line, with what would happen to each.</summary>
    private static string DescribeRestore(IReadOnlyList<SnapshotChange> changes) =>
        string.Join("\n", changes.Take(15).Select(c => $"    {c.Status switch { 'A' => "delete", 'D' => "restore", _ => "revert" }}  {c.Path}"))
        + (changes.Count > 15 ? $"\n    … and {changes.Count - 15} more" : string.Empty);

    /// <summary>Puts one file a prompt changed back the way it was before that prompt, after the user confirms.</summary>
    public async Task RevertFileAsync(ChangedFileRow row)
    {
        if (this.IsBusy || row.Entry.SnapshotBefore is not string before || this.RepoRoot is not string root)
        {
            return;
        }

        this.IsBusy = true;
        try
        {
            this.Status = "Checking the file…";
            string? now = await WorkingTreeSnapshot.CaptureAsync(root, CancellationToken.None);
            if (now is null)
            {
                this.Status = "Couldn't read the working tree, so nothing was reverted.";
                return;
            }

            SnapshotChange? change = (await WorkingTreeSnapshot.ChangesAsync(root, before, now, CancellationToken.None))
                .FirstOrDefault(c => string.Equals(c.Path, row.Path, StringComparison.OrdinalIgnoreCase));
            this.Status = string.Empty;
            if (change is null)
            {
                this.Status = $"{row.Path} is already the way it was before that message.";
                return;
            }

            bool editedSince = row.Entry.SnapshotAfter is string after && after != now
                && (await WorkingTreeSnapshot.ChangesAsync(root, after, now, CancellationToken.None)).Any(c => string.Equals(c.Path, row.Path, StringComparison.OrdinalIgnoreCase));
            string verb = change.Status switch { 'A' => "Delete", 'D' => "Restore", _ => "Put back" };
            string warning = editedSince ? "\n\nThis file has changed since that message finished (your own edits or later messages). Those changes will be lost too." : string.Empty;
            if (!this.Confirm($"{verb} {row.Path} the way it was before this message?{warning}"))
            {
                return;
            }

            if (await WorkingTreeSnapshot.RestoreAsync(root, before, [change], CancellationToken.None) is string error)
            {
                this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Couldn't revert the file", error));
                return;
            }

            this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"Reverted {row.Path}"));
            this.PromptSent?.Invoke(this, EventArgs.Empty);
            this.SaveChat();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Couldn't revert the file", ex.Message));
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    /// <summary>/checkpoint: remembers how every file is right now, as a restore point in the transcript.</summary>
    public async Task CreateRestorePointAsync(string name)
    {
        if (this.IsBusy)
        {
            return;
        }

        if (this.RepoRoot is not string root)
        {
            this.Status = "Send a message or set the repository in Settings first, so there's a repository to save a restore point of.";
            return;
        }

        this.IsBusy = true;
        try
        {
            this.Status = "Saving a restore point…";
            string? tree = await WorkingTreeSnapshot.CaptureAsync(root, CancellationToken.None);
            this.Status = string.Empty;
            if (tree is null)
            {
                this.Status = "Couldn't read the working tree, so no restore point was saved.";
                return;
            }

            name = name.Trim();
            string label = name.Length > 0 ? name : DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
            this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"Restore point: {label}") { SnapshotBefore = tree });
            this.PromptSent?.Invoke(this, EventArgs.Empty);
            this.Status = "Saved a restore point. Restore it from its row or with /restore.";
            this.SaveChat();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            this.Status = "Couldn't save a restore point: " + ex.Message;
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    /// <summary>/restore [name]: restores the named restore point (the latest if no name is given).</summary>
    public Task RestoreCommandAsync(string name)
    {
        List<ChatEntry> points = this.Messages.Where(m => m.IsRestorePoint).ToList();
        name = name.Trim();
        ChatEntry? point = name.Length == 0
            ? points.LastOrDefault()
            : points.LastOrDefault(p => p.Header.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                ?? points.LastOrDefault(p => p.Header.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        if (point is null)
        {
            this.Status = points.Count == 0
                ? "There are no restore points in this chat. /checkpoint [name] saves one."
                : $"No restore point matches \"{name}\". They are: {string.Join(", ", points.Select(p => p.Header.Replace("Restore point: ", string.Empty)))}.";
            return Task.CompletedTask;
        }

        return this.RestoreToAsync(point);
    }

    /// <summary>Puts every file back the way it was at a restore point, after the user confirms the list.</summary>
    public async Task RestoreToAsync(ChatEntry point)
    {
        if (this.IsBusy || point.SnapshotBefore is not string tree || this.RepoRoot is not string root)
        {
            return;
        }

        this.IsBusy = true;
        try
        {
            this.Status = "Checking which files changed…";
            string? now = await WorkingTreeSnapshot.CaptureAsync(root, CancellationToken.None);
            if (now is null)
            {
                this.Status = "Couldn't read the working tree, so nothing was restored.";
                return;
            }

            IReadOnlyList<SnapshotChange> changes = await WorkingTreeSnapshot.ChangesAsync(root, tree, now, CancellationToken.None);
            this.Status = string.Empty;
            if (changes.Count == 0)
            {
                this.Status = "Nothing to restore: the files are already as they were at that restore point.";
                return;
            }

            string files = changes.Count == 1 ? "1 file" : $"{changes.Count} files";
            if (!this.Confirm($"Put {files} back the way {(changes.Count == 1 ? "it was" : "they were")} at \"{point.Header.Replace("Restore point: ", string.Empty)}\"?\n\n{DescribeRestore(changes)}\n\nChanges made to {(changes.Count == 1 ? "it" : "them")} since then, by Claude or by you, will be lost. The conversation is left as it is."))
            {
                return;
            }

            if (await WorkingTreeSnapshot.RestoreAsync(root, tree, changes, CancellationToken.None) is string error)
            {
                this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Couldn't restore every file", error));
                return;
            }

            this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"Restored {files} to \"{point.Header.Replace("Restore point: ", string.Empty)}\""));
            this.PromptSent?.Invoke(this, EventArgs.Empty);
            this.SaveChat();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Couldn't restore the files", ex.Message));
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    /// <summary>Puts every file changed since a prompt started back the way it was, after the user confirms the list.</summary>
    public async Task UndoAsync(ChatEntry entry)
    {
        if (this.IsBusy || entry.SnapshotBefore is not string before || this.RepoRoot is not string root)
        {
            return;
        }

        this.IsBusy = true;
        try
        {
            this.Status = "Checking which files changed…";
            string? now = await WorkingTreeSnapshot.CaptureAsync(root, CancellationToken.None);
            if (now is null)
            {
                this.Status = "Couldn't read the working tree, so nothing was undone.";
                return;
            }

            IReadOnlyList<SnapshotChange> changes = await WorkingTreeSnapshot.ChangesAsync(root, before, now, CancellationToken.None);
            this.Status = string.Empty;
            if (changes.Count == 0)
            {
                this.Status = "Nothing to undo: the files are already as they were before that message.";
                entry.SnapshotAfter = before;
                this.SaveChat();
                return;
            }

            // Files touched since the prompt finished: the user's own edits or later prompts' changes, which are lost too.
            IReadOnlyList<SnapshotChange> later = entry.SnapshotAfter is string after && after != now
                ? await WorkingTreeSnapshot.ChangesAsync(root, after, now, CancellationToken.None)
                : [];
            string list = DescribeRestore(changes);
            string warning = later.Count > 0
                ? $"\n\n{later.Count} of these {(later.Count == 1 ? "file has" : "files have")} changed since that message finished (your own edits or later messages). Those changes will be lost too."
                : string.Empty;

            // Files that changed while Claude was working, but not (or not only) through its own edits.
            var undone = new HashSet<string>(changes.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            var during = new List<string>();
            int promptIndex = this.Messages.IndexOf(entry);
            foreach (ChatEntry prompt in this.Messages.Skip(Math.Max(0, promptIndex)).Where(m => m.Kind == ChatEntryKind.User).ToList())
            {
                foreach (string path in await this.UnexplainedChangesAsync(root, prompt))
                {
                    if (undone.Contains(path) && !during.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        during.Add(path);
                    }
                }
            }

            if (during.Count > 0)
            {
                warning += "\n\nWhile Claude was working, "
                    + (during.Count == 1 ? "this file" : "these files")
                    + " also changed in ways its own edits don't account for (your edits, another program, or a command it ran). Undo puts "
                    + (during.Count == 1 ? "it" : "them")
                    + " back too:\n"
                    + string.Join("\n", during.Take(5).Select(p => "    " + p))
                    + (during.Count > 5 ? $"\n    … and {during.Count - 5} more" : string.Empty);
            }
            string files = changes.Count == 1 ? "1 file" : $"{changes.Count} files";
            if (!this.Confirm($"Put {files} back the way {(changes.Count == 1 ? "it was" : "they were")} before this message?\n\n{list}{warning}"))
            {
                return;
            }

            if (await WorkingTreeSnapshot.RestoreAsync(root, before, changes, CancellationToken.None) is string error)
            {
                this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Couldn't undo every change", error));
                return;
            }

            // Nothing is left to undo from this prompt on.
            int from = this.Messages.IndexOf(entry);
            foreach (ChatEntry later2 in this.Messages.Skip(Math.Max(0, from)).Where(m => m.Kind == ChatEntryKind.User))
            {
                later2.SnapshotAfter = later2.SnapshotBefore;
            }

            this.Messages.Add(new ChatEntry(ChatEntryKind.Notice, $"Undid changes to {files}"));
            this.PromptSent?.Invoke(this, EventArgs.Empty);
            this.SaveChat();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            this.Messages.Add(new ChatEntry(ChatEntryKind.Error, "Couldn't undo the changes", ex.Message));
        }
        finally
        {
            this.IsBusy = false;
        }
    }
}
