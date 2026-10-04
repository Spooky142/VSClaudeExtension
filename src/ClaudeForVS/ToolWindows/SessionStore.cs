using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

namespace ClaudeForVS.ToolWindows;

/// <summary>Summary of a saved chat: everything the History tab shows, kept apart from the (larger) transcript.</summary>
internal sealed record SessionInfo
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string RepoPath { get; init; } = string.Empty;

    public ChatEngineKind Engine { get; init; }

    public ClaudeModel Model { get; init; }

    public DateTimeOffset Created { get; init; }

    public DateTimeOffset Updated { get; init; }

    public int MessageCount { get; init; }

    /// <summary>Pinned chats are listed first and kept by "Delete all".</summary>
    public bool Pinned { get; init; }

    /// <summary>The Claude Code session to <c>--resume</c>; null for chats that ran on the Claude API.</summary>
    public string? ClaudeCodeSessionId { get; init; }

    /// <summary>The latest message in the Claude Code session, so prompts can still be edited after reopening.</summary>
    public string? ClaudeCodeLastMessageId { get; init; }

    /// <summary>A rewind (a Claude Code checkpoint) still to be carried out by the next message.</summary>
    public string? ClaudeCodeResumeAt { get; init; }
}

/// <summary>One transcript entry as saved to disk.</summary>
internal sealed record SavedEntry
{
    public ChatEntryKind Kind { get; init; }

    public string Header { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public ActivityKind Activity { get; init; }

    // User prompts only; see the matching ChatEntry properties.
    public string? Note { get; init; }

    public string? Checkpoint { get; init; }

    public string? SnapshotBefore { get; init; }

    public string? SnapshotAfter { get; init; }

    /// <summary>Set on the files-changed row under a reply, which is saved as a tool row; see <see cref="ChatEntryKind.Changes"/>.</summary>
    public IReadOnlyList<FileStat>? Files { get; init; }
}

/// <summary>
/// Chats saved under %LOCALAPPDATA%\ClaudeForVS\sessions: a small <c>id.meta.json</c> per chat for the History
/// list and a <c>id.chat.json</c> with its transcript, which is only read when the chat is opened.
/// </summary>
internal static class SessionStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "sessions");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(SessionInfo info, IReadOnlyList<SavedEntry> entries)
    {
        AtomicFile.WriteAllText(ChatPath(info.Id), JsonSerializer.Serialize(entries, JsonOptions));
        SaveInfo(info); // last: the list only shows chats that are complete
    }

    /// <summary>Rewrites only a chat's summary, e.g. after renaming it.</summary>
    public static void SaveInfo(SessionInfo info)
        => AtomicFile.WriteAllText(MetaPath(info.Id), JsonSerializer.Serialize(info, JsonOptions));

    // Each searched chat's transcript text, as of the chat's last update, so searching again doesn't re-read every file.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Updated, string Text)> SearchText = new(StringComparer.Ordinal);

    /// <summary>True if the chat's saved transcript contains the text (case-insensitive). Safe to call off the UI thread.</summary>
    public static bool TranscriptContains(SessionInfo info, string text)
    {
        if (!SearchText.TryGetValue(info.Id, out var cached) || cached.Updated != info.Updated)
        {
            try
            {
                cached = (info.Updated, LoadEntries(info.Id) is { } entries ? string.Join("\n", entries.Select(e => e.Text)) : string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cached = (info.Updated, string.Empty);
            }

            SearchText[info.Id] = cached;
        }

        return cached.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Every saved chat, most recently used first. Unreadable files are skipped.</summary>
    public static List<SessionInfo> List()
    {
        var sessions = new List<SessionInfo>();
        if (!Directory.Exists(Folder))
        {
            return sessions;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(Folder, "*.meta.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(file), JsonOptions) is { Id.Length: > 0 } info)
                    {
                        sessions.Add(info);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return sessions.OrderByDescending(s => s.Updated).ToList();
    }

    /// <summary>The saved transcript, or null if it is missing or unreadable.</summary>
    public static List<SavedEntry>? LoadEntries(string id)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SavedEntry>>(File.ReadAllText(ChatPath(id)), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string id)
    {
        SearchText.TryRemove(id, out _);
        foreach (string path in new[] { MetaPath(id), ChatPath(id) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string MetaPath(string id) => Path.Combine(Folder, id + ".meta.json");

    private static string ChatPath(string id) => Path.Combine(Folder, id + ".chat.json");
}

/// <summary>One row in the History tab.</summary>
internal sealed class SessionItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool isRenaming;
    private string editTitle = string.Empty;

    public SessionItem(SessionInfo info, bool isCurrent)
    {
        this.Info = info;
        this.IsCurrent = isCurrent;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public SessionInfo Info { get; }

    public bool IsCurrent { get; }

    public string Title => this.Info.Title;

    public bool IsPinned => this.Info.Pinned;

    /// <summary>The pin button's glyph: unpin when pinned, pin otherwise.</summary>
    public string PinGlyph => this.Info.Pinned ? "" : "";

    public string PinToolTip => this.Info.Pinned ? "Unpin" : "Pin to the top";

    /// <summary>True while the title is being edited in place.</summary>
    public bool IsRenaming
    {
        get => this.isRenaming;
        set
        {
            this.isRenaming = value;
            this.PropertyChanged?.Invoke(this, new(nameof(this.IsRenaming)));
            this.PropertyChanged?.Invoke(this, new(nameof(this.IsNotRenaming)));
        }
    }

    public bool IsNotRenaming => !this.isRenaming;

    /// <summary>The title being typed while renaming.</summary>
    public string EditTitle
    {
        get => this.editTitle;
        set
        {
            this.editTitle = value;
            this.PropertyChanged?.Invoke(this, new(nameof(this.EditTitle)));
        }
    }

    public string Subtitle =>
        $"{Path.GetFileName(this.Info.RepoPath.TrimEnd('\\', '/'))} · {AgentSettings.ModelName(this.Info.Model)}"
        + (this.Info.Engine == ChatEngineKind.ClaudeCode ? " · Claude Code" : string.Empty)
        + $" · {this.Info.MessageCount} {(this.Info.MessageCount == 1 ? "message" : "messages")}";

    public string TimeText
    {
        get
        {
            TimeSpan age = DateTimeOffset.Now - this.Info.Updated;
            return age.TotalMinutes < 1 ? "Just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
                : age.TotalDays < 2 ? "Yesterday"
                : this.Info.Updated.LocalDateTime.ToString("d MMM yyyy");
        }
    }

    public string Tooltip => this.Info.RepoPath;
}
