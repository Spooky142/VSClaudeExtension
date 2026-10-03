using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeForVS.Agent;

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

    /// <summary>The Claude Code session to <c>--resume</c>; null for chats that ran on the Claude API.</summary>
    public string? ClaudeCodeSessionId { get; init; }
}

/// <summary>One transcript entry as saved to disk.</summary>
internal sealed record SavedEntry
{
    public ChatEntryKind Kind { get; init; }

    public string Header { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public ActivityKind Activity { get; init; }
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
        Directory.CreateDirectory(Folder);
        File.WriteAllText(ChatPath(info.Id), JsonSerializer.Serialize(entries, JsonOptions));
        File.WriteAllText(MetaPath(info.Id), JsonSerializer.Serialize(info, JsonOptions)); // last: the list only shows chats that are complete
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
internal sealed class SessionItem
{
    public SessionItem(SessionInfo info, bool isCurrent)
    {
        this.Info = info;
        this.IsCurrent = isCurrent;
    }

    public SessionInfo Info { get; }

    public bool IsCurrent { get; }

    public string Title => this.Info.Title;

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
