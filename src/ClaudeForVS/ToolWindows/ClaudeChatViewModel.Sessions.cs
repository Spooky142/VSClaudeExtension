using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>Saved chats: saving, reopening, searching, renaming and exporting them (the History tab).</summary>
internal sealed partial class ClaudeChatViewModel
{
    private string? chatId;
    private DateTimeOffset chatCreated;
    private string chatTitle = string.Empty;
    private bool chatPinned;

    /// <summary>A saved chat that was opened but hasn't sent a message yet; its context is given to the next engine.</summary>
    private SessionInfo? resumeFrom;

    private bool showHistory;
    private string sessionSearch = string.Empty;
    private bool thisRepoOnly;

    /// <summary>Saved chats, most recently used first. Refreshed whenever the History tab is shown.</summary>
    public ObservableCollection<SessionItem> Sessions { get; } = [];

    public bool HasSessions => this.Sessions.Count > 0;

    /// <summary>True when nothing has been saved yet.</summary>
    public bool NoSavedSessions { get; private set; }

    /// <summary>True when there are saved chats but the search or filter hides them all.</summary>
    public bool NoSessionMatches { get; private set; }

    /// <summary>Filters the History tab by title, repository, or (from three characters) anything said in the chat.</summary>
    public string SessionSearch
    {
        get => this.sessionSearch;
        set
        {
            this.Set(ref this.sessionSearch, value);
            _ = this.RefreshSessionsAsync(debounce: true);
        }
    }

    /// <summary>Shows only chats for the current repository.</summary>
    public bool ThisRepoOnly
    {
        get => this.thisRepoOnly;
        set
        {
            this.Set(ref this.thisRepoOnly, value);
            this.RefreshSessions();
        }
    }

    public bool ShowHistory
    {
        get => this.showHistory;
        set
        {
            this.Set(ref this.showHistory, value);
            this.OnPropertyChanged(nameof(this.ShowChat));
            if (value)
            {
                this.RefreshSessions();
            }
        }
    }

    public bool ShowChat
    {
        get => !this.showHistory;
        set => this.ShowHistory = !value;
    }

    public void RefreshSessions() => _ = this.RefreshSessionsAsync(debounce: false);

    // Cancels a refresh still in progress when a newer one starts.
    private CancellationTokenSource? sessionRefresh;

