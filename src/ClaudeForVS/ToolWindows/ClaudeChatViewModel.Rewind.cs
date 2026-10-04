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
    private async Task ShowChangedFilesAsync(string root, ChatEntry prompt, ChatEntry? usageEntry)
    {
        if (prompt.SnapshotBefore is not string before || prompt.SnapshotAfter is not string after || before == after || !this.Messages.Contains(prompt))
        {
            return;
        }

        IReadOnlyList<FileStat> files;
        try
        {
            files = await WorkingTreeSnapshot.StatsAsync(root, before, after, CancellationToken.None);
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
            string list = string.Join("\n", changes.Take(15).Select(c => $"    {c.Status switch { 'A' => "delete", 'D' => "restore", _ => "revert" }}  {c.Path}"))
                + (changes.Count > 15 ? $"\n    … and {changes.Count - 15} more" : string.Empty);
            string warning = later.Count > 0
                ? $"\n\n{later.Count} of these {(later.Count == 1 ? "file has" : "files have")} changed since that message finished (your own edits or later messages). Those changes will be lost too."
                : string.Empty;
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
