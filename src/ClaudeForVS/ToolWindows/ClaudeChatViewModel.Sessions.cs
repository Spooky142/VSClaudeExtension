using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
            this.RefreshSessions();
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

    public void RefreshSessions()
    {
        this.Sessions.Clear();
        List<SessionInfo> all = SessionStore.List();
        string query = this.sessionSearch.Trim();
        string repo = (this.RepoRoot ?? this.RepoPath).Trim().TrimEnd('\\', '/');
        foreach (SessionInfo info in all.OrderByDescending(s => s.Pinned)) // stable: newest first within each group
        {
            if (this.thisRepoOnly && !string.Equals(info.RepoPath.TrimEnd('\\', '/'), repo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Length > 0
                && info.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && Path.GetFileName(info.RepoPath.TrimEnd('\\', '/')).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && !(query.Length >= 3 && SessionStore.TranscriptContains(info.Id, query)))
            {
                continue;
            }

            this.Sessions.Add(new SessionItem(info, info.Id == this.chatId));
        }

        this.NoSavedSessions = all.Count == 0;
        this.NoSessionMatches = all.Count > 0 && this.Sessions.Count == 0;
        this.OnPropertyChanged(nameof(this.HasSessions));
        this.OnPropertyChanged(nameof(this.NoSavedSessions));
        this.OnPropertyChanged(nameof(this.NoSessionMatches));
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

    private List<SavedEntry> SavedEntries() => this.Messages
        .Select(m => new SavedEntry
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

        try
        {
            SessionStore.Save(this.CurrentInfo()!, this.SavedEntries());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
    public void OpenSession(SessionItem item)
    {
        if (this.IsBusy)
        {
            this.Status = "Stop the running prompt before opening another chat.";
            return;
        }

        SessionInfo info = item.Info;
        if (!Directory.Exists(info.RepoPath))
        {
            this.Status = $"The repository for this chat no longer exists: {info.RepoPath}";
            return;
        }

        if (SessionStore.LoadEntries(info.Id) is not { } entries)
        {
            this.Status = "This chat's saved transcript couldn't be read.";
            return;
        }

        this.DisposeEngine();
        this.Messages.Clear();
        foreach (SavedEntry e in entries)
        {
            // Chats saved by 0.3.9-0.3.10 kept the token count in the header.
            this.Messages.Add(e.Kind == ChatEntryKind.Usage && e.Text.Length == 0
                ? new ChatEntry(e.Kind, string.Empty, e.Header)
                : new ChatEntry(e.Kind, e.Header, e.Text, e.Detail, e.Activity)
                {
                    Note = e.Note ?? string.Empty,
                    Checkpoint = e.Checkpoint,
                    SnapshotBefore = e.SnapshotBefore,
                    SnapshotAfter = e.SnapshotAfter,
                });
        }

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
            switched.Save();
            this.Settings = switched;
            status += info.Engine == ChatEngineKind.ClaudeCode ? " Switched to Claude Code to continue it." : " Switched to the Claude API to continue it.";
        }

        this.Status = status;
        this.ShowHistory = false;
        this.PromptSent?.Invoke(this, EventArgs.Empty); // scroll to the end of the opened chat
    }

    /// <summary>Reopens the most recent chat for the current repository, so a chat carries over when Visual Studio restarts.</summary>
    public void RestoreLastSession()
    {
        if (this.Messages.Count > 0 || this.engine != null)
        {
            return;
        }

        string repo = this.RepoPath.Trim().TrimEnd('\\', '/');
        SessionInfo? last = SessionStore.List().FirstOrDefault(s =>
            string.Equals(s.RepoPath.TrimEnd('\\', '/'), repo, StringComparison.OrdinalIgnoreCase)
            && s.Engine == this.settings.Engine);
        if (last != null)
        {
            this.OpenSession(new SessionItem(last, isCurrent: false));
        }
    }

    public void DeleteSession(SessionItem item)
    {
        SessionStore.Delete(item.Info.Id);
        if (item.Info.Id == this.chatId)
        {
            this.NewChat();
            this.ShowHistory = true;
        }

        this.RefreshSessions();
    }

    /// <summary>True while a search or the repository filter is hiding some chats.</summary>
    public bool IsSessionListFiltered => this.sessionSearch.Trim().Length > 0 || this.thisRepoOnly;

    /// <summary>Deletes every unpinned chat the History tab is showing (all of them, unless it's filtered).</summary>
    public void DeleteShownSessions()
    {
        bool deletedCurrent = false;
        foreach (SessionItem item in this.Sessions.Where(s => !s.IsPinned).ToList())
        {
            SessionStore.Delete(item.Info.Id);
            deletedCurrent |= item.Info.Id == this.chatId;
        }

        if (deletedCurrent)
        {
            this.NewChat();
            this.ShowHistory = true;
        }

        this.RefreshSessions();
    }

    public void TogglePin(SessionItem item)
    {
        bool pinned = !item.IsPinned;
        try
        {
            SessionStore.SaveInfo(item.Info with { Pinned = pinned });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Status = "Couldn't pin this chat: " + ex.Message;
            return;
        }

        if (item.Info.Id == this.chatId)
        {
            this.chatPinned = pinned;
        }

        this.RefreshSessions();
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

        try
        {
            SessionStore.SaveInfo(item.Info with { Title = title });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Status = "Couldn't rename this chat: " + ex.Message;
            return;
        }

        if (item.Info.Id == this.chatId)
        {
            this.chatTitle = title;
        }

        this.RefreshSessions();
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