    /// <summary>
    /// Reloads the History list. The files are read and searched on a background thread, since searching transcripts
    /// means reading every chat; while typing, it waits for a pause first.
    /// </summary>
    private async Task RefreshSessionsAsync(bool debounce)
    {
        this.sessionRefresh?.Cancel();
        var cts = new CancellationTokenSource();
        this.sessionRefresh = cts;

        string query = this.sessionSearch.Trim();
        string repo = (this.RepoRoot ?? this.RepoPath).Trim().TrimEnd('\\', '/');
        bool repoOnly = this.thisRepoOnly;
        try
        {
            if (debounce && query.Length >= 3)
            {
                await Task.Delay(250, cts.Token);
            }

            (List<SessionInfo> matches, int total) = await Task.Run(() => FindSessions(query, repo, repoOnly, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            this.Sessions.Clear();
            foreach (SessionInfo info in matches)
            {
                this.Sessions.Add(new SessionItem(info, info.Id == this.chatId));
            }

            this.NoSavedSessions = total == 0;
            this.NoSessionMatches = total > 0 && this.Sessions.Count == 0;
            this.OnPropertyChanged(nameof(this.HasSessions));
            this.OnPropertyChanged(nameof(this.NoSavedSessions));
            this.OnPropertyChanged(nameof(this.NoSessionMatches));
        }
        catch (OperationCanceledException)
        {
            // A newer refresh replaced this one.
        }
    }

    /// <summary>The saved chats to list, pinned first; plus how many there are in all.</summary>
    private static (List<SessionInfo> Matches, int Total) FindSessions(string query, string repo, bool repoOnly, CancellationToken cancellationToken)
    {
        List<SessionInfo> all = SessionStore.List();
        var matches = new List<SessionInfo>();
        foreach (SessionInfo info in all.OrderByDescending(s => s.Pinned)) // stable: newest first within each group
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (repoOnly && !string.Equals(info.RepoPath.TrimEnd('\\', '/'), repo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Length > 0
                && info.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && Path.GetFileName(info.RepoPath.TrimEnd('\\', '/')).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && !(query.Length >= 3 && SessionStore.TranscriptContains(info, query)))
            {
                continue;
            }

            matches.Add(info);
        }

        return (matches, all.Count);
    }

    /// <summary>The summary saved for the current chat, or null before its first message.</summary>
    private SessionInfo? CurrentInfo()
    {
        if (this.chatId is null)
        {
            return null;
        }

        var code = this.engine as ClaudeCodeEngine;
        return new SessionInfo
        {
            Id = this.chatId,
            Title = this.chatTitle,
            RepoPath = this.engine?.RepoRoot ?? this.RepoPath,
            Engine = this.engine?.Kind ?? this.resumeFrom?.Engine ?? this.settings.Engine,
            Model = this.engine?.Model ?? this.resumeFrom?.Model ?? this.settings.Model,
            Created = this.chatCreated,
            Updated = DateTimeOffset.Now,
            MessageCount = this.Messages.Count(m => m.Kind is ChatEntryKind.User or ChatEntryKind.Assistant && !m.IsLocalReply),
            Pinned = this.chatPinned,

            // A reopened chat that hasn't sent anything yet keeps what it was opened with.
            ClaudeCodeSessionId = code != null ? code.SessionId : this.resumeFrom?.ClaudeCodeSessionId,
            ClaudeCodeLastMessageId = code != null ? code.LastMessageId : this.resumeFrom?.ClaudeCodeLastMessageId,
            ClaudeCodeResumeAt = code != null ? code.PendingRewind : this.resumeFrom?.ClaudeCodeResumeAt,
        };
    }

    private List<SavedEntry> SavedEntries(IEnumerable<ChatEntry>? entries = null) => (entries ?? this.Messages)
        .Select(m => m.Kind == ChatEntryKind.Changes
            ? new SavedEntry
            {
                Kind = ChatEntryKind.Tool,
                Header = ChatEntry.Summarize(m.Files),
                Detail = string.Join("\n", m.FileRows.Select(f => $"{f.StatusText,-8}  {f.AddedText} {f.RemovedText}  {f.Path}")),
                Activity = ActivityKind.Edit,
                SnapshotBefore = m.SnapshotBefore,
                SnapshotAfter = m.SnapshotAfter,
                Files = m.Files,
            }
            : new SavedEntry
            {
                Kind = m.Kind,
                Header = m.Header,
                Text = m.Text,
                Detail = string.Join("\n", m.DetailLines.Select(l => l.Text)),
                Activity = m.Activity,
                Note = m.HasNote ? m.Note : null,
                Checkpoint = m.Checkpoint,
                SnapshotBefore = m.SnapshotBefore,
                SnapshotAfter = m.SnapshotAfter,
                Starred = m.IsStarred,
            })
        .ToList();

    /// <summary>Writes the current chat to disk so it can be reopened later.</summary>
    private void SaveChat()
    {
        if (!this.Messages.Any(m => m.Kind == ChatEntryKind.User))
        {
            return;
        }

        if (this.chatId is null)
        {
            this.chatId = Guid.NewGuid().ToString("N");
            this.chatCreated = DateTimeOffset.Now;
            string first = this.Messages.First(m => m.Kind == ChatEntryKind.User).Text;
            string oneLine = string.Join(" ", first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            this.chatTitle = oneLine.Length > 80 ? oneLine.Substring(0, 80) + "…" : oneLine;
        }

        // The entries are copied here, on the UI thread; turning them into JSON and writing it happens in the background.
        _ = this.SaveChatAsync(this.CurrentInfo()!, this.SavedEntries());
    }

    private async Task SaveChatAsync(SessionInfo info, List<SavedEntry> entries)
    {
        try
        {
            await SessionStore.SaveInBackgroundAsync(info, entries);
        }
        catch (Exception ex)
        {
            this.Status = "Couldn't save this chat to history: " + ex.Message;
            return;
        }

        if (this.ShowHistory)
        {
            this.RefreshSessions();
        }
    }

    /// <summary>Replaces the current chat with a saved one. The next message continues it.</summary>
    /// <remarks>The transcript is read on a background thread, so a long chat doesn't hold up Visual Studio while it loads.</remarks>
    public async Task OpenSessionAsync(SessionItem item)
    {
        if (!this.CanOpenSession(item.Info))
        {
            return;
        }

        List<SavedEntry>? entries = await Task.Run(() => SessionStore.LoadEntries(item.Info.Id));
        if (entries is null)
        {
            this.Status = "This chat's saved transcript couldn't be read.";
            return;
        }

        this.ShowSession(item, entries);
    }

    private bool CanOpenSession(SessionInfo info)
    {
        if (this.IsBusy)
        {
            this.Status = "Stop the running prompt before opening another chat.";
            return false;
        }

        if (Instances.Any(other => other != this && other.chatId == info.Id))
        {
            this.Status = "This chat is open in another Claude window.";
            return false;
        }

        if (!Directory.Exists(info.RepoPath))
        {
            this.Status = $"The repository for this chat no longer exists: {info.RepoPath}";
            return false;
        }

        return true;
    }

    /// <param name="entries">The chat's transcript, already read.</param>
    private void ShowSession(SessionItem item, List<SavedEntry> entries)
    {
        SessionInfo info = item.Info;
        if (!this.CanOpenSession(info)) // checked again: things may have changed while the transcript was read
        {
            return;
        }

        this.DisposeEngine();
        ChatChanges.Forget(this);
        this.SetTasks([]);

        // Swapped in all at once, so the transcript is laid out once rather than after each entry.
        this.Messages.ReplaceAll(entries.Select(e =>
            // Chats saved by 0.3.9-0.3.10 kept the token count in the header.
            e.Kind == ChatEntryKind.Usage && e.Text.Length == 0
                ? new ChatEntry(e.Kind, string.Empty, e.Header)
                : e.Kind == ChatEntryKind.Tool && e.Files is { Count: > 0 }
                ? new ChatEntry(ChatEntryKind.Changes, string.Empty) { Files = e.Files, SnapshotBefore = e.SnapshotBefore, SnapshotAfter = e.SnapshotAfter }
                : new ChatEntry(e.Kind, e.Header, e.Text, e.Detail, e.Activity)
                {
                    Note = e.Note ?? string.Empty,
                    Checkpoint = e.Checkpoint,
                    SnapshotBefore = e.SnapshotBefore,
                    SnapshotAfter = e.SnapshotAfter,
                    IsStarred = e.Starred,
                }).ToList());

        this.ResetChatUsage();
        this.chatId = info.Id;
        this.chatCreated = info.Created;
        this.chatTitle = info.Title;
        this.chatPinned = info.Pinned;
        this.resumeFrom = info;
        this.RepoPath = info.RepoPath;

        // A Claude Code session can only be continued by Claude Code, so follow the chat's engine.
        string status = $"Opened \"{info.Title}\".";
        if (info.Engine != this.settings.Engine)
        {
            AgentSettings switched = this.settings with { Engine = info.Engine };
            switched.SaveChangesFrom(this.settings);
            this.Settings = switched;
            status += info.Engine == ChatEngineKind.ClaudeCode ? " Switched to Claude Code to continue it." : " Switched to the Claude API to continue it.";
        }

        this.Status = status;
        this.ShowHistory = false;
        this.PromptSent?.Invoke(this, EventArgs.Empty); // scroll to the end of the opened chat
    }

    /// <summary>Reopens the most recent chat for the current repository, so a chat carries over when Visual Studio restarts.</summary>
    /// <remarks>Every saved chat's summary and then the transcript are read on a background thread, so the window opens straight away.</remarks>
    public async Task RestoreLastSessionAsync()
    {
        if (this.Messages.Count > 0 || this.engine != null)
        {
            return;
        }

        string repo = this.RepoPath.Trim().TrimEnd('\\', '/');
        ChatEngineKind engineKind = this.settings.Engine;
        (SessionInfo? last, List<SavedEntry>? entries) = await Task.Run(() =>
        {
            SessionInfo? found = SessionStore.List().FirstOrDefault(s =>
                string.Equals(s.RepoPath.TrimEnd('\\', '/'), repo, StringComparison.OrdinalIgnoreCase) && s.Engine == engineKind);
            return (found, found is null ? null : SessionStore.LoadEntries(found.Id));
        });

        // Unless something was started (or opened) meanwhile.
        if (last != null && entries != null && this.Messages.Count == 0 && this.engine == null && this.chatId == null)
        {
            this.ShowSession(new SessionItem(last, isCurrent: false), entries);
        }
    }

    public void DeleteSession(SessionItem item) => this.DeleteChats([item]);

    /// <summary>True while a search or the repository filter is hiding some chats.</summary>
    public bool IsSessionListFiltered => this.sessionSearch.Trim().Length > 0 || this.thisRepoOnly;

    /// <summary>Deletes every unpinned chat the History tab is showing (all of them, unless it's filtered).</summary>
    public void DeleteShownSessions() => this.DeleteChats(this.Sessions.Where(s => !s.IsPinned).ToList());

    /// <summary>
    /// Deletes chats. A chat open in a window (this one or another) is deleted by that window once its reply, if any,
    /// has stopped, and the window starts afresh; otherwise its next save would bring the chat back.
    /// </summary>
    private void DeleteChats(IReadOnlyList<SessionItem> items)
    {
        foreach (IGrouping<ClaudeChatViewModel, SessionItem> group in items.GroupBy(i => OwnerOf(i.Info.Id) ?? this))
        {
            ClaudeChatViewModel owner = group.Key;
            List<string> ids = group.Select(i => i.Info.Id).ToList();
            void Delete()
            {
                foreach (string id in ids)
                {
                    SessionStore.Delete(id);
                }

                if (owner.chatId != null && ids.Contains(owner.chatId))
                {
                    owner.ResetChat();
                    if (owner == this)
                    {
                        owner.ShowHistory = true;
                    }
                    else
                    {
                        owner.Status = "This chat was deleted in another Claude window.";
                    }
                }

                RefreshSessionLists(this);
            }

            // Only a reply in the deleted chat needs stopping.
            if (owner.chatId != null && ids.Contains(owner.chatId))
            {
                owner.WhenIdle(Delete);
            }
            else
            {
                Delete();
            }
        }
    }

    /// <summary>The window that has this chat open, if any (this one included).</summary>
    private static ClaudeChatViewModel? OwnerOf(string id) => Instances.FirstOrDefault(c => c.chatId == id);

    /// <summary>Refreshes the History list in this window, and in any other window showing it.</summary>
    private static void RefreshSessionLists(ClaudeChatViewModel current)
    {
        foreach (ClaudeChatViewModel chat in Instances.Where(c => c == current || c.ShowHistory))
        {
            chat.RefreshSessions();
        }
    }

    /// <summary>
    /// Changes a chat's summary from the History tab. If a window has the chat open, its own summary is used (the list's
    /// can be out of date, e.g. its Claude Code session) and it learns the change, since it saves the summary again.
    /// </summary>
    private void ChangeInfo(SessionItem item, Func<SessionInfo, SessionInfo> change, Action<ClaudeChatViewModel> tellOwner, string failure)
    {
        ClaudeChatViewModel? owner = OwnerOf(item.Info.Id);
        SessionInfo info = owner?.CurrentInfo() is SessionInfo current ? current with { Updated = item.Info.Updated } : item.Info;
        try
        {
            SessionStore.SaveInfo(change(info));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Status = failure + ex.Message;
            return;
        }

        if (owner != null)
        {
            tellOwner(owner);
        }

        RefreshSessionLists(this);
    }

    public void TogglePin(SessionItem item)
    {
        bool pinned = !item.IsPinned;
        this.ChangeInfo(item, info => info with { Pinned = pinned }, owner => owner.chatPinned = pinned, "Couldn't pin this chat: ");
    }

    public void StartRename(SessionItem item)
    {
        foreach (SessionItem other in this.Sessions.Where(s => s.IsRenaming))
        {
            other.IsRenaming = false;
        }

        item.EditTitle = item.Title;
        item.IsRenaming = true;
    }

    public void CommitRename(SessionItem item)
    {
        string title = string.Join(" ", item.EditTitle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        item.IsRenaming = false;
        if (title.Length == 0 || title == item.Title)
        {
            return;
        }

        this.ChangeInfo(item, info => info with { Title = title }, owner => owner.chatTitle = title, "Couldn't rename this chat: ");
    }

    /// <summary>Saves a chat from the History tab as Markdown.</summary>
    public void ExportSession(SessionItem item)
    {
        if (SessionStore.LoadEntries(item.Info.Id) is not { } entries)
        {
            this.Status = "This chat's saved transcript couldn't be read.";
            return;
        }

        this.Export(item.Info, entries);
    }

    /// <summary>Saves the current chat as Markdown.</summary>
    public void ExportCurrentChat()
    {
        if (!this.Messages.Any(m => m.Kind == ChatEntryKind.User))
        {
            this.Status = "There's nothing to export yet.";
            return;
        }

        this.SaveChat();
        this.Export(this.CurrentInfo()!, this.SavedEntries());
    }

    private void Export(SessionInfo info, IReadOnlyList<SavedEntry> entries)
    {
        if (this.ChooseExportPath(ChatExporter.SuggestedFileName(info)) is not string path)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, ChatExporter.ToMarkdown(info, entries));
            this.Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Status = "Couldn't save the export: " + ex.Message;
        }
    }

    /// <summary>Stars or unstars a message, and saves the chat so the star is kept.</summary>
    public void ToggleStar(ChatEntry entry)
    {
        if (!entry.CanStar)
        {
            return;
        }

        entry.IsStarred = !entry.IsStarred;
        this.OnPropertyChanged(nameof(this.HasStarred));
        this.SaveChat();
    }

    public bool HasStarred => this.Messages.Any(m => m.IsStarred);

    /// <summary>The starred messages, in the order they appear in the chat.</summary>
    public IReadOnlyList<ChatEntry> StarredMessages() => this.Messages.Where(m => m.IsStarred).ToList();

    /// <summary>
    /// Raised when a chat has been branched: the new saved chat, and the text of the message it was branched from, which
    /// the new window puts in its prompt box.
    /// </summary>
    public event EventHandler<(SessionInfo Info, string Prompt)>? BranchRequested;

    /// <summary>
    /// Saves a copy of the chat as it was just before a message, to continue in another window, with that message waiting
    /// in its prompt box. This chat is left as it is, so both ways forward can be tried.
    /// </summary>
    public void BranchFrom(ChatEntry prompt)
    {
        int index = this.Messages.IndexOf(prompt);
        if (this.IsBusy || index < 0 || prompt.Kind != ChatEntryKind.User)
        {
            return;
        }

        this.SaveChat();
        if (this.CurrentInfo() is not SessionInfo current)
        {
            return;
        }

        List<ChatEntry> before = this.Messages.Take(index).ToList();
        string title = "Branch: " + current.Title;
        SessionInfo info = current with
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title.Length > 80 ? title.Substring(0, 80) + "…" : title,
            Created = DateTimeOffset.Now,
            Updated = DateTimeOffset.Now,
            MessageCount = before.Count(m => m.Kind is ChatEntryKind.User or ChatEntryKind.Assistant && !m.IsLocalReply),
            Pinned = false,
            ClaudeCodeSessionId = null,
            ClaudeCodeLastMessageId = null,
            ClaudeCodeResumeAt = null,
        };

        if (current.Engine == ChatEngineKind.ClaudeCode && prompt.Checkpoint is not { Length: 0 })
        {
            // Claude Code forks its own session at that message when the branch sends its first one.
            string[] parts = prompt.Checkpoint?.Split('|') ?? [];
            if (parts.Length != 2)
            {
                this.Status = "This message can't be branched from: it was sent by an older version of the extension, or the conversation has changed since.";
                return;
            }

            info = info with { ClaudeCodeSessionId = parts[0], ClaudeCodeLastMessageId = parts[1], ClaudeCodeResumeAt = prompt.Checkpoint };
        }

        try
        {
            SessionStore.Save(info, this.SavedEntries(before));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Status = "Couldn't save the branch: " + ex.Message;
            return;
        }

        this.Status = "Branched into a new window. This chat is unchanged.";
        this.BranchRequested?.Invoke(this, (info, prompt.Text));
    }

    /// <summary>Gives a freshly created engine the context of the chat that was opened from History.</summary>
    private void ApplyResume(IChatEngine created)
    {
        SessionInfo? info = this.resumeFrom;
        this.resumeFrom = null;
        if (info is null || !string.Equals(info.RepoPath, created.RepoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (created is ClaudeCodeEngine code)
        {
            code.SessionId = info.ClaudeCodeSessionId;
            code.LastMessageId = info.ClaudeCodeLastMessageId;
            if (info.ClaudeCodeResumeAt != null)
            {
                code.Rewind(info.ClaudeCodeResumeAt);
            }
        }
        else if (created is ClaudeAgent agent)
        {
            agent.RestoreHistory(this.Messages
                .Where(m => m.Kind is ChatEntryKind.User or ChatEntryKind.Assistant && !m.IsLocalReply)
                .Select(m => (m.Kind == ChatEntryKind.User, m.Text)));
        }
    }
}
